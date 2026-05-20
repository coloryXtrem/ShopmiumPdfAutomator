using System.IO;
using Microsoft.Win32;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Système de licence par clé d'activation.
    /// 
    /// Fonctionnement :
    ///   1. L'acheteur reçoit une clé de licence (ex: SHPM-XXXX-XXXX-XXXX)
    ///   2. Au premier lancement, il saisit sa clé
    ///   3. La clé est vérifiée localement (algo HMAC-SHA256)
    ///   4. Si valide, elle est stockée chiffrée dans AppData
    ///   5. Les lancements suivants vérifient le fichier local
    /// 
    /// La clé encode : produit + date d'expiration + machine ID (optionnel)
    /// Format : SHPM-{BASE32(HMAC_SIGNATURE)}
    /// </summary>
    public static class LicenseService
    {
        // ── Constantes ────────────────────────────────────────────────────────
        private const string APP_NAME    = "ShopmiumPdfAutomator";
        private const string LICENSE_FILE = "license.dat";
        
        // Clé secrète HMAC — CHANGE CETTE VALEUR avant de distribuer !
        // Génère une clé aléatoire sur https://generate-secret.now.sh/64
        private const string SECRET_KEY  = "d0804ac66db8ac0fc52f6c606323c5ab0d6c0ac289a3e5b39dda8ef750a8643e";

        // Registre Windows — suivi des clés activées (survit à la suppression de license.dat)
        private const string REG_PATH = @"SOFTWARE\ShopmiumApp\Licenses";

        // ── Dossier de stockage ───────────────────────────────────────────────
        private static string LicenseFolder =>
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), APP_NAME);

        private static string LicensePath =>
            Path.Combine(LicenseFolder, LICENSE_FILE);

        // ══════════════════════════════════════════════════════════════════════
        //  API PUBLIQUE
        // ══════════════════════════════════════════════════════════════════════

        public enum LicenseStatus
        {
            Valid,
            NotActivated,
            Expired,
            Invalid,
        }

        /// <summary>Vérifie si l'application est activée.</summary>
        public static LicenseStatus Check()
        {
            if (!File.Exists(LicensePath))
                return LicenseStatus.NotActivated;

            try
            {
                var json    = Decrypt(File.ReadAllText(LicensePath));
                var stored  = JsonSerializer.Deserialize<StoredLicense>(json);
                if (stored == null) return LicenseStatus.Invalid;

                // Revérifier la signature de la clé
                bool sigOk = !string.IsNullOrEmpty(stored.Payload)
                    ? ValidateKeyWithPayload(stored.Key, stored.Payload, out _)
                    : ValidateKey(stored.Key, out _);
                if (!sigOk) return LicenseStatus.Invalid;

                // Vérifier l'expiration depuis la date calculée à l'activation
                if (stored.ExpiresAt.HasValue && stored.ExpiresAt.Value < DateTime.Now)
                    return LicenseStatus.Expired;

                return LicenseStatus.Valid;
            }
            catch
            {
                return LicenseStatus.Invalid;
            }
        }

        /// <summary>Active l'application avec la clé fournie.</summary>
        public static (bool Success, string Message) Activate(string key)
        {
            key = key.Trim().ToUpperInvariant();

            // Séparer la clé du payload si format étendu "SHPM-XXXX...:SHPM|D90|SEL"
            string cleanKey = key, storedPayload = "";
            if (key.Contains(':'))
            {
                var sep       = key.IndexOf(':');
                cleanKey      = key[..sep].Trim().ToUpperInvariant();
                storedPayload = key[(sep + 1)..].Trim();
            }

            if (!ValidateKey(key, out var days))
                return (false, "Clé de licence invalide.\nVérifiez la clé et réessayez.");

            var currentMachine = GetMachineId();

            // ── Vérification registre : clé déjà activée sur une autre machine ? ──
            var regResult = CheckRegistry(cleanKey, currentMachine);
            if (regResult == RegistryCheckResult.OtherMachine)
                return (false,
                    "Cette clé a déjà été activée sur un autre appareil.\n\n" +
                    "Si vous avez réinstallé Windows ou changé de PC, " +
                    "cette clé ne peut pas être réutilisée.\n\n" +
                    "Veuillez utiliser une nouvelle clé de licence.");

            // Calculer l'expiration depuis MAINTENANT en heure locale
            var now       = DateTime.Now;
            var expiresAt = days.HasValue ? now.AddDays(days.Value) : (DateTime?)null;

            // Si la clé est déjà activée sur cette machine → restaurer
            if (regResult == RegistryCheckResult.SameMachine)
            {
                var savedExpiry = GetRegistryExpiry(cleanKey);
                if (savedExpiry.HasValue) expiresAt = savedExpiry.Value;

                // Vérifier si expirée
                if (expiresAt.HasValue && expiresAt.Value < now)
                    return (false,
                        $"Cette clé a expiré le {expiresAt.Value:dd/MM/yyyy à HH:mm}.\n" +
                        "Elle ne peut pas être réactivée.\n" +
                        "Veuillez utiliser une nouvelle clé de licence.");

                var msgSame = expiresAt.HasValue
                    ? $"Valide jusqu'au {expiresAt.Value:dd/MM/yyyy à HH:mm}"
                    : "Licence permanente";

                // Resauvegarder le fichier local si supprimé
                Directory.CreateDirectory(LicenseFolder);
                var restoredLicense = new StoredLicense
                {
                    Key         = cleanKey,
                    Payload     = storedPayload,
                    ActivatedAt = now,
                    ExpiresAt   = expiresAt,
                    MachineId   = currentMachine,
                };
                File.WriteAllText(LicensePath, Encrypt(JsonSerializer.Serialize(restoredLicense)));

                return (true,
                    $"⚠️ Cette clé a déjà été utilisée sur cet appareil.\n" +
                    $"Votre accès a été restauré — {msgSame}");
            }

            // ── Vérifier si la clé est expirée (format court sans date connue) ──
            if (expiresAt.HasValue && expiresAt.Value < now)
            {
                return (false,
                    $"Cette clé a expiré le {expiresAt.Value:dd/MM/yyyy à HH:mm}.\n" +
                    "Elle ne peut pas être réactivée.\n" +
                    "Veuillez utiliser une nouvelle clé de licence.");
            }

            // Sauvegarder chiffrée (stocker la clé courte + le payload pour re-validation)
            Directory.CreateDirectory(LicenseFolder);
            var stored = new StoredLicense
            {
                Key         = cleanKey,
                Payload     = storedPayload,
                ActivatedAt = now,
                ExpiresAt   = expiresAt,
                MachineId   = currentMachine,
            };
            File.WriteAllText(LicensePath, Encrypt(JsonSerializer.Serialize(stored)));

            // Enregistrer dans le registre Windows (persiste même si license.dat supprimé)
            SaveToRegistry(cleanKey, currentMachine, expiresAt);

            var msg = expiresAt.HasValue
                ? $"Activé jusqu'au {expiresAt.Value:dd/MM/yyyy à HH:mm}"
                : "Activé (licence permanente)";
            return (true, msg);
        }

        /// <summary>Retourne les infos de la licence active.</summary>
        /// <summary>Retourne la date d'expiration UTC, null si permanente ou non activée.</summary>
        public static DateTime? GetExpiresAt()
        {
            if (!File.Exists(LicensePath)) return null;
            try
            {
                var stored = JsonSerializer.Deserialize<StoredLicense>(
                    Decrypt(File.ReadAllText(LicensePath)));
                return stored?.ExpiresAt;
            }
            catch { return null; }
        }

        /// <summary>
        /// Retourne la durée TOTALE de la licence en jours.
        /// null = permanente ou non activée.
        /// Permet à l'UI de choisir le mode d'affichage du timer.
        /// </summary>
        public static int? GetLicenceDays()
        {
            if (!File.Exists(LicensePath)) return null;
            try
            {
                var stored = JsonSerializer.Deserialize<StoredLicense>(
                    Decrypt(File.ReadAllText(LicensePath)));
                if (stored == null || !stored.ExpiresAt.HasValue) return null;
                return (int)Math.Round((stored.ExpiresAt.Value - stored.ActivatedAt).TotalDays);
            }
            catch { return null; }
        }

        public static string GetLicenseInfo()
        {
            if (!File.Exists(LicensePath)) return "Non activé";
            try
            {
                var stored = JsonSerializer.Deserialize<StoredLicense>(
                    Decrypt(File.ReadAllText(LicensePath)));
                if (stored == null) return "Erreur licence";
                return stored.ExpiresAt.HasValue
                    ? $"Valide jusqu'au {stored.ExpiresAt.Value:dd/MM/yyyy à HH:mm}"
                    : "Licence permanente";
            }
            catch { return "Erreur lecture licence"; }
        }

        /// <summary>Désactive la licence (pour les tests).</summary>
        public static void Deactivate()
        {
            try { if (File.Exists(LicensePath)) File.Delete(LicensePath); } catch { }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GÉNÉRATION DE CLÉS (à utiliser côté vendeur, pas dans l'app finale)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Génère une clé de licence.
        /// Appelle cette méthode dans un outil séparé pour créer les clés de tes clients.
        /// 
        /// expiry = null → licence permanente
        /// expiry = DateTime → expire à cette date
        /// </summary>
        /// <summary>
        /// Génère une clé encodant une DURÉE (pas une date fixe).
        /// L'expiration démarre à l'activation sur le PC de l'utilisateur.
        /// days = null → licence permanente
        /// </summary>
        public static string GenerateKey(int? days = null)
        {
            // Payload : "SHPM|D{jours}" (durée) ou "SHPM|PERM" (permanent)
            var payload = days.HasValue
                ? $"SHPM|D{days.Value}"
                : "SHPM|PERM";

            // Signature HMAC-SHA256
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SECRET_KEY));
            var hash        = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

            // Encoder en Base32 lisible (16 chars = 80 bits)
            var b32  = ToBase32(hash[..10]);                // 10 bytes → 16 chars
            var key  = $"SHPM-{b32[..4]}-{b32[4..8]}-{b32[8..12]}-{b32[12..16]}";

            return key;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  VALIDATION INTERNE
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Valide la signature HMAC de la clé et retourne le nombre de jours encodé.
        /// days = null → permanent. days = 0 → clé invalide/inconnue.
        /// L'expiration réelle est calculée à l'activation (ActivatedAt + days).
        /// </summary>
        /// <summary>
        /// Vérifie la signature HMAC d'une clé à partir de son payload connu.
        /// Le payload est stocké dans StoredLicense lors de l'activation.
        /// </summary>
        private static bool ValidateKeyWithPayload(string key, string payload, out int? days)
        {
            days = null;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(payload))
                return false;

            var b32part = key.Replace("SHPM-", "").Replace("-", "");
            if (b32part.Length != 16) return false;

            byte[] hashFromKey;
            try { hashFromKey = FromBase32(b32part); }
            catch { return false; }

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SECRET_KEY));
            var hash     = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            if (!hash[..10].SequenceEqual(hashFromKey)) return false;

            // Extraire la durée du payload : "SHPM|D30|SEL" → 30, "SHPM|PERM|SEL" → null
            var parts = payload.Split('|');
            if (parts.Length >= 2 && parts[1].StartsWith("D"))
                days = int.Parse(parts[1][1..]);
            // sinon days reste null = permanent

            return true;
        }

        /// <summary>
        /// Validation lors de l'ACTIVATION : on ne connaît pas encore le payload,
        /// on teste les combinaisons durée × 65536 sels possibles — trop long.
        /// À la place : le payload est inclus en clair dans la clé étendue.
        /// Format clé étendue : "SHPM-XXXX-XXXX-XXXX-XXXX:PAYLOAD"
        /// Le bot envoie la clé sous cette forme, l'app extrait le payload.
        /// </summary>
        private static bool ValidateKey(string rawKey, out int? days)
        {
            days = null;
            if (string.IsNullOrWhiteSpace(rawKey)) return false;

            // Format étendu : "SHPM-XXXX-XXXX-XXXX-XXXX:SHPM|D90|ABCD1234"
            string key, payload;
            if (rawKey.Contains(':'))
            {
                var sep = rawKey.IndexOf(':');
                key     = rawKey[..sep].Trim();
                payload = rawKey[(sep + 1)..].Trim();
                return ValidateKeyWithPayload(key, payload, out days);
            }

            // Format court (rétrocompatibilité sans sel) : tester durées classiques
            key = rawKey;
            var b32part = key.Replace("SHPM-", "").Replace("-", "");
            if (b32part.Length != 16) return false;

            byte[] hashFromKey;
            try { hashFromKey = FromBase32(b32part); }
            catch { return false; }

            var durees     = new[] { 1, 7, 14, 30, 60, 90, 180, 365, 36500 };
            var candidates = new List<string> { "SHPM|PERM" };
            foreach (var d in durees) candidates.Add($"SHPM|D{d}");

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SECRET_KEY));
            foreach (var p in candidates)
            {
                if (hmac.ComputeHash(Encoding.UTF8.GetBytes(p))[..10].SequenceEqual(hashFromKey))
                {
                    if (p != "SHPM|PERM") days = int.Parse(p.Split('|')[1][1..]);
                    return true;
                }
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CHIFFREMENT DU FICHIER LICENCE (AES-256)
        // ══════════════════════════════════════════════════════════════════════

        private static byte[] DeriveKey()
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(
                Encoding.UTF8.GetBytes(SECRET_KEY + GetMachineId()));
        }

        private static string Encrypt(string plain)
        {
            using var aes = Aes.Create();
            aes.Key       = DeriveKey();
            aes.GenerateIV();
            using var enc = aes.CreateEncryptor();
            var data      = Encoding.UTF8.GetBytes(plain);
            var cipher    = enc.TransformFinalBlock(data, 0, data.Length);
            var result    = new byte[aes.IV.Length + cipher.Length];
            aes.IV.CopyTo(result, 0);
            cipher.CopyTo(result, aes.IV.Length);
            return Convert.ToBase64String(result);
        }

        private static string Decrypt(string base64)
        {
            var all    = Convert.FromBase64String(base64);
            using var aes = Aes.Create();
            aes.Key    = DeriveKey();
            aes.IV     = all[..16];
            using var dec = aes.CreateDecryptor();
            var plain  = dec.TransformFinalBlock(all, 16, all.Length - 16);
            return Encoding.UTF8.GetString(plain);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  REGISTRE WINDOWS — tracking des clés activées (anti-réutilisation)
        // ══════════════════════════════════════════════════════════════════════

        private enum RegistryCheckResult { NotFound, SameMachine, OtherMachine }

        /// <summary>Vérifie si la clé est déjà enregistrée dans le registre.</summary>
        private static RegistryCheckResult CheckRegistry(string cleanKey, string currentMachine)
        {
            try
            {
                using var regKey = Registry.CurrentUser.OpenSubKey(REG_PATH);
                if (regKey == null) return RegistryCheckResult.NotFound;

                // Clé registre = hash SHA256 de la clé courte (pas stocker la clé en clair)
                var keyHash = HashKey(cleanKey);
                var val = regKey.GetValue(keyHash) as string;
                if (string.IsNullOrEmpty(val)) return RegistryCheckResult.NotFound;

                // Format stocké : "machineId|expiryUtcTicks"
                var parts = val.Split('|');
                var storedMachine = parts[0];

                return storedMachine == currentMachine
                    ? RegistryCheckResult.SameMachine
                    : RegistryCheckResult.OtherMachine;
            }
            catch { return RegistryCheckResult.NotFound; }
        }

        /// <summary>Enregistre la clé dans le registre Windows.</summary>
        private static void SaveToRegistry(string cleanKey, string machineId, DateTime? expiresAt)
        {
            try
            {
                using var regKey = Registry.CurrentUser.CreateSubKey(REG_PATH);
                var keyHash   = HashKey(cleanKey);
                var ticks     = expiresAt.HasValue ? expiresAt.Value.Ticks.ToString() : "0";
                regKey.SetValue(keyHash, $"{machineId}|{ticks}");
            }
            catch { }
        }

        /// <summary>Récupère la date d'expiration depuis le registre.</summary>
        private static DateTime? GetRegistryExpiry(string cleanKey)
        {
            try
            {
                using var regKey = Registry.CurrentUser.OpenSubKey(REG_PATH);
                if (regKey == null) return null;
                var val = regKey.GetValue(HashKey(cleanKey)) as string;
                if (string.IsNullOrEmpty(val)) return null;
                var parts = val.Split('|');
                if (parts.Length < 2) return null;
                if (!long.TryParse(parts[1], out var ticks) || ticks == 0) return null;
                return new DateTime(ticks, DateTimeKind.Local);
            }
            catch { return null; }
        }

        private static string HashKey(string key)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(key)))[..16];
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ID MACHINE (lié au PC de l'acheteur)
        // ══════════════════════════════════════════════════════════════════════

        private static string GetMachineId()
        {
            try
            {
                // Utiliser le numéro de série du volume C: comme identifiant unique
                var drives = System.IO.DriveInfo.GetDrives();
                foreach (var d in drives)
                    if (d.Name == "C:\\" && d.IsReady)
                        return d.VolumeLabel + "_" + Environment.MachineName;
            }
            catch { }
            return Environment.MachineName;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  BASE32
        // ══════════════════════════════════════════════════════════════════════

        private const string BASE32_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        private static string ToBase32(byte[] data)
        {
            var sb = new StringBuilder();
            int buffer = data[0], next = 1, bitsLeft = 8;
            while (bitsLeft > 0 || next < data.Length)
            {
                if (bitsLeft < 5)
                {
                    if (next < data.Length) { buffer <<= 8; buffer |= data[next++]; bitsLeft += 8; }
                    else { buffer <<= 5 - bitsLeft; bitsLeft = 5; }
                }
                bitsLeft -= 5;
                sb.Append(BASE32_CHARS[(buffer >> bitsLeft) & 31]);
            }
            return sb.ToString();
        }

        private static byte[] FromBase32(string s)
        {
            var result = new List<byte>();
            int buffer = 0, bitsLeft = 0;
            foreach (var c in s.ToUpperInvariant())
            {
                var idx = BASE32_CHARS.IndexOf(c);
                if (idx < 0) throw new FormatException();
                buffer = (buffer << 5) | idx;
                bitsLeft += 5;
                if (bitsLeft >= 8) { bitsLeft -= 8; result.Add((byte)(buffer >> bitsLeft)); }
            }
            return result.ToArray();
        }

        private record StoredLicense
        {
            public string   Key         { get; init; } = "";
            public string   Payload     { get; init; } = ""; // payload HMAC stocké à l'activation
            public DateTime ActivatedAt { get; init; }
            public DateTime? ExpiresAt  { get; init; }       // null = permanent
            public string   MachineId  { get; init; } = "";
        }
    }
}
