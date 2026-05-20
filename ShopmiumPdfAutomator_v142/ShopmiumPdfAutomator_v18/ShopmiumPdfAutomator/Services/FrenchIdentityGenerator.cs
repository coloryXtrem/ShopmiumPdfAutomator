using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Génère des identités françaises réalistes (civilité + nom + prénom + adresse)
    /// pour remplir les calques du PSD Carrefour Drive.
    ///
    /// - Noms / prénoms : courants en France (INSEE)
    /// - Adresses : combinaison d'un nom de rue + ville + CP cohérents (toutes
    ///   les villes listées existent réellement avec leur vrai code postal)
    /// - Mémorise les 50 dernières identités générées pour éviter les répétitions
    ///   (clé Registre HKCU\Software\ShopmiumHelper\IdentityHistory)
    /// </summary>
    public static class FrenchIdentityGenerator
    {
        // ── Prénoms (masculins / féminins) ────────────────────────────────────
        // Source : prénoms courants nés en France entre 1980-2005 (INSEE)
        private static readonly string[] MaleFirstNames =
        {
            "Thomas", "Nicolas", "Julien", "Alexandre", "Maxime", "Antoine",
            "Romain", "Pierre", "Vincent", "Sébastien", "Mathieu", "Guillaume",
            "Florian", "Benjamin", "Olivier", "Jérémy", "Adrien", "Bastien",
            "Damien", "Fabien", "Lucas", "Hugo", "Théo", "Quentin",
            "Clément", "Baptiste", "Arthur", "Louis", "Paul", "Léo",
            "Nathan", "Étienne", "Raphaël", "Mathis", "Gabriel", "Enzo",
            "Mehdi", "Karim", "Yanis", "Samir", "Sofiane", "Yassine",
            "Christophe", "François", "Stéphane", "Frédéric", "Laurent", "Philippe",
            "Patrick", "Pascal", "David", "Bruno", "Éric", "Cédric"
        };

        private static readonly string[] FemaleFirstNames =
        {
            "Marie", "Sophie", "Camille", "Julie", "Caroline", "Mathilde",
            "Émilie", "Pauline", "Aurélie", "Laure", "Élodie", "Céline",
            "Sandrine", "Charlotte", "Audrey", "Marion", "Manon", "Léa",
            "Emma", "Lucie", "Chloé", "Sarah", "Anaïs", "Justine",
            "Margaux", "Clara", "Inès", "Jade", "Louise", "Alice",
            "Élise", "Romane", "Lola", "Ambre", "Léna", "Mila",
            "Nadia", "Yasmine", "Fatima", "Amina", "Leïla", "Sonia",
            "Catherine", "Isabelle", "Christine", "Nathalie", "Valérie", "Sylvie",
            "Patricia", "Véronique", "Brigitte", "Martine", "Anne", "Hélène"
        };

        // ── Noms de famille français courants ────────────────────────────────
        private static readonly string[] LastNames =
        {
            "Martin", "Bernard", "Dubois", "Thomas", "Robert", "Richard",
            "Petit", "Durand", "Leroy", "Moreau", "Simon", "Laurent",
            "Lefebvre", "Michel", "Garcia", "David", "Bertrand", "Roux",
            "Vincent", "Fournier", "Morel", "Girard", "André", "Lefèvre",
            "Mercier", "Dupont", "Lambert", "Bonnet", "François", "Martinez",
            "Legrand", "Garnier", "Faure", "Rousseau", "Blanc", "Guérin",
            "Muller", "Henry", "Roussel", "Nicolas", "Perrin", "Morin",
            "Mathieu", "Clément", "Gauthier", "Dumont", "Lopez", "Fontaine",
            "Chevalier", "Robin", "Masson", "Sanchez", "Gérard", "Nguyen",
            "Boyer", "Denis", "Lemaire", "Duval", "Joly", "Gautier",
            "Roger", "Roche", "Roy", "Noël", "Meyer", "Lucas",
            "Meunier", "Jean", "Perez", "Marchand", "Dufour", "Blanchard",
            "Marie", "Barbier", "Brun", "Dumas", "Brunet", "Schmitt",
            "Léger", "Renard", "Aubert", "Caron", "Hubert", "Royer",
            "Le Gall", "Cousin", "Picard", "Rolland", "Renaud", "Olivier"
        };

        // ── Noms de rues français ────────────────────────────────────────────
        // (sans "rue/avenue/place" → on ajoute le type devant)
        private static readonly string[] StreetNames =
        {
            "de la République", "de Paris", "de la Gare", "Victor Hugo",
            "Jean Jaurès", "Jules Ferry", "Pasteur", "Émile Zola",
            "du Général de Gaulle", "Gambetta", "de la Mairie", "de l'Église",
            "Carnot", "des Écoles", "Voltaire", "des Lilas",
            "des Acacias", "Berthelot", "de Verdun", "Aristide Briand",
            "Léon Gambetta", "Jean Moulin", "Anatole France", "de la Liberté",
            "des Tilleuls", "des Marronniers", "de Strasbourg", "de la Paix",
            "du Maréchal Foch", "des Roses", "Henri Barbusse", "Roger Salengro",
            "Jean Mermoz", "Edmond Rostand", "Saint-Antoine", "Saint-Martin",
            "du Bourg", "du Centre", "du Moulin", "des Jardins",
            "de la Fontaine", "des Prés", "des Champs", "de Bretagne",
            "de Bourgogne", "d'Alsace", "de Picardie", "de Provence"
        };

        // ── Villes françaises avec leur vrai code postal ─────────────────────
        // (sélection variée géographiquement)
        private static readonly (string city, string zip)[] Cities =
        {
            ("PARIS", "75001"), ("PARIS", "75011"), ("PARIS", "75015"),
            ("LYON", "69001"), ("LYON", "69003"), ("LYON", "69007"),
            ("MARSEILLE", "13001"), ("MARSEILLE", "13008"),
            ("TOULOUSE", "31000"), ("TOULOUSE", "31200"),
            ("NICE", "06000"), ("NICE", "06200"),
            ("NANTES", "44000"), ("NANTES", "44300"),
            ("STRASBOURG", "67000"), ("STRASBOURG", "67100"),
            ("MONTPELLIER", "34000"), ("MONTPELLIER", "34080"),
            ("BORDEAUX", "33000"), ("BORDEAUX", "33200"),
            ("LILLE", "59000"), ("LILLE", "59800"),
            ("RENNES", "35000"), ("RENNES", "35700"),
            ("REIMS", "51100"), ("LE HAVRE", "76600"),
            ("SAINT-ÉTIENNE", "42000"), ("TOULON", "83000"),
            ("ANGERS", "49000"), ("GRENOBLE", "38000"),
            ("DIJON", "21000"), ("NÎMES", "30000"),
            ("AIX-EN-PROVENCE", "13100"), ("BREST", "29200"),
            ("LE MANS", "72000"), ("AMIENS", "80000"),
            ("TOURS", "37000"), ("LIMOGES", "87000"),
            ("CLERMONT-FERRAND", "63000"), ("VILLEURBANNE", "69100"),
            ("METZ", "57000"), ("BESANÇON", "25000"),
            ("PERPIGNAN", "66000"), ("ORLÉANS", "45000"),
            ("CAEN", "14000"), ("MULHOUSE", "68100"),
            ("ROUEN", "76000"), ("NANCY", "54000"),
            ("ARGENTEUIL", "95100"), ("MONTREUIL", "93100"),
            ("VERSAILLES", "78000"), ("CRÉTEIL", "94000"),
            ("NANTERRE", "92000"), ("BOULOGNE-BILLANCOURT", "92100"),
            ("IVRY-SUR-SEINE", "94200"), ("VITRY-SUR-SEINE", "94400"),
            ("VINCENNES", "94300"), ("ASNIÈRES-SUR-SEINE", "92600"),
            ("COURBEVOIE", "92400"), ("COLOMBES", "92700"),
            ("RUEIL-MALMAISON", "92500"), ("LEVALLOIS-PERRET", "92300"),
            ("ISSY-LES-MOULINEAUX", "92130"), ("MEUDON", "92190"),
            ("CHÂTILLON", "92320"), ("VANVES", "92170"),
            ("CHAMPIGNY-SUR-MARNE", "94500"), ("SAINT-DENIS", "93200"),
            ("AUBERVILLIERS", "93300"), ("PANTIN", "93500"),
        };

        // ── Type de voie ─────────────────────────────────────────────────────
        private static readonly string[] StreetTypes =
        {
            "rue", "rue", "rue", "rue",          // rue est très majoritaire
            "avenue", "avenue",
            "boulevard",
            "place",
            "allée",
            "impasse",
            "chemin",
        };

        // ── Génération ───────────────────────────────────────────────────────

        /// <summary>
        /// Résultat de génération : 6 champs séparés pour remplir le PSD Drive.
        /// </summary>
        public class GeneratedIdentity
        {
            public string Title      { get; set; } = "MR";           // calque "MME"
            public string FullName   { get; set; } = "";              // calque "BEN YOUSSEF Nesrine"
            public string StreetNum  { get; set; } = "12";            // calque "12"
            public string StreetType { get; set; } = "rue";           // calque "rue"
            public string StreetName { get; set; } = "";              // calque "gaston monmousseau"
            public string Country    { get; set; } = "FRANCE";        // calque "FRANCE"
            public string ZipAndCity { get; set; } = "";              // calque "94200 IVRY SUR SEINE"

            public override string ToString() =>
                $"{Title} {FullName}, {StreetNum} {StreetType} {StreetName}, {ZipAndCity}";
        }

        private const string HistoryKey = @"Software\ShopmiumHelper\IdentityHistory";
        private const int    HistoryMax = 50;

        public static GeneratedIdentity Generate()
        {
            var rng = new Random(Guid.NewGuid().GetHashCode());

            // Charger l'historique pour éviter les répétitions
            var history = LoadHistory();

            GeneratedIdentity id;
            int attempts = 0;
            do
            {
                id = GenerateOne(rng);
                attempts++;
            }
            while (history.Contains(id.ToString()) && attempts < 20);

            // Ajouter à l'historique
            SaveToHistory(id.ToString());
            return id;
        }

        private static GeneratedIdentity GenerateOne(Random rng)
        {
            bool isMale = rng.Next(2) == 0;
            var firstName = isMale
                ? MaleFirstNames[rng.Next(MaleFirstNames.Length)]
                : FemaleFirstNames[rng.Next(FemaleFirstNames.Length)];
            var lastName = LastNames[rng.Next(LastNames.Length)];

            // Format Carrefour Drive : "NOM Prénom" (nom en MAJUSCULES, prénom Capitalisé)
            var fullName = $"{lastName.ToUpper()} {firstName}";

            var streetNum  = rng.Next(1, 200).ToString();
            var streetType = StreetTypes[rng.Next(StreetTypes.Length)];
            var streetName = StreetNames[rng.Next(StreetNames.Length)];

            var (city, zip) = Cities[rng.Next(Cities.Length)];
            var zipAndCity = $"{zip} {city}";

            return new GeneratedIdentity
            {
                Title      = isMale ? "MR" : "MME",
                FullName   = fullName,
                StreetNum  = streetNum,
                StreetType = streetType,
                StreetName = streetName,
                Country    = "FRANCE",
                ZipAndCity = zipAndCity,
            };
        }

        // ── Historique anti-doublons ─────────────────────────────────────────

        private static HashSet<string> LoadHistory()
        {
            var set = new HashSet<string>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(HistoryKey);
                if (key?.GetValue("Identities") is string[] arr)
                    foreach (var s in arr) set.Add(s);
            }
            catch { }
            return set;
        }

        private static void SaveToHistory(string identity)
        {
            try
            {
                var list = LoadHistory().ToList();
                list.Add(identity);
                if (list.Count > HistoryMax)
                    list = list.Skip(list.Count - HistoryMax).ToList();

                using var key = Registry.CurrentUser.CreateSubKey(HistoryKey);
                key.SetValue("Identities", list.ToArray(),
                    RegistryValueKind.MultiString);
            }
            catch { }
        }

        /// <summary>Efface l'historique des identités générées.</summary>
        public static void ClearHistory()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(HistoryKey, true);
                key?.DeleteValue("Identities", false);
            }
            catch { }
        }
    }
}
