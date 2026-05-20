using ShopmiumPdfAutomator.Models;
using System.Text.RegularExpressions;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Convertit une ShopmiumOffer (données API) en ProductData prêt pour Photoshop.
    /// Priorité : données API > règles locales > valeurs par défaut.
    /// </summary>
    public static class OfferAnalyzer
    {
        // ── Enseignes connues → type de ticket ────────────────────────────────
        private static readonly string[] _leclercKeys =
            ["leclerc", "e.leclerc", "e-leclerc"];
        private static readonly string[] _carrefourDriveKeys =
            ["carrefour drive", "carrefourdrive", "drive carrefour"];
        private static readonly string[] _carrefourKeys =
            ["carrefour", "carrefour market", "carrefour city", "carrefour express"];

        // ── Mapping kind/type API → ProofType local ───────────────────────────
        private static readonly Dictionary<string, ProofType> _kindMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["receipt"]               = ProofType.Ticket,
            ["ticket"]                = ProofType.Ticket,
            ["picture"]               = ProofType.PhotoArticles,
            ["photo"]                 = ProofType.PhotoArticles,
            ["crossed_barcode"]       = ProofType.PhotoBarcodeRaye,
            ["barcode_crossed"]       = ProofType.PhotoBarcodeRaye,
            ["crossed_barcode_photo"] = ProofType.PhotoBarcodeRaye,
            ["packaging"]             = ProofType.PhotoEmballage,
            ["package"]               = ProofType.PhotoEmballage,
            ["price_tag"]             = ProofType.PhotoEtiquettePrix,
            ["label_price"]           = ProofType.PhotoEtiquettePrix,
            ["product_outside"]       = ProofType.PhotoProduitHorsEmballage,
            ["cut_packaging"]         = ProofType.PhotoEmballageDecoupe,
            ["articles_and_ticket"]   = ProofType.PhotoArticlesEtTicket,
        };

        /// <summary>
        /// Analyse une offre et retourne un ProductData complet prêt à générer.
        /// Règles imposées par l'utilisateur (depuis le début) :
        ///   - PRIX : toujours le PLUS ÉLEVÉ parmi les produits éligibles
        ///   - QUANTITÉ : toujours la PLUS ÉLEVÉE
        ///   - NOM : produit correspondant au prix le plus élevé
        ///   - DATE : première date affichée
        ///   - HEURE : aléatoire
        /// Tout est extrait automatiquement, AUCUNE saisie manuelle.
        /// </summary>
        public static ProductData Analyze(ShopmiumOffer offer)
        {
            var data = new ProductData();

            // ─── Parser conditions.content (le seul endroit qui liste les vraies refs) ──
            var conditionsText = offer.Presentation?.Detail?.Conditions?.Content ?? "";
            var outlineText    = offer.Presentation?.Detail?.Outline?.Content ?? "";
            var allText        = conditionsText + "\n" + outlineText;

            // 1) Tenter d'extraire la liste des références éligibles
            //    Format A : "- Nom (4,80€)"   (Boulettes au bœuf)
            //    Format B : "- Nom"           (Apéricube, pas de prix individuel)
            var refsWithPrices = ParseEligibleRefsFromText(conditionsText);
            var refsWithoutPrices = refsWithPrices.Count == 0
                ? ParseEligibleRefNamesFromText(conditionsText)
                : new List<string>();

            // ─── PRIX (le PLUS ÉLEVÉ) + nom du produit correspondant ────────────
            string? bestProductName = null;
            double  bestPrice = 0;
            int     bestIndex = -1;

            // Priorité 1 : conditions avec prix individuels → prendre le MAX
            if (refsWithPrices.Count > 0)
            {
                var ordered = refsWithPrices
                    .Select((r, i) => (r.name, r.price, i))
                    .OrderByDescending(x => x.price)
                    .ToList();
                bestProductName = ordered[0].name;
                bestPrice       = ordered[0].price;
                bestIndex       = ordered[0].i;
            }

            // Priorité 2 : pas de prix individuels MAIS la liste des refs existe
            //              → prix vient du strikethrough (potentiellement un range)
            //              → on garde TOUTES les refs avec Price=0 pour que le panneau
            //                de choix manuel s'affiche (NeedsManualRefChoice = true).
            //                AUCUNE sélection automatique.
            bool needsManualChoice = false;
            if (bestPrice <= 0)
            {
                var s = offer.Presentation?.Detail?.Price?.Strikethrough ?? "";
                var maxFromRange = ExtractMaxEuroFromRange(s);
                if (maxFromRange > 0) bestPrice = maxFromRange;

                if (refsWithoutPrices.Count > 1)
                {
                    // Marquer pour choix manuel
                    needsManualChoice = true;
                    // Nom temporaire = nom de l'offre (sera remplacé après le clic utilisateur)
                    bestProductName = null;
                }
                else if (refsWithoutPrices.Count == 1)
                {
                    // Une seule ref → pas de choix à faire
                    bestProductName = refsWithoutPrices[0];
                }
            }

            // Fallback : prix produit API
            if (bestPrice <= 0)
            {
                var pp = offer.Products
                    .Where(p => p.Price.HasValue && p.Price > 0)
                    .Select(p => p.Price!.Value)
                    .OrderByDescending(p => p)
                    .FirstOrDefault();
                if (pp > 0) bestPrice = pp;
            }

            // Tout dernier fallback
            if (bestPrice <= 0)
            {
                bestPrice = offer.MaxPrice
                    ?? (offer.EffectiveRebate > 0 ? offer.EffectiveRebate * 2.5 : 3.99);
            }

            // ─── NOM DU PRODUIT ────────────────────────────────────────────────
            // Priorité (du plus précis au plus générique) :
            //   1. Nom de la référence éligible (extrait de conditions.content)
            //   2. offer.Name : nom propre exposé à l'utilisateur (ex: "La Vache qui rit")
            //   3. Nom d'un product[] s'il a un name (rare)
            //   4. short_name : NE JAMAIS utiliser (interne Shopmium type "BEL - X - V1 - Avril2026")
            data.ProductName = SanitizeProductName(
                bestProductName
                ?? offer.Name
                ?? offer.Products.FirstOrDefault(p => !string.IsNullOrEmpty(p.Name))?.Name
                ?? "");

            data.MaxPrice = bestPrice;

            // ─── QUANTITÉ (la PLUS ÉLEVÉE) ─────────────────────────────────────
            var qtyCandidates = new List<int>();
            if (offer.ItemMax.HasValue && offer.ItemMax.Value > 0) qtyCandidates.Add(offer.ItemMax.Value);
            if (offer.ItemMin.HasValue && offer.ItemMin.Value > 0) qtyCandidates.Add(offer.ItemMin.Value);
            var subSel = offer.Submission?.ProductSelection;
            if (subSel?.ItemsMax.HasValue == true && subSel.ItemsMax.Value > 0) qtyCandidates.Add(subSel.ItemsMax.Value);
            if (subSel?.ItemsMin.HasValue == true && subSel.ItemsMin.Value > 0) qtyCandidates.Add(subSel.ItemsMin.Value);
            if (offer.MaxQuantity.HasValue && offer.MaxQuantity.Value > 0) qtyCandidates.Add(offer.MaxQuantity.Value);
            var outlineQty = ParseQuantityFromText(allText);
            if (outlineQty > 0) qtyCandidates.Add(outlineQty);

            data.MaxArticles = qtyCandidates.Any() ? qtyCandidates.Max() : 1;
            if (data.MaxArticles < 1) data.MaxArticles = 1;

            // ─── MARQUE (customer_brands API) ──────────────────────────────────
            data.Brand = offer.BrandName
                ?? offer.Products.FirstOrDefault(p => !string.IsNullOrEmpty(p.Brand))?.Brand;

            // ─── EAN ────────────────────────────────────────────────────────────
            // Si on a un bestIndex et que products[] a le même nombre d'éléments
            // que la liste de refs → on prend l'EAN à ce même index.
            // Sinon, on prend le 1er EAN dispo.
            var allBarcodes = offer.Products
                .Where(p => !string.IsNullOrEmpty(p.Ean))
                .Select(p => p.Ean!)
                .ToList();

            if (bestIndex >= 0 && bestIndex < allBarcodes.Count
                && (refsWithPrices.Count == allBarcodes.Count
                 || refsWithoutPrices.Count == allBarcodes.Count))
            {
                data.BarcodeEan = allBarcodes[bestIndex];
            }
            else
            {
                data.BarcodeEan = allBarcodes.FirstOrDefault();
            }

            // ─── DATE DE DÉBUT (depuis date_conditions ou conditions.content) ──
            var dateLabel = offer.Presentation?.Detail?.DateConditions?.Labels?.Displayed
                         ?? offer.Presentation?.Detail?.DateConditions?.Labels?.StillSubmittable
                         ?? "";
            var startDate = ParseFirstDateFromText(dateLabel + " " + conditionsText);
            data.StartDate = !string.IsNullOrEmpty(startDate)
                ? startDate
                : DateTime.Now.ToString("dd/MM/yyyy");

            // ─── HEURE ALÉATOIRE ───────────────────────────────────────────────
            var rngTime = new Random();
            data.TimeHHMM = $"{rngTime.Next(8, 21):D2}:{rngTime.Next(0, 60):D2}";

            // ─── App exclusive (scan barcode requis ?) ──────────────────────────
            data.IsAppExclusive = offer.IsRemote == true
                || string.Equals(offer.VerificationType, "in_app", StringComparison.OrdinalIgnoreCase)
                || string.Equals(subSel?.Method, "scan", StringComparison.OrdinalIgnoreCase);

            // ─── Type de ticket (depuis retailers + texte conditions) ──────────
            data.TicketType = DetectTicketType(offer, allText);

            // ─── Preuves requises (depuis submission.proofs_capture API) ───────
            data.ProofRequirements = DetectProofRequirements(offer);

            // ─── TVA (détection auto via TvaCalculator) ────────────────────────
            var rayonText = BuildRayonText(offer);
            data.RayonText = rayonText;
            var tvaResult  = TvaCalculator.DetectWithConfidence(data.ProductName, rayonText);
            data.TvaRate              = tvaResult.Rate;
            data.TvaNeedsConfirmation = !tvaResult.Certain;

            // ─── Image produit (product_image_url) ─────────────────────────────
            data.ProductImageUrl = offer.EffectiveImageUrl;
            if (!string.IsNullOrEmpty(offer.EffectiveImageUrl))
                data.AllImageUrls.Add(offer.EffectiveImageUrl);

            // ─── Toutes les références éligibles (avec prix + EAN + image) ────────
            // On enrichit toujours depuis offer.Products pour avoir Barcode/ImageUrl/ProductId.
            // Stratégie : si les refs viennent du texte (refsWithPrices / refsWithoutPrices),
            // on les matche avec offer.Products par nom pour récupérer EAN + image.
            // Sinon on part directement de offer.Products.

            // Index products par nom normalisé pour matcher rapidement
            var productByName = new Dictionary<string, ShopmiumProduct>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var p in offer.Products)
                if (!string.IsNullOrEmpty(p.Name))
                    productByName[p.Name.Trim()] = p;

            EligibleRef MakeRef(string name, double price)
            {
                // Chercher le produit Shopmium correspondant par nom
                ShopmiumProduct? match = null;
                productByName.TryGetValue(name.Trim(), out match);

                // Si pas de match exact par nom → fallback sur le produit
                // dont le nom contient une sous-chaîne commune
                if (match == null)
                    foreach (var kv in productByName)
                        if (kv.Key.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)
                            || name.Trim().Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                            { match = kv.Value; break; }

                return new EligibleRef
                {
                    Name      = name,
                    Price     = price,
                    Barcode   = match?.Barcode ?? match?.EanLegacy ?? "",
                    ImageUrl  = match?.ImageUrl,
                    ProductId = match?.Id ?? 0
                };
            }

            if (refsWithPrices.Count > 0)
            {
                data.AllEligibleRefs = refsWithPrices
                    .Select(r => MakeRef(r.name, r.price))
                    .ToList();
            }
            else if (refsWithoutPrices.Count > 0)
            {
                var priceForRefs = needsManualChoice ? 0 : bestPrice;
                data.AllEligibleRefs = refsWithoutPrices
                    .Select(n => MakeRef(n, priceForRefs))
                    .ToList();
            }
            else
            {
                // Source directe : offer.Products (cas le plus fiable, EAN garanti)
                data.AllEligibleRefs = offer.Products
                    .Where(p => !string.IsNullOrEmpty(p.Name))
                    .Select(p => new EligibleRef
                    {
                        Name      = p.Name,
                        Price     = p.Price ?? bestPrice,
                        Barcode   = p.Barcode ?? p.EanLegacy ?? "",
                        ImageUrl  = p.ImageUrl,
                        ProductId = p.Id
                    })
                    .ToList();
            }

            // ─── Conditions brutes pour debug / affichage ──────────────────────
            data.RawConditions = string.IsNullOrEmpty(conditionsText)
                ? (offer.EffectiveDescription ?? "")
                : conditionsText;

            return data;
        }

        // ── Helpers de parsing ────────────────────────────────────────────────

        /// <summary>
        /// Format A : "- Nom du produit (4,80€)" → liste (nom, prix).
        /// </summary>
        private static List<(string name, double price)> ParseEligibleRefsFromText(string text)
        {
            var refs = new List<(string name, double price)>();
            if (string.IsNullOrWhiteSpace(text)) return refs;

            var idx = text.IndexOf("éligible", StringComparison.OrdinalIgnoreCase);
            var zone = idx >= 0 ? text[idx..] : text;

            var rx = new Regex(
                @"[\-•·*]\s*([^\(\n\r]+?)\s*\(\s*([\d]+[.,]?[\d]*)\s*€?\s*\)",
                RegexOptions.IgnoreCase);

            foreach (Match m in rx.Matches(zone))
            {
                var name = m.Groups[1].Value.Trim().Trim('-', '•', '*', ' ');
                var priceStr = m.Groups[2].Value.Replace(',', '.');
                if (double.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var price)
                    && !string.IsNullOrEmpty(name))
                {
                    refs.Add((name, price));
                }
            }
            return refs;
        }

        /// <summary>
        /// Format B : "- Nom du produit" (sans prix) après "Référence(s) éligible(s)".
        /// </summary>
        private static List<string> ParseEligibleRefNamesFromText(string text)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return names;

            var idx = text.IndexOf("éligible", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return names;
            var zone = text[idx..];

            // Stopper le bloc à la première ligne vide ou à un nouveau paragraphe
            // qui commence par autre chose qu'un tiret
            var lines = zone.Split('\n');
            bool inBlock = false;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r', ' ', '\t');
                if (line.StartsWith("Référence", StringComparison.OrdinalIgnoreCase)
                 || line.StartsWith("éligible", StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = true;
                    continue;
                }
                if (!inBlock) continue;

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("-") || trimmed.StartsWith("•") || trimmed.StartsWith("·"))
                {
                    var name = trimmed.TrimStart('-', '•', '·', '*', ' ').Trim();
                    if (!string.IsNullOrEmpty(name) && name.Length > 2)
                        names.Add(name);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    // Ligne pleine qui ne commence pas par tiret → fin du bloc
                    if (names.Count > 0) break;
                }
            }
            return names;
        }

        /// <summary>
        /// Extrait le MAX d'une chaîne du type "1,68€ à 4,93€" ou "4,80€".
        /// </summary>
        private static double ExtractMaxEuroFromRange(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var rx = new Regex(@"([\d]+[.,][\d]+|[\d]+)\s*€");
            double max = 0;
            foreach (Match m in rx.Matches(s))
            {
                var v = m.Groups[1].Value.Replace(',', '.');
                if (double.TryParse(v, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    if (d > max) max = d;
                }
            }
            return max;
        }

        /// <summary>
        /// Cherche dans un texte la valeur la plus élevée de "X article(s) acheté(s)" / "sur X articles".
        /// </summary>
        private static int ParseQuantityFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var rx = new Regex(@"(\d+)\s*article", RegexOptions.IgnoreCase);
            int max = 0;
            foreach (Match m in rx.Matches(text))
                if (int.TryParse(m.Groups[1].Value, out var v) && v > max) max = v;
            return max;
        }

        /// <summary>
        /// Parse la première date au format JJ/MM/AAAA dans un texte.
        /// </summary>
        private static string ParseFirstDateFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var m = Regex.Match(text, @"(\d{1,2})/(\d{1,2})/(\d{4})");
            if (!m.Success) return "";
            return $"{int.Parse(m.Groups[1].Value):D2}/{int.Parse(m.Groups[2].Value):D2}/{m.Groups[3].Value}";
        }

        /// <summary>Parse "1,80€" ou "-4.80 €" → double.</summary>
        private static double ParseEuroAmount(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var c = s.Replace("€", "").Replace("-", "").Replace(" ", "")
                     .Replace(",", ".").Trim();
            return double.TryParse(c, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        /// <summary>
        /// Nettoie un nom produit pour le rendre lisible/naturel (utilisable dans un prompt).
        /// Retire les artefacts internes Shopmium :
        ///   - Préfixes de marque interne ("BEL - ", "DANONE - ", etc.) : trois caractères
        ///     maj. ou nom de marque suivi de " - "
        ///   - Suffixes type "V1", "V2", "V12" en début de segment
        ///   - Suffixes dates type "Avril2026", "Mai2026", "Janvier2026"
        ///   - Doubles tirets / espaces multiples
        /// </summary>
        public static string SanitizeProductName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var s = name.Trim();

            // 1) Retirer suffixes Vx (V1, V2, V12) entourés de tirets
            s = Regex.Replace(s, @"\s*-\s*V\d+\s*-\s*", " - ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*-\s*V\d+\s*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"^\s*V\d+\s*-\s*", "", RegexOptions.IgnoreCase);

            // 2) Retirer suffixes mois+année (Mai2026, Avril2026, Janvier2026, etc.)
            var months = @"(?:Janvier|F[eé]vrier|Mars|Avril|Mai|Juin|Juillet|Ao[uû]t|Septembre|Octobre|Novembre|D[eé]cembre)";
            s = Regex.Replace(s, $@"\s*-\s*{months}\s*\d{{4}}\s*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, $@"\s*-\s*{months}\s*\d{{4}}\s*-\s*", " - ", RegexOptions.IgnoreCase);

            // 3) Retirer un préfixe MARQUE en MAJUSCULES suivi de " - "
            //    Ex: "BEL - La Vache qui rit" → "La Vache qui rit"
            //    On ne retire QUE si le préfixe est tout-en-majuscules (donc clairement un code interne)
            //    et que le reste du nom commence par une majuscule normale.
            s = Regex.Replace(s, @"^[A-Z]{2,}(?:\s+[A-Z&]+)?\s*-\s+", "");

            // 4) Nettoyer espaces multiples et tirets restants en fin
            s = Regex.Replace(s, @"\s+", " ").Trim();
            s = Regex.Replace(s, @"\s*-\s*$", "").Trim();
            s = Regex.Replace(s, @"^\s*-\s*", "").Trim();

            return s;
        }

        // ── Détection type de ticket ──────────────────────────────────────────
        //
        // RÈGLE PRIORITAIRE (en accord avec l'utilisateur, depuis v112) :
        //   1. Carrefour standard
        //   2. Carrefour Drive
        //   3. Leclerc
        //   4. Autres enseignes compatibles
        //
        // → On RÉUTILISE EXACTEMENT la logique v112 (ShopmiumParser.DetectTicketTypeFromText)
        //   qui a fait ses preuves. Cette logique attend du texte/HTML : on lui passe
        //   la concaténation conditions.content + date_conditions.labels.displayed
        //   qui contient exactement les phrases analysées en v112.
        //
        private static TicketType DetectTicketType(ShopmiumOffer offer, string allText = "")
        {
            // Construire le texte à analyser : conditions + date_conditions + outline
            // (l'API renvoie ces 3 blocs, et le texte qu'on y trouve est très similaire
            //  à ce que v112 voyait dans le HTML)
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(offer.Presentation?.Detail?.Conditions?.Content ?? "");
            sb.AppendLine(offer.Presentation?.Detail?.DateConditions?.Labels?.Displayed ?? "");
            sb.AppendLine(offer.Presentation?.Detail?.DateConditions?.Labels?.StillSubmittable ?? "");
            sb.AppendLine(offer.Presentation?.Detail?.Outline?.Content ?? "");
            sb.AppendLine(allText ?? "");

            // Ajouter aussi les noms de retailers de l'API (au cas où le texte ne les mentionne pas)
            if (offer.Retailers != null)
            {
                foreach (var r in offer.Retailers)
                {
                    var n = r.Name ?? r.Key ?? r.Chain;
                    if (!string.IsNullOrEmpty(n))
                        sb.Append("Valable chez ").Append(n).AppendLine(".");
                }
            }

            var (type, _) = ShopmiumParser.DetectTicketTypeFromText(sb.ToString());
            return type;
        }

        // ── Détection preuves depuis submission.proofs_capture ────────────────
        private static List<ProofRequirement> DetectProofRequirements(ShopmiumOffer offer)
        {
            var result = new List<ProofRequirement>();
            var seenTypes = new HashSet<ProofType>();

            // 1. Nouveau format API : submission.proofs_capture[]
            //    On EXCLUT le purpose "receipt" (= ticket de caisse implicite,
            //    pas une exigence supplémentaire à montrer)
            var proofs = offer.Submission?.ProofsCapture;
            if (proofs != null && proofs.Count > 0)
            {
                foreach (var p in proofs.Where(p => p.Skippable != true))
                {
                    var pt = MapPurposeToProofType(p.Purpose, p.Overlay);
                    if (pt == ProofType.None) continue;
                    if (pt == ProofType.Ticket) continue;       // ticket = implicite
                    if (!seenTypes.Add(pt)) continue;           // pas de doublon
                    result.Add(new ProofRequirement
                    {
                        Type    = pt,
                        RawText = DescribeProofType(pt),
                        Label   = DescribeProofType(pt)
                    });
                }
            }

            // 2. Compléter en parsant le texte des conditions (cas plus précis)
            var allText = (offer.Presentation?.Detail?.Conditions?.Content ?? "")
                       + "\n" + (offer.Presentation?.Detail?.Outline?.Content ?? "")
                       + "\n" + (offer.EffectiveDescription ?? "");
            if (!string.IsNullOrEmpty(allText))
            {
                var detected = ShopmiumParser.DetectAllProofTypes(allText);
                foreach (var pt in detected)
                {
                    if (pt == ProofType.None || pt == ProofType.Ticket) continue;
                    if (!seenTypes.Add(pt)) continue;
                    result.Add(ProofRequirement.From(pt, ShopmiumParser.DescribeProofType(pt)));
                }
            }

            return result; // peut être vide → aucun panneau preuves affiché
        }

        // ── Mapper purpose/overlay du nouveau format API ──────────────────────
        private static ProofType MapPurposeToProofType(string? purpose, string? overlay)
        {
            var p = (purpose ?? "").ToLowerInvariant();
            var o = (overlay ?? "").ToLowerInvariant();

            // overlay "receipt" ou purpose "receipt" → ticket
            if (p == "receipt" || o == "receipt") return ProofType.Ticket;

            // barcode crossing
            if (p.Contains("barcode") || o.Contains("barcode"))
            {
                if (p.Contains("cross") || o.Contains("cross") || p.Contains("damaged"))
                    return ProofType.PhotoBarcodeRaye;
                return ProofType.PhotoBarcodeRaye;
            }

            // packaging
            if (p.Contains("packag") || o.Contains("packag"))
            {
                if (p.Contains("cut") || o.Contains("cut"))
                    return ProofType.PhotoEmballageDecoupe;
                return ProofType.PhotoEmballage;
            }

            // price tag
            if (p.Contains("price") || o.Contains("price") || p.Contains("label"))
                return ProofType.PhotoEtiquettePrix;

            // articles
            if (p.Contains("product") || p.Contains("article") || p.Contains("picture"))
                return ProofType.PhotoArticles;

            return ProofType.None;
        }

        private static string DescribeProofType(ProofType pt) => pt switch
        {
            ProofType.Ticket                       => "Photo du ticket de caisse",
            ProofType.PhotoArticles                => "Photo des articles",
            ProofType.PhotoBarcodeRaye             => "Photo du code-barres rayé",
            ProofType.PhotoEmballage               => "Photo de l'emballage",
            ProofType.PhotoEmballageDecoupe        => "Photo de l'emballage découpé",
            ProofType.PhotoEtiquettePrix           => "Photo de l'étiquette prix",
            ProofType.PhotoProduitHorsEmballage    => "Photo du produit hors emballage",
            ProofType.PhotoArticlesEtTicket        => "Photo articles + ticket",
            _                                       => "Preuve",
        };

        private static ProofType MapStepToProofType(ShopmiumAdditionalStep step)
        {
            // Essayer kind en premier
            if (!string.IsNullOrEmpty(step.Kind) && _kindMap.TryGetValue(step.Kind, out var t1))
                return t1;
            // Puis type
            if (!string.IsNullOrEmpty(step.Type) && _kindMap.TryGetValue(step.Type, out var t2))
                return t2;
            // Chercher dans la description
            if (!string.IsNullOrEmpty(step.Description))
                return ShopmiumParser.ClassifyProofItem(step.Description);

            return ProofType.None;
        }

        private static string DescribeStep(ShopmiumAdditionalStep step)
        {
            var kind = (step.Kind ?? step.Type ?? "").ToLowerInvariant();
            return kind switch
            {
                "receipt"               => "Photo du ticket de caisse",
                "crossed_barcode"       => "Code-barres barré au stylo",
                "packaging"             => "Photo de l'emballage",
                "price_tag"             => "Photo de l'étiquette prix",
                "picture"               => "Photo du/des article(s)",
                "product_outside"       => "Photo du produit hors emballage",
                "cut_packaging"         => "Photo emballage découpé au code-barres",
                "articles_and_ticket"   => "Photo articles + ticket",
                _                       => "Preuve photo requise"
            };
        }

        // ── Construire le texte rayon pour TvaCalculator ──────────────────────
        private static string BuildRayonText(ShopmiumOffer offer)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(offer.Category))            parts.Add(offer.Category);
            if (!string.IsNullOrEmpty(offer.EffectiveDescription)) parts.Add(offer.EffectiveDescription);
            if (!string.IsNullOrEmpty(offer.Name))                parts.Add(offer.Name);
            // L'outline.heading donne souvent une indication de rayon
            if (!string.IsNullOrEmpty(offer.Presentation?.Detail?.Outline?.Heading))
                parts.Add(offer.Presentation.Detail.Outline.Heading);
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Calcule le TVA de façon asynchrone (avec fallback OpenFoodFacts + GPT).
        /// Appeler après Analyze() si TvaNeedsConfirmation == true.
        /// </summary>
        public static async Task RefreshTvaAsync(ProductData data)
        {
            var rate = await TvaCalculator.DetectAsync(data.ProductName, data.RayonText);
            data.TvaRate              = rate;
            data.TvaNeedsConfirmation = false;
        }
    }
}
