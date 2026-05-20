using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ShopmiumPdfAutomator
{
    /// <summary>Convertit un bool en opacité : true=1.0, false=0.4</summary>
    public class BoolToOpacityConverter : IValueConverter
    {
        public static readonly BoolToOpacityConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? 1.0 : 0.4;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Convertit une URL d'image (string) en BitmapImage WPF.
    /// Utilise un cache statique pour éviter les téléchargements répétés.
    /// Chargement asynchrone : la 1ère fois renvoie null, l'image apparaît
    /// quand le téléchargement aboutit (le binding est re-déclenché).
    /// </summary>
    public class UrlToImageConverter : IValueConverter
    {
        public static readonly UrlToImageConverter Instance = new();

        // Cache global url → BitmapImage déjà décodée (Frozen, thread-safe)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage> _cache
            = new();

        // Téléchargements en cours pour éviter les doublons
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<BitmapImage?>> _inflight
            = new();

        private static readonly System.Net.Http.HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        /// <summary>
        /// Précharge une liste d'URLs en arrière-plan (à appeler après le chargement
        /// des offres pour que toutes les vignettes soient prêtes).
        /// </summary>
        public static Task PreloadAsync(IEnumerable<string?> urls)
        {
            var tasks = urls
                .Where(u => !string.IsNullOrWhiteSpace(u) && !_cache.ContainsKey(u!))
                .Distinct()
                .Select(u => GetOrDownloadAsync(u!))
                .ToArray();
            return Task.WhenAll(tasks);
        }

        // Cache global des bytes bruts (pour réutilisation dans PSD/preview)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _bytesCache
            = new();

        /// <summary>
        /// Récupère les bytes bruts d'une image (depuis le cache si dispo, sinon télécharge).
        /// Utilisé par le pipeline PSD pour éviter de re-télécharger une image
        /// déjà préchargée pour les vignettes.
        /// </summary>
        public static async Task<byte[]?> GetCachedBytesAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (_bytesCache.TryGetValue(url, out var cached)) return cached;

            try
            {
                var bytes = await _http.GetByteArrayAsync(url);
                _bytesCache[url] = bytes;
                return bytes;
            }
            catch { return null; }
        }

        private static async Task<BitmapImage?> GetOrDownloadAsync(string url)
        {
            if (_cache.TryGetValue(url, out var cached)) return cached;

            // Si déjà en cours de téléchargement, on attend ce téléchargement
            if (_inflight.TryGetValue(url, out var existing)) return await existing;

            var task = DownloadOneAsync(url);
            _inflight[url] = task;
            try
            {
                var bmp = await task;
                if (bmp != null) _cache[url] = bmp;
                return bmp;
            }
            finally
            {
                _inflight.TryRemove(url, out _);
            }
        }

        private static async Task<BitmapImage?> DownloadOneAsync(string url)
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(url);
                // Mettre aussi en cache les bytes pour réutilisation (PSD, preview, etc.)
                _bytesCache[url] = bytes;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new System.IO.MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 128;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var url = value as string;
            if (string.IsNullOrWhiteSpace(url)) return null;

            // 1. Si déjà en cache → renvoyer instantanément
            if (_cache.TryGetValue(url, out var cached)) return cached;

            // 2. Sinon : déclencher le téléchargement en arrière-plan (fire-and-forget)
            //    Quand il est terminé, on ne peut PAS notifier le binding ici,
            //    mais le préchargement (PreloadAsync) doit l'avoir déjà fait
            //    en amont. En fallback, on relance ici si jamais.
            _ = GetOrDownloadAsync(url);
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
