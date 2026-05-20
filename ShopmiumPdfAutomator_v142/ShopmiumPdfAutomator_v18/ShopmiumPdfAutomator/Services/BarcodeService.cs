namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Utilitaires EAN-13 : validation et génération.
    /// La recherche automatique EAN a été supprimée (instable).
    /// L'utilisateur recherche manuellement via les boutons "Rechercher sur Auchan/Leclerc".
    /// </summary>
    public static class BarcodeService
    {
        public static bool IsValidEan13(string ean)
        {
            if (string.IsNullOrEmpty(ean) || ean.Length != 13) return false;
            if (!ean.All(char.IsDigit)) return false;
            return CalcCheckDigit(ean[..12]) == ean[12].ToString();
        }

        public static string GeneratePlausibleEan13(string productName)
        {
            var rnd     = new Random(productName.GetHashCode());
            var prefix  = (300 + rnd.Next(80)).ToString();
            var partial = prefix;
            while (partial.Length < 12) partial += rnd.Next(10).ToString();
            partial = partial[..12];
            return partial + CalcCheckDigit(partial);
        }

        private static string CalcCheckDigit(string twelve)
        {
            int sum = 0;
            for (int i = 0; i < 12; i++)
                sum += int.Parse(twelve[i].ToString()) * (i % 2 == 0 ? 1 : 3);
            return ((10 - sum % 10) % 10).ToString();
        }
    }
}
