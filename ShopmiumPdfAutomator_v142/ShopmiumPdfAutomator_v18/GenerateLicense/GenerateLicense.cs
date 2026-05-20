using System.Security.Cryptography;
using System.Text;

// ============================================================
// OUTIL VENDEUR — Génération de clés de licence
// NE PAS DISTRIBUER — usage interne uniquement
// ============================================================

const string SECRET_KEY = "REMPLACE_PAR_TA_CLE_SECRETE_64_CHARS_MIN_OBLIGATOIRE";
const string BASE32      = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== SHOPMIUM — GENERATEUR DE LICENCES ===\n");
Console.WriteLine("1. Licence permanente");
Console.WriteLine("2. Licence avec expiration");
Console.Write("Choix : ");
var choix = Console.ReadLine();

DateTime? expiry = null;
if (choix == "2")
{
    Console.Write("Date expiration (jj/mm/aaaa) : ");
    var input = Console.ReadLine() ?? "";
    if (DateTime.TryParseExact(input, "dd/MM/yyyy",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var d))
        expiry = d;
    else { Console.WriteLine("Date invalide."); return; }
}

Console.Write("Combien de cles ? (1) : ");
int n = int.TryParse(Console.ReadLine(), out var ni) ? ni : 1;

Console.WriteLine("\n=== CLES GENEREES ===");
for (int i = 0; i < n; i++)
    Console.WriteLine(GenerateKey(expiry));

Console.WriteLine(expiry.HasValue
    ? $"\nExpire le : {expiry.Value:dd/MM/yyyy}"
    : "\nLicence permanente");

Console.WriteLine("\nEntree pour fermer...");
Console.ReadLine();

static string GenerateKey(DateTime? expiry)
{
    var payload = expiry.HasValue ? $"SHPM|{expiry.Value:yyyyMMdd}" : "SHPM|PERM";
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SECRET_KEY));
    var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    var b32  = ToBase32(hash[..10]);
    return $"SHPM-{b32[..4]}-{b32[4..8]}-{b32[8..12]}-{b32[12..16]}";
}

static string ToBase32(byte[] data)
{
    var sb = new StringBuilder();
    int buf = data[0], next = 1, left = 8;
    while (left > 0 || next < data.Length)
    {
        if (left < 5) { if (next < data.Length) { buf <<= 8; buf |= data[next++]; left += 8; } else { buf <<= 5 - left; left = 5; } }
        left -= 5; sb.Append(BASE32[(buf >> left) & 31]);
    }
    return sb.ToString();
}
