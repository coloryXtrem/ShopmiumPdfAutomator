using System.Text.RegularExpressions;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Scoring de correspondance produit — fiable pour produits français Shopmium.
    ///
    /// Score de 0 à 100 :
    ///   ≥ 55 → correspondance acceptable (SEUIL_MIN)
    ///   ≥ 70 → correspondance forte     (SEUIL_BON)
    ///
    /// Algorithme :
    ///   40 pts — marque (1er mot ou mot commun dominant)
    ///   30 pts — mots-clés significatifs communs
    ///   30 pts — quantité (nombre + unité)
    ///   Règle bloquante : variante différente (rouge/rosé, nature/vanille…) → 15 pts
    ///   Règle bloquante : quantité (nombre de portions) différente → 8 pts
    /// </summary>
    public static class ProductMatcher
    {
        public const int SEUIL_MIN = 55;
        public const int SEUIL_BON = 70;

        public record ScoredCandidate(string Name, string Url, string? Ean, int Score);

        // Groupes de variantes exclusives : si query a l'un et candidat a l'autre → conflit
        private static readonly HashSet<string>[] VariantGroups =
        [
            new() { "rouge", "rose", "blanc", "brut", "sec", "demi-sec", "moelleux" },
            new() { "nature", "vanille", "fraise", "framboise", "peche", "mangue", "citron", "abricot" },
            new() { "entier", "ecreme", "allege", "demi-ecreme" },
            new() { "light", "zero", "original" },
        ];

        // ── Score principal ───────────────────────────────────────────────────
        public static int Score(string query, string candidate)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
                return 0;

            var q = Normalize(query);
            var c = Normalize(candidate);
            if (q == c) return 100;

            var qTokens = ExtractTokens(q);
            var cTokens = ExtractTokens(c);
            var qQty    = ExtractQtyPortions(q);
            var cQty    = ExtractQtyPortions(c);

            // ── Règle bloquante 1 : quantité (portions/parts) différente ─────
            if (qQty.HasValue && cQty.HasValue && qQty.Value != cQty.Value)
                return 8;

            // ── Règle bloquante 2 : variante conflictuelle ───────────────────
            foreach (var group in VariantGroups)
            {
                var qHas = qTokens.Intersect(group).ToHashSet();
                var cHas = cTokens.Intersect(group).ToHashSet();
                if (qHas.Any() && cHas.Any() && !qHas.SetEquals(cHas))
                    return 15;
            }

            int score = 0;

            // ── 1. Marque (40 pts) ────────────────────────────────────────────
            var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cWords = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (qWords.Length > 0 && cWords.Length > 0)
            {
                var brand = qWords[0];
                if (brand.Length >= 3 && cWords.Contains(brand))
                    score += 40;
                else if (brand.Length >= 3 &&
                         cWords.Any(w => w.StartsWith(brand[..Math.Min(4, brand.Length)])))
                    score += 25;
            }

            // ── 2. Mots significatifs communs (30 pts) ────────────────────────
            if (qTokens.Count > 0)
                score += (int)((double)qTokens.Count(w => cTokens.Contains(w))
                               / qTokens.Count * 30);

            // ── 3. Quantité (30 pts) ──────────────────────────────────────────
            if (qQty.HasValue && cQty.HasValue && qQty.Value == cQty.Value)
                score += 30;
            else
                score += 10; // pas de quantité détectée → neutre

            return Math.Max(0, Math.Min(100, score));
        }

        // ── Sélectionner le meilleur candidat ────────────────────────────────
        public static ScoredCandidate? BestMatch(
            string query,
            IEnumerable<(string name, string url, string? ean)> candidates,
            int minScore = SEUIL_MIN)
        {
            var scored = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.name))
                .Select(c => new ScoredCandidate(c.name, c.url, c.ean, Score(query, c.name)))
                .OrderByDescending(c => c.Score)
                .ToList();

            foreach (var s in scored.Take(5))
                System.Diagnostics.Debug.WriteLine(
                    $"[Matcher] {s.Score,3}/100 | {s.Name[..Math.Min(80, s.Name.Length)]}");

            return scored.FirstOrDefault(c => c.Score >= minScore);
        }

        public static HashSet<double> GetNumbers(string text) =>
            new(Regex.Matches(Normalize(text), @"\b(\d+)\b")
                .Select(m => double.Parse(m.Groups[1].Value)));

        public static string GetNormalized(string text) => Normalize(text);

        // ── Helpers ───────────────────────────────────────────────────────────
        private static string Normalize(string s)
        {
            s = s.ToLowerInvariant()
                 .Replace("é","e").Replace("è","e").Replace("ê","e").Replace("ë","e")
                 .Replace("à","a").Replace("â","a").Replace("ä","a")
                 .Replace("ù","u").Replace("û","u").Replace("ü","u")
                 .Replace("î","i").Replace("ï","i")
                 .Replace("ô","o").Replace("ö","o")
                 .Replace("ç","c").Replace("œ","oe").Replace("æ","ae");

            // Synonymes de quantité
            s = Regex.Replace(s, @"\bparts?\b",          "portions");
            s = Regex.Replace(s, @"\bpcs?\b",            "portions");
            s = Regex.Replace(s, @"\bunites?\b",          "portions");
            s = Regex.Replace(s, @"\bmorceaux?\b",        "portions");
            s = Regex.Replace(s, @"\bfromage fondu\b",    "fromage");
            s = Regex.Replace(s, @"\ba la\b",             " ");
            s = Regex.Replace(s, @"\bau\b",               " ");

            // Retirer les grammages purs (216g, 750ml…) qui polluent la comparaison des quantités
            s = Regex.Replace(s, @"\b\d+\s*(?:g|ml|cl|kg|l)\b", " ");

            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static HashSet<string> ExtractTokens(string norm)
        {
            var stop = new HashSet<string>
            {
                "de","du","des","le","la","les","un","une","au","aux","et","en",
                "par","pour","sur","avec","sans","the","of","and","for","x","aoc"
            };
            return new HashSet<string>(
                Regex.Split(norm, @"[\s\-/,\.]+")
                    .Where(w => w.Length >= 3 && !stop.Contains(w)
                                && !Regex.IsMatch(w, @"^\d+$"))
            );
        }

        /// <summary>
        /// Extrait UNIQUEMENT la quantité exprimée en portions/tranches/etc.
        /// (pas les volumes ou poids qui varient selon le format)
        /// </summary>
        private static double? ExtractQtyPortions(string norm)
        {
            var m = Regex.Match(norm,
                @"(\d+(?:[,\.]\d+)?)\s*(?:x\s*)?(?:portions?|parts?|tranches?|sachets?|capsules?|tablettes?)\b",
                RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (double.TryParse(m.Groups[1].Value.Replace(",", "."),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
                return n;
            return null;
        }
    }
}
