using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Analyse le HTML collé par l'utilisateur depuis une page de résultats Leclerc
    /// pour en extraire l'EAN du produit correspondant.
    /// </summary>
    public static partial class LeclercService
    {
        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        [GeneratedRegex(@"href=""(/(?:prd|produit)/[a-z0-9\-]+)""",
            RegexOptions.IgnoreCase)]
        private static partial Regex LeclercSlugPattern();

        [GeneratedRegex(@"(?:alt|aria-label|title)\s*=\s*""([^""]{3,120})""",
            RegexOptions.IgnoreCase)]
        private static partial Regex AltTitlePattern();

        static LeclercService()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9");
        }

        // ── Analyse du HTML collé par l'utilisateur ───────────────────────────
        public static async Task<string?> ParseSearchResultsHtmlAsync(
            string html, string productName, IProgress<string>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;
            progress?.Report("Analyse du HTML Leclerc...");

            var products = ExtractProductsFromHtml(html);
            progress?.Report($"{products.Count} produit(s) détecté(s)");

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
                progress?.Report("Aucun produit correspondant — essayez avec un autre terme.");
                return null;
            }

            progress?.Report($"Produit identifié : {best.Name[..Math.Min(55, best.Name.Length)]}");

            if (!string.IsNullOrEmpty(best.Ean) && BarcodeService.IsValidEan13(best.Ean))
            {
                progress?.Report($"✅ EAN extrait : {best.Ean}");
                return best.Ean;
            }

            if (!string.IsNullOrEmpty(best.Url))
            {
                progress?.Report("Récupération de la fiche produit Leclerc...");
                var ean = await FetchEanHttp(best.Url, progress);
                if (!string.IsNullOrEmpty(ean)) return ean;
            }

            progress?.Report("EAN non trouvé — saisissez-le manuellement.");
            return null;
        }

        // ── Récupération HTTP de la fiche produit ─────────────────────────────
        private static async Task<string?> FetchEanHttp(
            string url, IProgress<string>? _progress)
        {
            try
            {
                await Task.Delay(300);
                var resp = await _http.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(15));
                if (!resp.IsSuccessStatusCode) return null;
                var html = await resp.Content.ReadAsStringAsync();
                return ExtractEanFromText(html);
            }
            catch { return null; }
        }

        // ── Extraction depuis HTML brut ───────────────────────────────────────
        private static List<(string name, string url, string? ean)> ExtractProductsFromHtml(
            string html)
        {
            List<(string, string, string?)> result = [];
            foreach (Match m in LeclercSlugPattern().Matches(html))
            {
                var slug  = m.Groups[1].Value;
                var url   = "https://www.e.leclerc" + slug;
                var start = Math.Max(0, m.Index - 400);
                var ctx   = html.Substring(start, Math.Min(600, html.Length - start));
                var nameM = AltTitlePattern().Match(ctx);
                var name  = nameM.Success
                    ? nameM.Groups[1].Value
                    : slug.TrimStart('/').Replace("-", " ");
                if (!result.Any(r => r.Item2 == url))
                    result.Add((name, url, ExtractEanFromText(ctx)));
            }
            return result;
        }

        private static string? ExtractEanFromText(string content)
        {
            string[] patterns =
            [
                @"""gtin13""\s*:\s*""(\d{13})""",
                @"""ean""\s*:\s*""(\d{8,13})""",
                @"""barcode""\s*:\s*""(\d{8,13})""",
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
