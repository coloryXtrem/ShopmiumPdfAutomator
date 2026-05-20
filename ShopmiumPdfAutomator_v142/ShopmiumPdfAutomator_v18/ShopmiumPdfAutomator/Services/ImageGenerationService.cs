using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShopmiumPdfAutomator.Models;

namespace ShopmiumPdfAutomator.Services
{
    public class ImageResult
    {
        public bool    Success    { get; init; }
        public byte[]? ImageData  { get; init; }
        public string? Error      { get; init; }
        public string? PromptUsed { get; init; }
        public string? Method     { get; init; }
    }

    public class ImageApiSettings
    {
        public string OpenAiKey { get; set; } = "";
        public string Model     { get; set; } = "gpt-image-1";
        public string Size      { get; set; } = "1024x1024";
    }

    public static class ImageGenerationService
    {
        private static readonly HttpClient _http = new()
            { Timeout = TimeSpan.FromSeconds(120) };

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShopmiumPdfAutomator", "image_api.json");

        public static ImageApiSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return JsonSerializer.Deserialize<ImageApiSettings>(
                        File.ReadAllText(SettingsPath)) ?? new();
            }
            catch { }
            return new ImageApiSettings();
        }

        public static void SaveSettings(ImageApiSettings s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }

        // ============================================================
        //  POINT D'ENTREE PRINCIPAL
        // ============================================================
        public static async Task<ImageResult> GenerateAsync(
            ProductData data,
            ProofType chosenType,
            ImageApiSettings settings,
            IProgress<string>? progress = null,
            string rawRequirementText = "")
        {
            if (string.IsNullOrWhiteSpace(settings.OpenAiKey))
                return Fail("Cle API OpenAI manquante.\\n\\nVa dans l onglet Params.");

            if (data.ProductImageData != null && data.ProductImageData.Length > 0)
            {
                progress?.Report("Image officielle disponible — gpt-image-1 edit...");
                return await GenerateWithImageEditing(
                    data, chosenType, settings, progress, rawRequirementText);
            }

            progress?.Report("Aucune image de base — DALL-E 3 generation...");
            return await GenerateDallE3Fallback(data, chosenType, settings, progress);
        }

        // ============================================================
        //  MODE A : gpt-image-1 IMAGE EDITING (FIDELE AU PRODUIT)
        //  Equivalent exact de "coller image dans ChatGPT + prompt"
        // ============================================================
        private static async Task<ImageResult> GenerateWithImageEditing(
            ProductData data, ProofType chosenType,
            ImageApiSettings settings, IProgress<string>? progress,
            string rawRequirementText = "")
        {
            var prompt = BuildEditPrompt(data, chosenType, rawRequirementText);
            progress?.Report("Envoi image + prompt a gpt-image-1...");

            // Taille adaptée selon le type :
            // portrait 1024x1536 pour articles sur table (comme ChatGPT)
            // carré 1024x1024 pour macro barcode et emballage
            var imgSize = chosenType switch
            {
                ProofType.PhotoArticles         => "1024x1536",
                ProofType.PhotoArticlesEtTicket => "1024x1536",
                ProofType.PhotoBarcodeRaye      => "1024x1024",
                _                               => "1024x1024"
            };

            // Préparer l'image : doit être PNG pour /images/edits
            var pngBytes = EnsurePng(data.ProductImageData!);

            // Construction multipart/form-data
            using var form = new MultipartFormDataContent();

            // Image source (produit réel)
            var imgContent = new ByteArrayContent(pngBytes);
            imgContent.Headers.ContentType =
                new MediaTypeHeaderValue("image/png");
            form.Add(imgContent, "image", "product.png");

            // Prompt
            form.Add(new StringContent(prompt), "prompt");

            // Modèle et paramètres
            form.Add(new StringContent("gpt-image-1"), "model");
            form.Add(new StringContent(imgSize), "size");
            form.Add(new StringContent("high"), "quality");
            form.Add(new StringContent("1"), "n");

            var req = new HttpRequestMessage(
                HttpMethod.Post, "https://api.openai.com/v1/images/edits");
            req.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.OpenAiKey);
            req.Content = form;

            try
            {
                progress?.Report("Generation en cours (20-40 sec)...");
                var resp = await _http.SendAsync(req);
                var raw  = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    var errMsg = ParseOpenAiError(raw);

                    // gpt-image-1 non disponible → fallback DALL-E 3
                    if (raw.Contains("model_not_found") ||
                        raw.Contains("not supported") ||
                        raw.Contains("does not exist"))
                    {
                        progress?.Report("gpt-image-1 indispo — fallback DALL-E 3...");
                        return await GenerateDallE3Fallback(
                            data, chosenType, settings, progress);
                    }
                    return Fail(errMsg);
                }

                using var doc = JsonDocument.Parse(raw);
                var item = doc.RootElement.GetProperty("data")[0];

                byte[] imgBytes;
                // gpt-image-1 peut retourner b64_json ou url
                if (item.TryGetProperty("b64_json", out var b64El) &&
                    b64El.ValueKind != JsonValueKind.Null)
                {
                    imgBytes = Convert.FromBase64String(b64El.GetString()!);
                }
                else if (item.TryGetProperty("url", out var urlEl))
                {
                    imgBytes = await _http.GetByteArrayAsync(urlEl.GetString()!);
                }
                else
                {
                    return Fail("Reponse inattendue de gpt-image-1");
                }

                progress?.Report("Image generee avec fidelite maximale !");
                return new ImageResult
                {
                    Success    = true,
                    ImageData  = imgBytes,
                    PromptUsed = prompt,
                    Method     = "gpt-image-1-edit"
                };
            }
            catch (Exception ex)
            {
                return Fail("Erreur : " + ex.Message);
            }
        }

        // ============================================================
        //  PROMPTS POUR IMAGE EDITING (gpt-image-1)
        //  L'IA garde le produit tel quel, change seulement la mise en scène
        // ============================================================
        private static string BuildEditPrompt(
            ProductData data, ProofType chosenType, string rawRequirement = "")
        {
            var qty = data.MaxArticles;
            var ean = data.BarcodeEan ?? "";

            // Texte exact de l exigence Shopmium traduit en anglais pour le prompt
            // Si disponible, il est injecté pour que l IA colle exactement à la demande
            var reqContext = !string.IsNullOrWhiteSpace(rawRequirement)
                ? $"Shopmium requirement (translate faithfully): \"{rawRequirement}\". "
                : "";

            // ── Bloc hyperréalisme commun ──────────────────────────────────────
            const string photoRealism =
                "Shot on iPhone 15 Pro, 26mm lens, f/1.8 aperture. " +
                "Natural soft window light from the left, casting subtle directional shadows. " +
                "Slight motion blur on corners (handheld). " +
                "Realistic digital noise/grain (ISO 400 look). " +
                "Subtle lens flare, gentle bokeh on background. " +
                "True-to-life color rendering, no studio lighting. " +
                "JPEG compression artifacts barely visible. " +
                "The result must be completely indistinguishable from a real consumer photo.";

            // ── Instruction fidélité packaging ─────────────────────────────────
            const string keepExact =
                "CRITICAL: Keep the product packaging EXACTLY as shown in the input image — " +
                "identical colors, logo, text, fonts, proportions, label layout. " +
                "Do NOT alter, simplify or hallucinate the packaging in any way. ";

            return chosenType switch
            {
                ProofType.PhotoArticles =>
                    keepExact + reqContext +
                    $"Show {qty} unit(s) of this exact product arranged naturally " +
                    "on a real wooden kitchen table. " +
                    "Products slightly overlapping or staggered for natural composition. " +
                    "Front labels clearly visible and readable. " +
                    "Warm ambient kitchen light. Realistic wood grain texture on table. " +
                    "Background: blurred kitchen countertop with soft out-of-focus elements. " +
                    photoRealism,

                ProofType.PhotoBarcodeRaye =>
                    keepExact + reqContext +
                    "Generate an extreme close-up of the barcode zone on this product's packaging. " +
                    (string.IsNullOrEmpty(ean) ? "" :
                        $"The barcode encodes EAN-13: {ean}. ") +
                    "The barcode is crossed out with 2-3 thick diagonal strokes " +
                    "of a black permanent marker (Sharpie). " +
                    "Strokes are irregular, hand-drawn, slightly uneven pressure — " +
                    "like a real person cancelling a barcode. " +
                    "Ink is matte black, slightly absorbed into the packaging surface. " +
                    "Original barcode bars still partially visible through the ink. " +
                    "Macro photography, shallow depth of field (2-3mm), " +
                    "slight focus fall-off at edges. " +
                    photoRealism,

                ProofType.PhotoEtiquettePrix =>
                    "Ultra-realistic photo of a French supermarket shelf price label. " +
                    "Standard Carrefour Market or Leclerc white rectangular label " +
                    "with black product name text, large red/black price digits in euros. " +
                    "Label is attached to a metal shelf rail, slightly worn at edges. " +
                    "Background: blurred supermarket shelf with product visible. " +
                    "Harsh fluorescent ceiling lighting with slight flicker reflection. " +
                    "Consumer handheld smartphone perspective, slight tilt angle. " +
                    photoRealism,

                ProofType.PhotoEmballage =>
                    keepExact + reqContext +
                    "Photo of the complete packaging of this product. " +
                    "Product placed on a flat surface or held at arm's length. " +
                    "All sides of the packaging clearly visible. " +
                    "The product may appear slightly opened, crushed or damaged " +
                    "(as is typical for a cashback proof photo after use). " +
                    "Indoor natural light, slight reflection on packaging surface. " +
                    photoRealism,

                ProofType.PhotoArticlesEtTicket =>
                    keepExact + reqContext +
                    $"Photo showing {qty} unit(s) of this product next to " +
                    "a real French supermarket receipt (ticket de caisse). " +
                    "The receipt is from Carrefour or Leclerc, partially unfolded, " +
                    "showing product lines and total. Both products and receipt " +
                    "clearly visible and readable in the same frame. " +
                    "Laid out on a light-colored kitchen table. " +
                    photoRealism,

                _ =>
                    keepExact + reqContext +
                    "Product placed on a neutral kitchen surface. " +
                    "All label details clearly visible. " +
                    photoRealism
            };
        }

                // ============================================================
        //  MODE B : DALL-E 3 FALLBACK (quand pas d'image base)
        // ============================================================
        private static async Task<ImageResult> GenerateDallE3Fallback(
            ProductData data, ProofType chosenType,
            ImageApiSettings settings, IProgress<string>? progress)
        {
            var prompt = BuildDallE3Prompt(data, chosenType);
            progress?.Report("DALL-E 3 hd — generation en cours...");

            var body = new
            {
                model           = "dall-e-3",
                prompt          = prompt,
                n               = 1,
                size            = "1024x1024",
                quality         = "hd",
                style           = "natural",
                response_format = "b64_json"
            };

            var req = new HttpRequestMessage(
                HttpMethod.Post, "https://api.openai.com/v1/images/generations");
            req.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.OpenAiKey);
            req.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            try
            {
                var resp = await _http.SendAsync(req);
                var raw  = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return Fail(ParseOpenAiError(raw));

                using var doc = JsonDocument.Parse(raw);
                var b64 = doc.RootElement
                    .GetProperty("data")[0]
                    .GetProperty("b64_json").GetString()!;
                return new ImageResult
                {
                    Success    = true,
                    ImageData  = Convert.FromBase64String(b64),
                    PromptUsed = prompt,
                    Method     = "dalle3-fallback"
                };
            }
            catch (Exception ex) { return Fail("Erreur : " + ex.Message); }
        }

        private static string BuildDallE3Prompt(ProductData data, ProofType type)
        {
            var p = data.ProductName; var q = data.MaxArticles;
            return type switch
            {
                ProofType.PhotoBarcodeRaye =>
                    $"Ultra-realistic macro iPhone photo of the barcode of '{p}'. " +
                    $"EAN: {data.BarcodeEan}. 2-3 diagonal Sharpie strokes crossing it. " +
                    "Natural light. Photorealistic.",
                ProofType.PhotoArticles =>
                    $"Ultra-realistic iPhone photo of {q} unit(s) of '{p}' " +
                    "on a kitchen counter. Natural light. Labels readable.",
                _ =>
                    $"Realistic iPhone photo of '{p}' on neutral surface. Natural light."
            };
        }

        // ============================================================
        //  UTILITAIRES
        // ============================================================

        /// <summary>
        /// Convertit l'image en PNG RGBA si nécessaire.
        /// gpt-image-1 /images/edits requiert du PNG.
        /// </summary>
        private static byte[] EnsurePng(byte[] imageData)
        {
            // Si déjà PNG (header 89 50 4E 47)
            if (imageData.Length >= 4 &&
                imageData[0] == 0x89 && imageData[1] == 0x50)
                return imageData;

            // JPEG → PNG via System.Drawing
            try
            {
                using var ms  = new MemoryStream(imageData);
                using var bmp = System.Drawing.Image.FromStream(ms);
                using var out_ = new MemoryStream();
                bmp.Save(out_, System.Drawing.Imaging.ImageFormat.Png);
                return out_.ToArray();
            }
            catch
            {
                return imageData; // retourner tel quel si conversion échoue
            }
        }

        private static string ParseOpenAiError(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement
                    .GetProperty("error")
                    .GetProperty("message")
                    .GetString() ?? raw;
            }
            catch { return raw.Length > 300 ? raw[..300] : raw; }
        }

        private static ImageResult Fail(string error) =>
            new ImageResult { Success = false, Error = error };

        public static string SaveToDesktop(
            byte[] data, string productName, ProofType proofType)
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "ShopmiumImages");
            Directory.CreateDirectory(folder);

            // Nom de fichier format iPhone (IMG_XXXX.jpg) — discret
            var rnd  = new Random();
            var num  = rnd.Next(1000, 9999);
            var path = Path.Combine(folder, $"IMG_{num}.jpg");

            // Écrire d'abord en temporaire
            var tmpPath = Path.Combine(Path.GetTempPath(),
                $"shpm_raw_{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tmpPath, data);

            try
            {
                // Post-traitement : PNG → JPEG + EXIF iPhone
                var processed = PostProcessAsIphone(tmpPath, path);
                if (!processed)
                    File.Copy(tmpPath, path, overwrite: true); // fallback direct
            }
            finally
            {
                try { File.Delete(tmpPath); } catch { }
            }
            return path;
        }

        /// <summary>
        /// Post-traite l'image générée pour qu'elle ressemble à une vraie photo iPhone :
        /// - PNG/JPEG → JPEG qualité 88 (standard iPhone)
        /// - EXIF : Apple / iPhone 15 Pro / date réaliste
        /// - Micro-rotation et recadrage naturels
        /// - Nom de fichier IMG_XXXX.jpg
        /// Appelle le script Python embarqué si disponible.
        /// </summary>
        private static bool PostProcessAsIphone(string inputPath, string outputPath)
        {
            // Chercher le script Python dans le dossier de l'exécutable
            var exeDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            var script = Path.Combine(exeDir, "Scripts", "postprocess_image.py");

            if (!File.Exists(script)) return false;

            // Chercher Python
            string? python = null;
            foreach (var cmd in new[] { "python3", "python" })
            {
                try
                {
                    var test = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo(cmd, "--version")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError  = true,
                            UseShellExecute        = false,
                            CreateNoWindow         = true
                        }
                    };
                    test.Start();
                    test.WaitForExit(3000);
                    if (test.ExitCode == 0) { python = cmd; break; }
                }
                catch { }
            }
            if (python == null) return false;

            try
            {
                var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo(
                        python, "\"" + script + "\" \"" + inputPath + "\" \"" + outputPath + "\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true
                    }
                };
                proc.Start();
                proc.WaitForExit(20000);
                return proc.ExitCode == 0 && File.Exists(outputPath);
            }
            catch { return false; }
        }
    }
}
