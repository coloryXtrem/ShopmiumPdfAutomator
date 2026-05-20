using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Nettoyage local des metadata d'une image (PNG/JPEG/autres).
    /// Stratégie : parser natif des chunks PNG et segments JPEG pour vraiment
    /// supprimer EXIF, XMP, IPTC, ICC, tEXt, tIME, etc. Fonctionne hors-ligne.
    /// </summary>
    public static class ExifCleanerService
    {
        public class CleanResult
        {
            public byte[]?       CleanedBytes      { get; set; }
            public string?       OriginalFormat    { get; set; }
            public string?       OutputFormat      { get; set; }
            public int           Width             { get; set; }
            public int           Height            { get; set; }
            public List<string>  RemovedNames      { get; set; } = new();
            public string?       Error             { get; set; }
            public bool          Success => Error == null && CleanedBytes != null;
        }

        public class VerifyResult
        {
            public bool          HasMetadata   { get; set; }
            public List<string>  MetadataFound { get; set; } = new();
            public string?       Format        { get; set; }
            public string?       Error         { get; set; }
            public string        Summary
                => Error != null
                    ? $"⚠ Erreur : {Error}"
                    : HasMetadata
                        ? $"⚠ {MetadataFound.Count} bloc(s) de metadata résiduels :\n"
                          + string.Join("\n", MetadataFound.Select(m => "  • " + m))
                        : "✓ Aucune metadata détectée — l'image est propre.";
        }

        public static string DetectFormat(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8) return "?";
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return "PNG";
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "JPEG";
            if (bytes[0] == 0x42 && bytes[1] == 0x4D) return "BMP";
            if ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00)
             || (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A))
                return "TIFF";
            if (bytes.Length >= 12
             && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
             && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return "WEBP";
            return "?";
        }

        // ════════════════════════════════════════════════════════════════════
        // CLEAN
        // ════════════════════════════════════════════════════════════════════

        public static CleanResult Clean(byte[] sourceBytes)
        {
            var result = new CleanResult();
            try
            {
                var fmt = DetectFormat(sourceBytes);
                result.OriginalFormat = fmt;

                try
                {
                    using var sizeStream = new MemoryStream(sourceBytes);
                    var dec = BitmapDecoder.Create(sizeStream,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (dec.Frames.Count > 0)
                    {
                        result.Width  = dec.Frames[0].PixelWidth;
                        result.Height = dec.Frames[0].PixelHeight;
                    }
                }
                catch { }

                // ─── PASSE 1 : nettoyage chunk-by-chunk (rapide, garde le format) ──
                byte[]? pass1;
                switch (fmt)
                {
                    case "PNG":
                        pass1 = CleanPng(sourceBytes, result.RemovedNames);
                        result.OutputFormat = "PNG";
                        break;
                    case "JPEG":
                        pass1 = CleanJpeg(sourceBytes, result.RemovedNames);
                        result.OutputFormat = "JPEG";
                        break;
                    default:
                        pass1 = ReencodeAsPng(sourceBytes);
                        result.OutputFormat = "PNG (converti)";
                        result.RemovedNames.Add($"Conversion {fmt} → PNG sans metadata");
                        break;
                }

                if (pass1 == null)
                {
                    result.Error = "La passe 1 n'a produit aucune sortie.";
                    return result;
                }

                // ─── PASSE 2 : ré-encodage TOTAL via WPF avec pixel buffer pur ──
                //
                //  Cette passe garantit la suppression de tout chunk technique
                //  résiduel (gAMA, cHRM, sRGB, bKGD, pHYs, sBIT, hIST, sPLT, etc.)
                //  ainsi que de toute trace de profil couleur, gamma, etc.
                //  → on décode en pixels bruts puis on ré-encode depuis ces pixels,
                //    sans aucun metadata, ni profil couleur, ni chunk d'auxiliaire.
                //
                try
                {
                    result.CleanedBytes = StripAllViaPixelBuffer(pass1, fmt, result.RemovedNames);
                    if (result.CleanedBytes == null) result.CleanedBytes = pass1;
                }
                catch
                {
                    // si la passe 2 échoue, on retombe sur la passe 1
                    result.CleanedBytes = pass1;
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Décode l'image en pixels bruts puis ré-encode dans le format demandé
        /// SANS aucun metadata ni chunk technique non essentiel.
        /// </summary>
        private static byte[]? StripAllViaPixelBuffer(byte[] src, string fmt, List<string> removed)
        {
            using var inStream = new MemoryStream(src);
            var decoder = BitmapDecoder.Create(
                inStream,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];

            // Convertir en BGRA32 (pixel format universel sans aucun profil couleur)
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = frame;
            converted.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();

            // Extraire les pixels bruts
            int w = converted.PixelWidth;
            int h = converted.PixelHeight;
            int stride = w * 4;
            byte[] pixels = new byte[h * stride];
            converted.CopyPixels(pixels, stride, 0);

            // Recréer un BitmapSource depuis les pixels purs (96 DPI fixe, pas de palette)
            var clean = System.Windows.Media.Imaging.BitmapSource.Create(
                w, h, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                pixels, stride);
            clean.Freeze();

            // Re-créer un BitmapFrame sans aucun metadata
            var cleanFrame = BitmapFrame.Create(clean, null, null, null);

            BitmapEncoder encoder;
            if (string.Equals(fmt, "JPEG", StringComparison.OrdinalIgnoreCase))
            {
                encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            }
            else
            {
                encoder = new PngBitmapEncoder();
            }
            encoder.Frames.Add(cleanFrame);

            using var outStream = new MemoryStream();
            encoder.Save(outStream);
            var bytes = outStream.ToArray();

            // ─── Nettoyage final post-encodage WPF ───
            // WPF peut réinjecter des chunks (iCCP/sRGB/gAMA/pHYs/tEXt/iTXt) au
            // ré-encodage. On refait une passe chunk-by-chunk pour les supprimer.
            if (DetectFormat(bytes) == "PNG")
            {
                var preCount = removed.Count;
                bytes = StripPngToMinimum(bytes, removed);
                if (removed.Count > preCount)
                    removed.Add($"(post-WPF cleanup : {removed.Count - preCount} chunks supprimés)");
            }
            else if (DetectFormat(bytes) == "JPEG")
            {
                bytes = CleanJpeg(bytes, removed);
            }

            return bytes;
        }

        /// <summary>
        /// Variante AGRESSIVE de CleanPng : ne garde QUE IHDR / PLTE / IDAT / IEND / tRNS.
        /// Tous les autres chunks (gAMA, cHRM, sRGB, bKGD, pHYs, sBIT, hIST, sPLT, etc.)
        /// sont retirés. Utilisé en post-traitement après WPF qui peut en réinjecter.
        /// </summary>
        private static byte[] StripPngToMinimum(byte[] src, List<string> removed)
        {
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (src.Length < 8) return src;
            for (int i = 0; i < 8; i++) if (src[i] != sig[i]) return src;

            // Chunks ESSENTIELS à garder (sans eux le PNG ne peut être décodé)
            var keep = new HashSet<string>(StringComparer.Ordinal) {
                "IHDR", "PLTE", "IDAT", "IEND", "tRNS"
            };

            using var ms = new MemoryStream();
            ms.Write(sig, 0, 8);

            int pos = 8;
            while (pos + 12 <= src.Length)
            {
                int len = (src[pos] << 24) | (src[pos+1] << 16) | (src[pos+2] << 8) | src[pos+3];
                if (len < 0 || pos + 12 + len > src.Length) break;
                string type = Encoding.ASCII.GetString(src, pos+4, 4);
                int total = 12 + len;

                if (!keep.Contains(type))
                    removed.Add($"PNG chunk {type} ({len} bytes) [strip-to-min]");
                else
                    ms.Write(src, pos, total);

                pos += total;
                if (type == "IEND") break;
            }
            return ms.ToArray();
        }

        // ─── PNG : chunks à supprimer ─────────────────────────────────────────
        private static readonly HashSet<string> _pngChunksToRemove = new(StringComparer.Ordinal)
        {
            "tEXt", "zTXt", "iTXt",   // textuels (auteur, software, copyright, etc.)
            "eXIf",                   // EXIF dans PNG (depuis PNG spec 1.5)
            "tIME",                   // dernière modification
            "iCCP",                   // profil ICC (peut embarquer XMP)
            "iDOT",                   // Apple iDOT
        };

        private static byte[] CleanPng(byte[] src, List<string> removed)
        {
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (src.Length < 8) throw new Exception("PNG trop court.");
            for (int i = 0; i < 8; i++)
                if (src[i] != sig[i]) throw new Exception("Signature PNG invalide.");

            using var ms = new MemoryStream();
            ms.Write(sig, 0, 8);

            int pos = 8;
            while (pos + 12 <= src.Length)
            {
                int len = (src[pos] << 24) | (src[pos+1] << 16) | (src[pos+2] << 8) | src[pos+3];
                if (len < 0 || pos + 12 + len > src.Length) break;

                string type = Encoding.ASCII.GetString(src, pos+4, 4);
                int total = 12 + len;

                if (_pngChunksToRemove.Contains(type))
                    removed.Add($"PNG chunk {type} ({len} bytes)");
                else
                    ms.Write(src, pos, total);

                pos += total;
                if (type == "IEND") break;
            }

            return ms.ToArray();
        }

        // ─── JPEG : segments à supprimer ──────────────────────────────────────
        private static byte[] CleanJpeg(byte[] src, List<string> removed)
        {
            if (src.Length < 4 || src[0] != 0xFF || src[1] != 0xD8)
                throw new Exception("Signature JPEG invalide.");

            using var ms = new MemoryStream();
            ms.WriteByte(0xFF); ms.WriteByte(0xD8); // SOI

            int pos = 2;
            while (pos < src.Length - 1)
            {
                if (src[pos] != 0xFF) break;
                byte marker = src[pos + 1];

                // Marqueurs sans payload
                if (marker == 0xD8 || marker == 0xD9
                 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
                {
                    ms.WriteByte(0xFF); ms.WriteByte(marker);
                    pos += 2;
                    if (marker == 0xD9) break;
                    continue;
                }

                // SOS : données compressées → copier tout le reste tel quel
                if (marker == 0xDA)
                {
                    ms.Write(src, pos, src.Length - pos);
                    break;
                }

                if (pos + 4 > src.Length) break;
                int segLen = (src[pos+2] << 8) | src[pos+3];
                if (segLen < 2 || pos + 2 + segLen > src.Length) break;

                bool drop = false;
                string label = $"APP{marker - 0xE0}";

                if (marker >= 0xE0 && marker <= 0xEF)
                {
                    // APP segments
                    if (marker == 0xE0)
                    {
                        // APP0 — on supprime TOUT y compris JFIF.
                        //
                        // Le segment JFIF contient 6 champs visibles par les outils
                        // type Exif.tools : JFIFVersion, ResolutionUnit, XResolution,
                        // YResolution, ThumbnailWidth, ThumbnailHeight. Aucune info
                        // personnelle mais Shopmium voit "6 tags présents" → on vire.
                        // Le JPEG reste lisible : tous les décodeurs modernes savent
                        // décoder un JPEG sans APP0/JFIF (la signature FF D8 FF + les
                        // tables de quantification SOF/SOS suffisent).
                        drop = true;
                        bool isJfif = segLen >= 7 && pos + 4 + 5 < src.Length
                            && src[pos+4] == 'J' && src[pos+5] == 'F'
                            && src[pos+6] == 'I' && src[pos+7] == 'F' && src[pos+8] == 0x00;
                        label = isJfif ? "APP0/JFIF (supprimé pour 0 tag)" : "APP0 (non-JFIF)";
                    }
                    else
                    {
                        drop = true;
                        if (marker == 0xE1)
                        {
                            if (segLen >= 8 && pos+8 < src.Length
                             && src[pos+4] == 'E' && src[pos+5] == 'x'
                             && src[pos+6] == 'i' && src[pos+7] == 'f')
                                label = "APP1/EXIF";
                            else if (segLen >= 32 && pos+8 < src.Length
                                  && src[pos+4] == 'h' && src[pos+5] == 't'
                                  && src[pos+6] == 't' && src[pos+7] == 'p')
                                label = "APP1/XMP";
                            else label = "APP1";
                        }
                        else if (marker == 0xE2) label = "APP2/ICC";
                        else if (marker == 0xED) label = "APP13/IPTC-Photoshop";
                        else if (marker == 0xEE) label = "APP14/Adobe";
                    }
                }
                else if (marker == 0xFE)
                {
                    drop = true;
                    label = "COM (commentaire)";
                }

                if (drop) removed.Add($"JPEG {label} ({segLen} bytes)");
                else ms.Write(src, pos, 2 + segLen);

                pos += 2 + segLen;
            }

            return ms.ToArray();
        }

        private static byte[] ReencodeAsPng(byte[] src)
        {
            using var inStream = new MemoryStream(src);
            var decoder = BitmapDecoder.Create(inStream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new Exception("Image vide.");
            var frame = BitmapFrame.Create(decoder.Frames[0], null, null, null);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(frame);
            using var outStream = new MemoryStream();
            encoder.Save(outStream);
            return outStream.ToArray();
        }

        // ════════════════════════════════════════════════════════════════════
        // VERIFY
        // ════════════════════════════════════════════════════════════════════

        public static VerifyResult Verify(byte[] imageBytes)
        {
            var result = new VerifyResult();
            try
            {
                var fmt = DetectFormat(imageBytes);
                result.Format = fmt;

                switch (fmt)
                {
                    case "PNG":  VerifyPng(imageBytes, result); break;
                    case "JPEG": VerifyJpeg(imageBytes, result); break;
                    default:     VerifyViaWpf(imageBytes, result); break;
                }

                result.HasMetadata = result.MetadataFound.Count > 0;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        private static void VerifyPng(byte[] src, VerifyResult r)
        {
            if (src.Length < 8) return;

            // Chunks essentiels (autorisés sans être considérés comme metadata)
            var essential = new HashSet<string>(StringComparer.Ordinal) {
                "IHDR", "PLTE", "IDAT", "IEND", "tRNS"
            };

            int pos = 8;
            while (pos + 12 <= src.Length)
            {
                int len = (src[pos] << 24) | (src[pos+1] << 16) | (src[pos+2] << 8) | src[pos+3];
                if (len < 0 || pos + 12 + len > src.Length) break;
                string type = Encoding.ASCII.GetString(src, pos+4, 4);

                if (!essential.Contains(type))
                {
                    // Catégoriser pour un message clair à l'utilisateur
                    string label = type switch
                    {
                        "tEXt" or "zTXt" or "iTXt" => $"PNG chunk texte/metadata {type}",
                        "eXIf" => "PNG chunk EXIF (eXIf)",
                        "iCCP" => "PNG chunk profil couleur ICC (iCCP)",
                        "sRGB" => "PNG chunk sRGB",
                        "gAMA" => "PNG chunk gamma (gAMA)",
                        "cHRM" => "PNG chunk chromaticité (cHRM)",
                        "bKGD" => "PNG chunk couleur de fond (bKGD)",
                        "pHYs" => "PNG chunk résolution physique (pHYs)",
                        "sBIT" => "PNG chunk bits significatifs (sBIT)",
                        "hIST" => "PNG chunk histogramme (hIST)",
                        "sPLT" => "PNG chunk palette suggérée (sPLT)",
                        "tIME" => "PNG chunk date dernière modif (tIME)",
                        "iDOT" => "PNG chunk Apple iDOT",
                        _      => $"PNG chunk non-essentiel {type}"
                    };
                    r.MetadataFound.Add($"{label} ({len} bytes)");
                }

                pos += 12 + len;
                if (type == "IEND") break;
            }
        }

        private static void VerifyJpeg(byte[] src, VerifyResult r)
        {
            if (src.Length < 4) return;
            int pos = 2;
            while (pos < src.Length - 1)
            {
                if (src[pos] != 0xFF) break;
                byte marker = src[pos + 1];
                if (marker == 0xD8 || marker == 0xD9
                 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
                {
                    pos += 2;
                    if (marker == 0xD9) break;
                    continue;
                }
                if (marker == 0xDA) break;
                if (pos + 4 > src.Length) break;
                int segLen = (src[pos+2] << 8) | src[pos+3];
                if (segLen < 2 || pos + 2 + segLen > src.Length) break;

                if (marker == 0xE0)
                {
                    // APP0/JFIF — contient JFIFVersion, ResolutionUnit, X/YResolution,
                    // ThumbnailWidth/Height (6 tags visibles par Exif.tools).
                    bool isJfif = segLen >= 7 && pos+8 < src.Length
                        && src[pos+4] == 'J' && src[pos+5] == 'F'
                        && src[pos+6] == 'I' && src[pos+7] == 'F';
                    r.MetadataFound.Add(isJfif ? "APP0/JFIF (6 tags JFIF)" : "APP0");
                }
                else if (marker == 0xE1)
                {
                    if (segLen >= 8 && pos+8 < src.Length
                     && src[pos+4] == 'E' && src[pos+5] == 'x')
                        r.MetadataFound.Add("EXIF (APP1)");
                    else if (segLen >= 32 && pos+8 < src.Length
                          && src[pos+4] == 'h' && src[pos+5] == 't')
                        r.MetadataFound.Add("XMP (APP1)");
                    else r.MetadataFound.Add("APP1");
                }
                else if (marker == 0xE2) r.MetadataFound.Add("ICC profile (APP2)");
                else if (marker == 0xED) r.MetadataFound.Add("IPTC/Photoshop (APP13)");
                else if (marker == 0xEE) r.MetadataFound.Add("Adobe (APP14)");
                else if (marker >= 0xE3 && marker <= 0xEF)
                    r.MetadataFound.Add($"APP{marker - 0xE0}");
                else if (marker == 0xFE)
                    r.MetadataFound.Add("Commentaire JPEG");

                pos += 2 + segLen;
            }
        }

        private static void VerifyViaWpf(byte[] src, VerifyResult r)
        {
            using var stream = new MemoryStream(src);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return;
            var md = decoder.Frames[0].Metadata as BitmapMetadata;
            if (md == null) return;
            void Try(string label, Func<string?> g)
            {
                try { if (!string.IsNullOrWhiteSpace(g())) r.MetadataFound.Add(label); } catch { }
            }
            Try("Caméra/fabricant", () => md.CameraManufacturer);
            Try("Modèle caméra",    () => md.CameraModel);
            Try("Date de prise",    () => md.DateTaken);
            Try("Titre",            () => md.Title);
            Try("Copyright",        () => md.Copyright);
            Try("Commentaire",      () => md.Comment);
            Try("Application",      () => md.ApplicationName);
        }
    }
}
