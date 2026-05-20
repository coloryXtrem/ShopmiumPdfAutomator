using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Analyse le HTML collé par l'utilisateur depuis une page de résultats Auchan
    /// pour en extraire l'EAN du produit correspondant.
    /// La recherche automatique a été supprimée — l'utilisateur ouvre lui-même
    /// la recherche Auchan et colle le HTML ici.
    /// </summary>
    public static partial class AuchanService
    {
        private static readonly CookieContainer _cookies = new();
        private static readonly HttpClient _http;

        [GeneratedRegex(@"<script[^>]+id=""__NEXT_DATA__""[^>]*>([\s\S]*?)</script>",
            RegexOptions.IgnoreCase)]
        private static partial Regex NextDataScript();

        [GeneratedRegex(@"R[eé]f\s*/\s*EAN\s*:\s*\d+\s*/\s*(\d{13})",
            RegexOptions.IgnoreCase)]
        private static partial Regex RefEanPattern();

        [GeneratedRegex(@"href=""(/[a-z0-9\-/]+/pr-[A-Z0-9]+)""",
            RegexOptions.IgnoreCase)]
        private static partial Regex AuchanSlugPattern();

        [GeneratedRegex(@"(?:alt|aria-label|title)\s*=\s*""([^""]{3,120})""",
            RegexOptions.IgnoreCase)]
        private static partial Regex AltTitlePattern();

        [GeneratedRegex(@"pr-[A-Z0-9]{5}")]
        private static partial Regex PrSlugPattern();

        static AuchanService()
        {
            var handler = new HttpClientHandler
            {
                CookieContainer        = _cookies,
                AllowAutoRedirect      = true,
                UseCookies             = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9");
        }

        // ── Analyse du HTML collé par l'utilisateur ───────────────────────────
        public static async Task<string?> ParseSearchResultsHtmlAsync(
            string html, string productName, IProgress<string>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;
            progress?.Report("Analyse du HTML Auchan...");

            // Extraire les produits via __NEXT_DATA__ ou DOM
            var ndMatch = NextDataScript().Match(html);
            List<(string name, string url, string? ean)> products = ndMatch.Success
                ? ParseNextData(ndMatch.Groups[1].Value)
                : [];

            if (products.Count == 0)
            {
                products = ExtractProductsFromHtml(html);
                progress?.Report($"{products.Count} produit(s) détecté(s) via DOM");
            }
            else
            {
                progress?.Report($"{products.Count} produit(s) détecté(s) via __NEXT_DATA__");
            }

            if (products.Count == 0)
            {
                progress?.Report("Aucun produit détecté — vérifiez que vous avez copié la page complète.");
                return null;
            }

            var minScore = products.Count <= 3
                ? ProductMatcher.SEUIL_MIN - 15 : ProductMatcher.SEUIL_MIN;
            var best = ProductMatcher.BestMatch(productName, products, minScore);

            if (best == null)
            {
                progress?.Report("Aucun produit correspondant — essayez avec un autre terme de recherche.");
                return null;
            }

            progress?.Report($"Produit identifié : {best.Name[..Math.Min(55, best.Name.Length)]}");

            // EAN dans les résultats directement
            if (!string.IsNullOrEmpty(best.Ean) && BarcodeService.IsValidEan13(best.Ean))
            {
                progress?.Report($"✅ EAN extrait : {best.Ean}");
                return best.Ean;
            }

            // Récupérer la fiche produit pour l'EAN
            if (!string.IsNullOrEmpty(best.Url))
            {
                progress?.Report("Récupération de la fiche produit...");
                var ean = await FetchEanFromProductPage(best.Url, progress);
                if (!string.IsNullOrEmpty(ean)) return ean;
            }

            progress?.Report("EAN non trouvé — saisissez-le manuellement.");
            return null;
        }

        // ── Récupération HTTP de la fiche produit ─────────────────────────────
        private static async Task<string?> FetchEanFromProductPage(
            string url, IProgress<string>? progress)
        {
            try
            {
                await Task.Delay(300);
                var resp = await _http.GetAsync(url)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                if (!resp.IsSuccessStatusCode) return null;

                var html    = await resp.Content.ReadAsStringAsync();
                var ndMatch = NextDataScript().Match(html);
                if (ndMatch.Success)
                {
                    var ean = ExtractEanFromText(ndMatch.Groups[1].Value);
                    if (!string.IsNullOrEmpty(ean)) return ean;
                }

                var refM = RefEanPattern().Match(html);
                if (refM.Success && BarcodeService.IsValidEan13(refM.Groups[1].Value))
                    return refM.Groups[1].Value;

                return ExtractEanFromText(html);
            }
            catch { return null; }
        }

        // ── Parsing __NEXT_DATA__ ─────────────────────────────────────────────
        private static List<(string name, string url, string? ean)> ParseNextData(string json)
        {
            List<(string, string, string?)> result = [];
            try
            {
                using var doc = JsonDocument.Parse(json);
                Traverse(doc.RootElement, result, 0);
            }
            catch { }
            return [..result.DistinctBy(p => p.Item2).Take(20)];
        }

        private static void Traverse(
            JsonElement el, List<(string, string, string?)> result, int depth)
        {
            if (depth > 10) return;
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    Traverse(item, result, depth + 1);
                return;
            }
            if (el.ValueKind != JsonValueKind.Object) return;

            string? slug = null, label = null, ean = null;
            foreach (var prop in el.EnumerateObject())
            {
                var pn = prop.Name.ToLowerInvariant();
                switch (pn)
                {
                    case "slug" when prop.Value.ValueKind == JsonValueKind.String:
                        var sv = prop.Value.GetString() ?? "";
                        if (sv.Contains("/pr-") || PrSlugPattern().IsMatch(sv)) slug = sv;
                        break;
                    case "label" or "name" or "title" or "productname"
                         when prop.Value.ValueKind == JsonValueKind.String:
                        var lv = prop.Value.GetString() ?? "";
                        if (lv.Length >= 3 && !lv.StartsWith('/') && lv.Any(char.IsLetter))
                            label ??= lv;
                        break;
                    case "gtin13" or "ean" or "barcode" or "gtin":
                        var ev = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
                        if (ev.Length == 13 && BarcodeService.IsValidEan13(ev)) ean = ev;
                        break;
                }
            }
            if (!string.IsNullOrEmpty(slug))
            {
                var url  = "https://www.auchan.fr/" + slug.TrimStart('/');
                var name = label ?? slug.Split('/').First().Replace("-", " ");
                result.Add((name, url, ean));
            }
            foreach (var prop in el.EnumerateObject())
                Traverse(prop.Value, result, depth + 1);
        }

        // ── Extraction depuis HTML brut ───────────────────────────────────────
        private static List<(string name, string url, string? ean)> ExtractProductsFromHtml(
            string html)
        {
            List<(string, string, string?)> result = [];
            foreach (Match m in AuchanSlugPattern().Matches(html))
            {
                var slug  = m.Groups[1].Value;
                var url   = "https://www.auchan.fr" + slug;
                var start = Math.Max(0, m.Index - 500);
                var ctx   = html.Substring(start, Math.Min(800, html.Length - start));
                var nameM = AltTitlePattern().Match(ctx);
                var name  = nameM.Success
                    ? nameM.Groups[1].Value
                    : slug.Split('/').First().Replace("-", " ");
                if (!result.Any(r => r.Item2 == url))
                    result.Add((name, url, ExtractEanFromText(ctx)));
            }
            return result;
        }

        // ── Extraction EAN depuis texte ───────────────────────────────────────
        internal static string? ExtractEanFromText(string content)
        {
            string[] patterns =
            [
                @"""gtin13""\s*:\s*""(\d{13})""",
                @"""gtin13""\s*:\s*(\d{13})\b",
                @"""ean""\s*:\s*""(\d{8,13})""",
                @"""barcode""\s*:\s*""(\d{8,13})""",
                @"""codeEan""\s*:\s*""(\d{8,13})""",
            ];
            foreach (var p in patterns)
            {
                var m = Regex.Match(content, p, RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var raw = m.Groups[1].Value;
                if (raw.Length == 13 && BarcodeService.IsValidEan13(raw)) return raw;
            }
            return null;
        }
    }
}
