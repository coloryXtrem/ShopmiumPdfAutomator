using HtmlAgilityPack;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Résultat d'extraction d'image produit.
    /// </summary>
    public class ProductImageResult
    {
        public bool     Success   { get; init; }
        public byte[]?  ImageData { get; init; }
        public string?  ImageUrl  { get; init; }
        public string?  Error     { get; init; }
        public string?  Source    { get; init; } // ex: "heading-block-photo", "slide-variant"
    }

    /// <summary>
    /// Extrait et télécharge l'image réelle du produit depuis le HTML Shopmium.
    ///
    /// Ordre de priorité pour trouver l'image la plus pertinente :
    ///   1. class="heading-block-photo" — image principale de la page produit (HD)
    ///   2. img dans heading-block-slide avec alt = nom produit
    ///   3. URL contenant offer_slide_images/variants/ (variante produit)
    ///   4. URL contenant offer_slide_images/ (slide générique)
    ///   5. URL contenant offer_products/ (image générique)
    ///   Fallback : première image du CDN Shopmium trouvée
    /// </summary>
    public static class ProductImageService
    {
        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        // CDN Shopmium
        private const string SHOPMIUM_CDN = "dojwn62xby8qn.cloudfront.net";
        private const string CLOUDFRONT   = "cloudfront.net";

        // ============================================================
        //  EXTRACTION D'URL
        // ============================================================

        /// <summary>
        /// Extrait les URLs d'images produit depuis le HTML de la page produit,
        /// triées par pertinence décroissante.
        /// </summary>
        // ── Extraction enrichie avec alt text pour matching produit ─────────
        public static List<(string Url, string Alt, string Source, int Score)>
            ExtractImageUrlsWithAlt(string html, string productName = "")
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var results  = new List<(string Url, string Alt, string Source, int Score)>();
            var nameLow  = productName.ToLowerInvariant();
            var seen     = new HashSet<string>();

            void Add(HtmlNode img, string src, int baseScore)
            {
                var url = GetSrc(img);
                if (string.IsNullOrEmpty(url) || seen.Contains(url)) return;
                seen.Add(url);
                var alt   = img.GetAttributeValue("alt", "").Trim();
                var altLow = alt.ToLowerInvariant();
                var bonus  = !string.IsNullOrEmpty(nameLow) &&
                             nameLow.Split(' ').Any(w => w.Length > 3 && altLow.Contains(w))
                             ? 20 : 0;
                results.Add((url, alt, src, baseScore + bonus));
            }

            // heading-block-photo (HD principale)
            doc.DocumentNode.SelectNodes("//img[contains(@class,'heading-block-photo')]")
                ?.ToList().ForEach(img => Add(img, "heading-block-photo", 100));

            // heading-block-slide
            doc.DocumentNode.SelectNodes(
                "//div[contains(@class,'heading-block-slide')]//img")
                ?.ToList().ForEach(img => Add(img, "heading-block-slide", 85));

            // CDN Shopmium
            var allNodes = doc.DocumentNode.SelectNodes("//img[@src]");
            foreach (var img in (IEnumerable<HtmlAgilityPack.HtmlNode>?)allNodes
                ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
            {
                var url = GetSrc(img);
                if (string.IsNullOrEmpty(url)) continue;
                if (!url.Contains(SHOPMIUM_CDN)) continue;
                int score = url.Contains("offer_slide_images/variants/") ? 80
                          : url.Contains("offer_slide_images/")          ? 60
                          : url.Contains("offer_products/")              ? 50
                          : url.Contains("customer_brand_offer_list_logos/") ? 5
                          : 30;
                Add(img, "cdn", score);
            }

                        return results
                .OrderByDescending(r => r.Score)
                .ToList();
        }

        /// <summary>
        /// Sélectionne l'image dont l'alt text correspond le mieux au nom produit.
        /// Fallback intelligent : même famille de produit si correspondance exacte absente.
        /// </summary>
        public static (string Url, string Alt, bool IsPerfectMatch, string MatchNote)
            SelectBestImageForProduct(
                List<(string Url, string Alt, string Source, int Score)> images,
                string productName)
        {
            if (images.Count == 0)
                return ("", "", false, "Aucune image disponible");

            var target = productName.ToLowerInvariant();
            var targetWords = target.Split(' ')
                .Where(w => w.Length > 2).ToList();

            // Score de similarité entre alt et nom produit
            int Similarity(string alt)
            {
                if (string.IsNullOrEmpty(alt)) return 0;
                var a = alt.ToLowerInvariant();
                return targetWords.Count(w => a.Contains(w));
            }

            var scored = images
                .Select(img => new { img.Url, img.Alt, sim = Similarity(img.Alt), img.Score })
                .OrderByDescending(x => x.sim * 100 + x.Score)
                .ToList();

            var best = scored.First();

            // Correspondance parfaite : tous les mots clés trouvés
            bool perfect = best.sim >= Math.Max(1, targetWords.Count - 1);

            string note = perfect
                ? $"Image correspondante : {best.Alt}"
                : $"Image approchante : « {best.Alt} » (produit cible : « {productName} »)";

            return (best.Url, best.Alt, perfect, note);
        }

        public static List<(string Url, string Source, int Score)> ExtractImageUrls(
            string html, string productName = "")
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var candidates = new List<(string Url, string Source, int Score)>();
            var nameLow    = productName.ToLowerInvariant();

            // ── Sélecteur 1 : heading-block-photo (image HD principale) ──────
            var mainPhotos = doc.DocumentNode
                .SelectNodes("//img[contains(@class,'heading-block-photo')]");
            if (mainPhotos != null)
                foreach (var img in mainPhotos)
                {
                    var url = GetSrc(img);
                    if (!string.IsNullOrEmpty(url))
                    {
                        // Bonus si alt correspond au nom produit
                        var alt   = img.GetAttributeValue("alt", "").ToLowerInvariant();
                        var bonus = !string.IsNullOrEmpty(nameLow) &&
                                    alt.Contains(nameLow.Split(' ')[0]) ? 20 : 0;
                        candidates.Add((url, "heading-block-photo", 100 + bonus));
                    }
                }

            // ── Sélecteur 2 : images dans les heading-block-slide ────────────
            var slideImgs = doc.DocumentNode
                .SelectNodes("//div[contains(@class,'heading-block-slide')]//img");
            if (slideImgs != null)
                foreach (var img in slideImgs)
                {
                    var url = GetSrc(img);
                    if (string.IsNullOrEmpty(url)) continue;
                    var alt = img.GetAttributeValue("alt", "").ToLowerInvariant();
                    // Priorité haute si alt correspond au produit le plus cher
                    var score = !string.IsNullOrEmpty(nameLow) &&
                                alt.Contains(nameLow.Split(' ')[0]) ? 90 : 70;
                    candidates.Add((url, "heading-block-slide", score));
                }

            // ── Sélecteur 3 : toutes les img du CDN Shopmium ─────────────────
            var allImgs = doc.DocumentNode.SelectNodes("//img[@src]");
            if (allImgs != null)
                foreach (var img in allImgs)
                {
                    var url = GetSrc(img);
                    if (string.IsNullOrEmpty(url)) continue;
                    if (!url.Contains(SHOPMIUM_CDN)) continue;

                    // Score selon le chemin URL
                    int score;
                    string source;

                    if (url.Contains("offer_slide_images/variants/"))
                    { score = 80; source = "slide-variant"; }
                    else if (url.Contains("offer_slide_images/"))
                    { score = 60; source = "slide"; }
                    else if (url.Contains("offer_products/"))
                    { score = 50; source = "offer-product"; }
                    else if (url.Contains("customer_brand_offer_list_logos/"))
                    { score = 10; source = "brand-logo"; } // logo marque, pas le produit
                    else
                    { score = 30; source = "cdn-other"; }

                    // Bonus si alt correspond au produit
                    var alt = img.GetAttributeValue("alt", "").ToLowerInvariant();
                    if (!string.IsNullOrEmpty(nameLow) && alt.Length > 0 &&
                        nameLow.Split(' ').Any(w => w.Length > 3 && alt.Contains(w)))
                        score += 15;

                    // Éviter les doublons
                    if (!candidates.Any(c => c.Url == url))
                        candidates.Add((url, source, score));
                }

            // Trier par score décroissant, dédupliqué
            return candidates
                .GroupBy(c => c.Url)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .OrderByDescending(c => c.Score)
                .ToList();
        }

        // ============================================================
        //  TÉLÉCHARGEMENT
        // ============================================================

        /// <summary>
        /// Extrait et télécharge la meilleure image produit depuis le HTML.
        /// </summary>
        public static async Task<ProductImageResult> FetchBestImageAsync(
            string html,
            string productName = "",
            IProgress<string>? progress = null)
        {
            progress?.Report("Analyse du HTML produit...");

            var candidates = ExtractImageUrls(html, productName);

            if (candidates.Count == 0)
                return new ProductImageResult
                {
                    Success = false,
                    Error   = "Aucune image trouvee dans le HTML produit."
                };

            progress?.Report($"{candidates.Count} image(s) trouvee(s), telechargement...");

            // Essayer les 3 meilleures candidates
            foreach (var (url, source, score) in candidates.Take(3))
            {
                try
                {
                    progress?.Report($"Telechargement ({source})...");

                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0.0.0");

                    var resp = await _http.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) continue;

                    var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                    if (!contentType.StartsWith("image/")) continue;

                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    if (bytes.Length < 1000) continue; // trop petit = pas une vraie image

                    progress?.Report($"Image telechargee ({bytes.Length / 1024} KB)");

                    return new ProductImageResult
                    {
                        Success   = true,
                        ImageData = bytes,
                        ImageUrl  = url,
                        Source    = source
                    };
                }
                catch { continue; }
            }

            return new ProductImageResult
            {
                Success = false,
                Error   = "Impossible de telecharger l image produit."
            };
        }

        // ============================================================
        //  TRAITEMENT POST-TELECHARGEMENT
        // ============================================================

        /// <summary>
        /// Convertit les bytes image en BitmapImage WPF.
        /// </summary>
        public static BitmapImage ToBitmap(byte[] data)
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(data);
            bmp.BeginInit();
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// Sauvegarde l'image sur le Bureau dans ShopmiumImages/.
        /// Retourne le chemin du fichier sauvegardé.
        /// </summary>
        public static string SaveToDesktop(
            byte[] data, string productName, string suffix = "PRODUIT")
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "ShopmiumImages");
            Directory.CreateDirectory(folder);

            var safe = Regex.Replace(
                productName.ToUpperInvariant(), @"[^A-Z0-9]", "_");
            safe = Regex.Replace(safe, @"_+", "_").Trim('_');
            if (safe.Length > 30) safe = safe[..30];

            // Déterminer l'extension depuis les données
            var ext = "jpg";
            if (data.Length >= 4 &&
                data[0] == 0x89 && data[1] == 0x50) ext = "png";   // PNG header
            else if (data.Length >= 2 &&
                data[0] == 0xFF && data[1] == 0xD8) ext = "jpg";    // JPEG header

            var path = Path.Combine(folder,
                $"{safe}_{suffix}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");
            File.WriteAllBytes(path, data);
            return path;
        }

        // ── Helper ────────────────────────────────────────────────────────────
        private static string GetSrc(HtmlNode img)
        {
            var src = img.GetAttributeValue("src", "");
            if (string.IsNullOrEmpty(src))
                src = img.GetAttributeValue("data-src", "");
            if (!src.StartsWith("http") && src.StartsWith("/"))
                src = "https://offers.shopmium.com" + src;
            return src;
        }
    }
}
