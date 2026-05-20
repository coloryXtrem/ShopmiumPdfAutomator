using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using ShopmiumPdfAutomator.Models;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Génère des images réalistes par composite Python :
    /// 1. Détourage de l'image officielle (fond → transparent)
    /// 2. Placement sur fond table en bois réaliste
    /// 3. Effets photo iPhone (couleur, netteté, vignette)
    ///
    /// Fidélité 100% : le packaging n'est jamais modifié.
    /// Aucune clé API requise.
    /// </summary>
    public static class ImageCompositeService
    {
        // ── Chemin vers le script Python embarqué ─────────────────────────────
        private static string ScriptPath
        {
            get
            {
                // Chercher d'abord à côté de l'exécutable
                var exeDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? "";
                var path = Path.Combine(exeDir, "Scripts", "composite_image.py");
                if (File.Exists(path)) return path;

                // Fallback : dossier AppData
                var appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ShopmiumPdfAutomator", "composite_image.py");
                return appData;
            }
        }

        // ── Vérification de disponibilité ─────────────────────────────────────
        public static bool IsAvailable()
        {
            if (!File.Exists(ScriptPath)) return false;
            return IsPythonAvailable();
        }

        public static bool IsPythonAvailable()
        {
            try
            {
                var p = RunProcess("python3", "--version", 3);
                return p != null && p.ExitCode == 0;
            }
            catch { }
            try
            {
                var p = RunProcess("python", "--version", 3);
                return p != null && p.ExitCode == 0;
            }
            catch { return false; }
        }

        // ── Point d'entrée principal ──────────────────────────────────────────
        public static async Task<ImageResult> GenerateCompositeAsync(
            ProductData data,
            ProofType chosenType,
            IProgress<string>? progress = null)
        {
            if (data.ProductImageData == null || data.ProductImageData.Length == 0)
                return new ImageResult
                {
                    Success = false,
                    Error   = "Aucune image officielle du produit disponible."
                };

            progress?.Report("Composite local : détourage + mise en scène...");

            // Sauvegarder l'image source en temporaire
            var tmpDir   = Path.GetTempPath();
            var srcPath  = Path.Combine(tmpDir, $"shpm_src_{Guid.NewGuid():N}.png");
            var outPath  = Path.Combine(tmpDir, $"shpm_out_{Guid.NewGuid():N}.jpg");

            try
            {
                File.WriteAllBytes(srcPath, data.ProductImageData);

                var proofArg = chosenType switch
                {
                    ProofType.PhotoBarcodeRaye      => "barcode",
                    ProofType.PhotoEtiquettePrix    => "etiquette",
                    ProofType.PhotoEmballage        => "emballage",
                    ProofType.PhotoArticlesEtTicket => "articles",
                    _                               => "articles"
                };

                var qty = data.MaxArticles.ToString();
                var ean = data.BarcodeEan ?? "";

                var args = $"\"{ScriptPath}\" \"{srcPath}\" {qty} {proofArg} \"{outPath}\" {ean}";

                progress?.Report($"Génération {proofArg} x{qty}...");

                var pythonCmd = FindPython();
                var proc = await RunProcessAsync(pythonCmd, args, 60);

                if (proc == null)
                    return new ImageResult
                    {
                        Success = false,
                        Error   = "Python introuvable.\\n" +
                                  "Installez Python 3.9+ avec Pillow : pip install pillow numpy"
                    };

                if (proc.ExitCode != 0)
                    return new ImageResult
                    {
                        Success = false,
                        Error   = "Erreur composite :\\n" + proc.StandardError
                    };

                if (!File.Exists(outPath))
                    return new ImageResult
                    {
                        Success = false,
                        Error   = "Le fichier de sortie n a pas été créé."
                    };

                var imgBytes = File.ReadAllBytes(outPath);
                progress?.Report("Composite terminé !");

                return new ImageResult
                {
                    Success    = true,
                    ImageData  = imgBytes,
                    Method     = "composite-local",
                    PromptUsed = $"{proofArg} x{qty}"
                };
            }
            finally
            {
                try { if (File.Exists(srcPath)) File.Delete(srcPath); } catch { }
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            }
        }

        // ── Helpers process ───────────────────────────────────────────────────
        private static string FindPython()
        {
            foreach (var cmd in new[] { "python3", "python" })
            {
                try
                {
                    var p = RunProcess(cmd, "--version", 3);
                    if (p?.ExitCode == 0) return cmd;
                }
                catch { }
            }
            return "python3";
        }

        private static Process? RunProcess(string cmd, string args, int timeoutSec)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(cmd, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                }
            };
            p.Start();
            p.WaitForExit(timeoutSec * 1000);
            return p;
        }

        private static async Task<ProcessResult?> RunProcessAsync(
            string cmd, string args, int timeoutSec)
        {
            var psi = new ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            var p = new Process { StartInfo = psi };
            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();

            p.OutputDataReceived += (_, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var completed = await Task.Run(
                () => p.WaitForExit(timeoutSec * 1000));

            if (!completed) { p.Kill(); }

            return new ProcessResult
            {
                ExitCode    = completed ? p.ExitCode : -1,
                StandardOut = sbOut.ToString(),
                StandardError = sbErr.ToString()
            };
        }

        private class ProcessResult
        {
            public int    ExitCode      { get; init; }
            public string StandardOut   { get; init; } = "";
            public string StandardError { get; init; } = "";
        }
    }
}
