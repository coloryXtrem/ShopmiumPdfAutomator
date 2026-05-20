using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Mise à jour silencieuse :
    ///   1. Vérifie version.json sur GitHub au démarrage (arrière-plan)
    ///   2. Si nouvelle version → télécharge le .exe en arrière-plan silencieusement
    ///   3. Quand le téléchargement est prêt → notifie l'UI (event UpdateReady)
    ///   4. L'UI affiche un bouton "Redémarrer" → l'utilisateur clique
    ///   5. L'app remplace son propre .exe puis se relance
    ///
    /// IMPORTANT : l'app doit être dans un dossier où elle a le droit d'écrire
    ///   → AppData\Local\ShopmiumHelper\ (pas Program Files)
    /// </summary>
    public static class UpdateService
    {
        // ── URL GitHub à configurer ─────────────────────────────────────────
        public static string VersionJsonUrl { get; set; } =
            "https://raw.githubusercontent.com/coloryXtrem/shopmium-updates/refs/heads/main/version.json";

        // ── Événements ──────────────────────────────────────────────────────
        /// <summary>Déclenché quand le téléchargement est terminé et prêt à installer.</summary>
        public static event Action<UpdateInfo>? UpdateReady;

        /// <summary>Déclenché pendant le téléchargement — (bytesReçus, totalBytes, pourcentage).</summary>
        public static event Action<long, long, int>? DownloadProgress;

        // ── Interne ─────────────────────────────────────────────────────────
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static string? _downloadedExePath = null;

        // ────────────────────────────────────────────────────────────────────
        //  VERSION LOCALE
        // ────────────────────────────────────────────────────────────────────
        public static string CurrentVersion =>
            Assembly.GetExecutingAssembly()
                    .GetName().Version?.ToString(3) ?? "1.0.0";

        // ────────────────────────────────────────────────────────────────────
        //  MODÈLE
        // ────────────────────────────────────────────────────────────────────
        public record UpdateInfo(
            string Version,
            string Url,
            string Notes,
            bool   Required);

        // ────────────────────────────────────────────────────────────────────
        //  ÉTAPE 1 — Vérifier si une mise à jour existe
        // ────────────────────────────────────────────────────────────────────
        public static async Task<UpdateInfo?> CheckAsync()
        {
            try
            {
                var json = await _http.GetStringAsync(VersionJsonUrl);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var remoteVersion = root.GetProperty("version").GetString() ?? "0.0.0";
                var url           = root.GetProperty("url").GetString()     ?? "";
                var notes         = root.TryGetProperty("notes",    out var n) ? (n.GetString() ?? "") : "";
                var required      = root.TryGetProperty("required", out var r) && r.GetBoolean();

                if (IsNewer(remoteVersion, CurrentVersion))
                    return new UpdateInfo(remoteVersion, url, notes, required);

                return null; // Déjà à jour
            }
            catch
            {
                return null; // Pas de connexion → silencieux
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  ÉTAPE 2 — Télécharger silencieusement en arrière-plan
        // ────────────────────────────────────────────────────────────────────
        public static async Task DownloadSilentlyAsync(UpdateInfo info)
        {
            try
            {
                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShopmiumHelper", "Updates");
                Directory.CreateDirectory(tempDir);

                // ── Nettoyer TOUS les anciens setups dans le dossier ────────
                // Cela gère :
                //   - changement de version (v2 → v3 : supprime l'ancien v2)
                //   - même nom de fichier (fichier corrompu ou re-publié)
                //   - doublons résiduels d'anciennes versions
                foreach (var oldFile in Directory.GetFiles(tempDir, "ShopmiumPdfAutomator_Setup_v*.exe"))
                {
                    try { File.Delete(oldFile); } catch { /* ignorer si verrouillé */ }
                }

                var fileName = $"ShopmiumPdfAutomator_Setup_v{info.Version}.exe";
                var destPath = Path.Combine(tempDir, fileName);

                // Télécharger dans un fichier temporaire d'abord
                // → évite qu'un téléchargement partiel soit considéré comme valide
                var tempPath = destPath + ".tmp";
                if (File.Exists(tempPath)) File.Delete(tempPath);

                using var response = await _http.GetAsync(
                    info.Url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var total    = response.Content.Headers.ContentLength ?? -1L;
                var received = 0L;

                await using var stream = await response.Content.ReadAsStreamAsync();
                await using (var file = File.Create(tempPath))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read));
                        received += read;
                        if (total > 0)
                        {
                            var pct = (int)(received * 100 / total);
                            DownloadProgress?.Invoke(received, total, pct);
                        }
                    }
                } // stream fermé ici → fichier libéré

                // Renommer .tmp → .exe seulement si téléchargement complet
                File.Move(tempPath, destPath, overwrite: true);

                _downloadedExePath = destPath;
                UpdateReady?.Invoke(info);
            }
            catch
            {
                // Échec silencieux — on réessaiera au prochain démarrage
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  ÉTAPE 3 — Installer et redémarrer (quand l'utilisateur clique)
        // ────────────────────────────────────────────────────────────────────
        public static void InstallAndRestart()
        {
            if (_downloadedExePath == null || !File.Exists(_downloadedExePath))
                return;

            // SIMPLE ET DIRECT :
            // Lancer le fichier telecharge dans AppData\Updates
            // C'est lui le vrai setup/updater - pas besoin de bat ni de schtasks
            // L'app se ferme apres -> aucun conflit de verrou
            Process.Start(new ProcessStartInfo
            {
                FileName        = _downloadedExePath,
                UseShellExecute = true
            });

            // L'app se ferme proprement - le setup continue independamment
            System.Windows.Application.Current.Shutdown();
        }

        // ────────────────────────────────────────────────────────────────────
        //  UTILITAIRE — Comparaison sémantique
        // ────────────────────────────────────────────────────────────────────
        public static bool IsNewer(string remote, string local)
        {
            try { return new Version(remote) > new Version(local); }
            catch { return false; }
        }

        public static bool UpdateIsReady => _downloadedExePath != null
                                         && File.Exists(_downloadedExePath);
    }
}
