using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShopmiumPdfAutomator.Services
{
    public static class TvaCalculator
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
        public static string? OpenAiKey { get; set; }

        public record TvaResult(double Rate, bool Certain);

        // ============================================================
        //  EXTRACTION DU RAYON DEPUIS LE HTML
        // ============================================================
        public static string? ExtractRayonFromHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;

            var m = Regex.Match(html,
                @"[Aa]u\s+rayon\s+([^<\r\n]{2,120})",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Value.Trim();

            m = Regex.Match(html,
                @"[Rr]ayon\s*:\s*([^<\r\n]{2,120})",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Value.Trim();

            m = Regex.Match(html,
                @"[Rr]ayon\s+([^<\r\n]{2,120})",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Value.Trim();

            return null;
        }

        // ============================================================
        //  DÉTECTION PRINCIPALE — depuis le rayon
        // ============================================================
        public static TvaResult DetectFromRayon(string? rayonText)
        {
            if (string.IsNullOrWhiteSpace(rayonText))
                return new(0.055, Certain: false);

            var r = Norm(rayonText);

            foreach (var kw in _medic)
                if (r.Contains(Norm(kw))) return new(0.021, true);

            // Exception : "sans alcool" / "non alcoolisé" → alimentaire 5.5%
            // (doit être testé AVANT la liste _alcool car "alcool" est dans le mot)
            if (r.Contains("sans alcool") || r.Contains("non alcoolise") ||
                r.Contains("sans teneur en alcool") || r.Contains("0% alcool"))
                return new(0.055, true);

            foreach (var kw in _alcool)
                if (r.Contains(Norm(kw))) return new(0.20, true);

            foreach (var kw in _hygiene)
                if (r.Contains(Norm(kw))) return new(0.20, true);

            // Alimentaire explicite → certain
            foreach (var kw in _alim)
                if (r.Contains(Norm(kw))) return new(0.055, true);

            // Rayon présent mais non classé → alimentaire certain
            // (sur Shopmium tout rayon non-alcool/hygiène = alimentaire)
            if (r.Contains("rayon"))
                return new(0.055, true);

            return new(0.055, Certain: false);
        }

        public static TvaResult DetectWithConfidence(string productName, string? rayonText = null)
        {
            if (!string.IsNullOrWhiteSpace(rayonText))
            {
                var r = DetectFromRayon(rayonText);
                if (r.Certain) return r;
            }
            return new(0.055, Certain: false);
        }

        public static double Detect(string productName) => 0.055;

        public static async Task<double> DetectAsync(string productName, string? rayonText = null)
        {
            var r1 = DetectWithConfidence(productName, rayonText);
            if (r1.Certain) return r1.Rate;

            try { var r = await TryOpenFoodFacts(productName); if (r.HasValue) return r.Value; }
            catch { }

            if (!string.IsNullOrWhiteSpace(OpenAiKey))
            {
                try { var r = await DetectWithGpt(productName, OpenAiKey); if (r.HasValue) return r.Value; }
                catch { }
            }

            return 0.055;
        }

        // ============================================================
        //  2.1% — MÉDICAMENTS REMBOURSABLES
        // ============================================================
        private static readonly string[] _medic =
        [
            // Termes génériques
            "medicament", "médicament", "medicaments", "médicaments",
            "pharmacie", "pharmaceutique",
            "remboursable", "remboursables",
            "ordonnance",
            // Compléments alimentaires (2.1% si remboursables, sinon 5.5%)
            "complement alimentaire", "complément alimentaire",
            "complements alimentaires", "compléments alimentaires",
            // Parapharmacie remboursable
            "parapharmacie",
            // Vitamines médicamenteuses
            "vitamine therapeutique", "vitamine thérapeutique",
            // Produits homéopathiques remboursables
            "homeopathique", "homéopathique",
            // Mots indicateurs rayon pharmacie
            "rayon pharmacie", "rayon parapharmacie",
            "rayon sante", "rayon santé",
        ];

        // ============================================================
        //  20% — ALCOOL
        // ============================================================
        private static readonly string[] _alcool =
        [
            // ── Vins ──────────────────────────────────────────────────
            "vin tranquille", "vins tranquilles",
            "vin rouge", "vins rouges",
            "vin blanc", "vins blancs",
            "vin rose", "vins roses", "vin rosé", "vins rosés",
            "vin mousseux", "vins mousseux",
            "vin petillant", "vin pétillant",
            "vin de bordeaux", "vins de bordeaux",
            "vin de bourgogne", "vins de bourgogne",
            "vin de provence", "vins de provence",
            "vin du rhone", "vins du rhone", "vin du rhône",
            "vin de loire", "vins de loire",
            "vin d alsace", "vins d alsace",
            "vin du languedoc", "vin du roussillon",
            "vin du sud ouest",
            "aoc bordeaux", "aop bordeaux",
            "aoc bourgogne", "aop bourgogne",
            "aoc provence", "aop provence",
            "aoc rhone", "aop rhone", "aoc rhône",
            "aoc alsace", "aop alsace",
            "aoc loire", "aop loire",
            "aoc languedoc",
            "appellation bordeaux",
            "appellation controlee", "appellation contrôlée",
            "grand cru", "premier cru",
            "cotes du rhone", "côtes du rhône",
            "cotes de provence", "côtes de provence",
            "bordeaux superieur",
            "saint emilion", "saint-emilion",
            "pomerol", "pauillac", "margaux", "sauternes",
            "muscadet", "sancerre", "pouilly fume",
            "riesling", "gewurztraminer", "pinot gris",
            "chardonnay", "sauvignon", "merlot", "cabernet",
            "grenache", "syrah", "mourvedre",
            // ── Champagne & Pétillants ─────────────────────────────────
            "champagne",
            "prosecco", "cava",
            "cremant", "crémant",
            "mousseux",
            "petillant", "pétillant",
            "methode champenoise", "méthode champenoise",
            // ── Bières ────────────────────────────────────────────────
            "biere", "bieres", "bière", "bières",
            "beer", "beers",
            "ale", "stout", "lager", "porter", "ipa",
            "blonde", "brune", "ambre", "ambree", "ambrée",
            "blanche biere", "blanche alcool",
            "brasserie", "brassee", "brassée",
            "craft beer", "biere artisanale", "bière artisanale",
            "kronenbourg", "heineken", "leffe", "grimbergen",
            "hoegaarden", "stella artois", "corona",
            "1664", "pelforth", "desperados",
            "fisher", "meteor", "alsacienne",
            // ── Cidre & Poiré ──────────────────────────────────────────
            "cidre", "cidres",
            "poire alcool", "poiré alcool",
            "pommeau",
            // ── Spiritueux ─────────────────────────────────────────────
            "whisky", "whiskey", "whiskeys", "whiskies",
            "bourbon", "scotch", "single malt",
            "vodka",
            "rhum", "rum", "rhums",
            "gin",
            "cognac", "armagnac", "calvados", "marc",
            "tequila", "mezcal",
            "sake",
            "grappa",
            // ── Liqueurs & Apéritifs ───────────────────────────────────
            "liqueur", "liqueurs",
            "cointreau", "baileys", "amaretto", "kahlua",
            "triple sec", "creme de",
            "aperitif", "apéritif", "apero", "apéro",
            "pastis", "ricard", "pernod", "51 ",
            "porto", "port ",
            "vermouth", "martini alcool",
            "sangria",
            "punch alcool",
            // ── Termes génériques alcool ───────────────────────────────
            "alcool", "alcools",
            "spiritueux",
            "boisson alcoolisee", "boisson alcoolisée",
            "boissons alcoolisees", "boissons alcoolisées",
            "rayon alcool", "rayon alcools",
            "rayon vins", "rayon vin ",
            "cave a vin", "cave à vin",
            "vinification", "viticole", "viticulture",
            "domaine viticole", "chateau ", "château ",
            " vin ",      // vin seul entouré d'espaces
            "vins ",      // vins en début/fin
        ];

        // ============================================================
        //  20% — HYGIÈNE, ENTRETIEN, COSMÉTIQUES, BÉBÉ NON-ALIM
        // ============================================================
        private static readonly string[] _hygiene =
        [
            // ── Papier hygiénique & essuie-tout ───────────────────────
            "papier hygienique", "papier hygiénique",
            "papier toilette", "papier wc",
            "essuie-tout", "essuie tout", "sopalin",
            "mouchoir", "mouchoirs", "kleenex",
            "papier absorbant",
            "coton demaquillant", "coton démaquillant",
            "coton tige", "coton-tige", "cotons tiges",
            "disque demaquillant", "disque démaquillant",
            "lingette demaquillante", "lingette démaquillante",
            // ── Couches & bébé non-alimentaire ────────────────────────
            "couche ", "couches ",
            "couche culotte", "couche-culotte",
            "changes complets", "change complet",
            "culotte apprentissage",
            "pampers", "huggies", "libero", "dodot",
            "love and green couche", "love&green couche",
            "lingette bebe", "lingette bébé",
            "lingettes bebe", "lingettes bébé",
            "lingette ", "lingettes ",
            "waterwipes", "water wipes",
            "creme bebe", "crème bébé",
            "creme de change", "crème de change",
            "baume bebe", "baume bébé",
            "talc bebe", "soin bebe", "soin bébé",
            // ── Hygiène corporelle ─────────────────────────────────────
            "shampoing", "shampooing", "shampoo",
            "apres-shampoing", "après-shampoing", "conditionneur",
            "gel douche", "gel lavant", "gel bain",
            "mousse bain", "bain douche",
            "savon liquide", "savon corps", "pain de savon",
            "pain dermatologique", "pain surgras",
            "deodorant", "déodorant", "déo ",
            "anti-transpirant", "antitranspirant",
            "roll-on", "spray deodorant",
            "dentifrice", "dentifrice",
            "bain de bouche", "rince-bouche",
            "fil dentaire", "brossette interdentaire",
            "brosse a dents", "brosse à dents",
            "rasoir", "rasoirs",
            "mousse a raser", "gel a raser", "gel de rasage",
            "after shave", "apres rasage", "après-rasage",
            "lame de rasoir", "cartouche rasoir",
            "epilateur", "épilateur", "cire epilatoire",
            "serviette hygienique", "serviette hygiénique",
            "tampon hygienique", "tampon hygiénique",
            "protege slip", "protège-slip",
            "coupe menstruelle", "culotte menstruelle",
            // ── Cosmétiques & soins visage/corps ──────────────────────
            "cosmetique", "cosmétique", "cosmétiques",
            "soin visage", "soin corps", "soin peau",
            "creme visage", "crème visage",
            "creme corps", "crème corps",
            "creme hydratante", "crème hydratante",
            "creme anti age", "crème anti-âge",
            "creme solaire", "crème solaire", "solaire",
            "lait corporel", "lait corps",
            "beurre corps", "huile corps", "huile soin",
            "serum visage", "sérum visage",
            "serum corps", "sérum corps",
            "lotion tonique", "lotion visage",
            "eau micellaire", "demaquillant", "démaquillant",
            "nettoyant visage", "gel nettoyant",
            "masque visage", "masque soin",
            "gommage visage", "gommage corps",
            "contour yeux", "contour des yeux",
            "fond de teint", "bb cream", "cc cream",
            "correcteur", "anti-cernes",
            "mascara", "eye liner", "eyeliner",
            "fard a paupieres", "fard à paupières",
            "rouge a levres", "rouge à lèvres", "gloss",
            "blush", "poudre teint", "bronzer",
            "vernis a ongles", "vernis à ongles",
            "base de teint", "fixateur maquillage",
            "demaquillant", "démaquillant",
            // ── Parfumerie ─────────────────────────────────────────────
            "parfum", "parfums",
            "eau de toilette",
            "eau de parfum",
            "eau de cologne",
            "fragrance",
            // ── Entretien maison ───────────────────────────────────────
            "lessive", "lessives",
            "lessive liquide", "lessive poudre",
            "lessive capsule", "lessive dose",
            "ariel", "skip", "persil", "dash", "le chat",
            "super croix", "omo",
            "adoucissant", "assouplissant",
            "lenor", "soupline", "downy",
            "liquide vaisselle", "liquide pour vaisselle",
            "pastille lave vaisselle", "tablette lave vaisselle",
            "tablette lave-vaisselle",
            "sel lave vaisselle", "sel pour lave-vaisselle",
            "liquide rincage", "liquide rinçage",
            "nettoyant menager", "nettoyant ménager",
            "produit menager", "produit ménager",
            "nettoyant multi usage", "nettoyant multi-usage",
            "spray nettoyant", "lingette nettoyante",
            "desinfectant", "désinfectant",
            "gel wc", "gel toilettes",
            "detartrant", "détartrant",
            "deboucheur", "déboucheur",
            "nettoyant sol", "nettoyant cuisine",
            "nettoyant salle de bain",
            "produit entretien", "entretien maison",
            "ajax", "cif", "mr propre", "mr. propre",
            "cillit", "harpic", "destop", "duck",
            "febreze", "febrèze",
            "insecticide", "antimite", "anti-mite",
            "chasse insectes",
            // ── Rayon hygiène générique ────────────────────────────────
            "rayon hygiene", "rayon hygiène",
            "rayon beaute", "rayon beauté",
            "rayon cosmetique", "rayon cosmétique",
            "rayon entretien",
            "rayon bebe non alim", "rayon bébé",
            "rayon papier",
            "hygiene bebe", "hygiène bébé",
            "hygiene corporelle", "hygiène corporelle",
            "hygiene bucco", "hygiène bucco",
        ];

        // ============================================================
        //  5.5% — ALIMENTAIRE (liste explicite pour les cas clairs)
        // ============================================================
        private static readonly string[] _alim =
        [
            // ── Produits laitiers ──────────────────────────────────────
            "produit laitier", "produits laitiers",
            "yaourt", "yaourts", "yogurt", "yogourt",
            "fromage", "fromages",
            "beurre", "beurres",
            "creme fraiche", "crème fraîche",
            "creme dessert", "crème dessert",
            "lait uht", "lait demi", "lait entier",
            "lait ecreme", "lait écrémé",
            "lait de vache", "lait de brebis", "lait de chevre",
            "emmental", "gruyere", "comté", "comte",
            "camembert", "brie", "roquefort",
            "mozzarella", "parmesan", "gouda", "edam",
            "kiri", "vache qui rit", "babybel",
            "boursin", "laughing cow",
            "creme glacee", "crème glacée",
            "riz au lait", "flan", "ile flottante",
            "mousse au chocolat",
            // ── Lait infantile / bébé alimentaire ─────────────────────
            "lait infantile", "lait nourrisson",
            "lait maternise", "lait maternisé",
            "lait 1er age", "lait 2eme age", "lait 3eme age",
            "lait de croissance", "lait bebe", "lait bébé",
            "preparation nourrisson", "préparation nourrisson",
            "formule infantile",
            "novalac", "aptamil", "guigoz", "milumel",
            "bledina lait", "nutrilon",
            "bebe alimentation", "bébé alimentation",
            "compote bebe", "compote bébé",
            "petits pots bebe", "petits pots bébé",
            "purée bébé", "puree bebe",
            "cereales bebe", "céréales bébé",
            // ── Épicerie sucrée ────────────────────────────────────────
            "epicerie", "épicerie",
            "chocolat", "chocolats",
            "tablette chocolat",
            "pate a tartiner", "pâte à tartiner",
            "nutella", "nocciolata", "speculoos",
            "confiture", "confitures",
            "miel", "miels",
            "sucre", "sucres",
            "bonbon", "bonbons", "confiserie", "confiseries",
            "caramel", "caramels",
            "chewing gum",
            "biscuit", "biscuits",
            "gateau", "gâteau", "gateaux", "gâteaux",
            "cookie", "cookies",
            "crackers", "cracotte",
            "pain d epices", "pain d'épices",
            "madeleine", "madeleines",
            "quatre quart", "quatre-quart",
            "brioche", "brioches",
            "cereales petit dejeuner", "céréales petit déjeuner",
            "muesli", "granola", "porridge",
            "barre cereales", "barre de céréales",
            // ── Épicerie salée ─────────────────────────────────────────
            "pates", "pâtes",
            "spaghetti", "tagliatelles", "fusilli", "rigatoni",
            "riz ", " riz",
            "farine", "farines",
            "semoule", "polenta", "quinoa",
            "lentille", "lentilles",
            "haricot", "haricots",
            "pois chiche", "pois chiches",
            "feve", "fèves",
            "conserve", "conserves",
            "boite de conserve",
            "sauce tomate", "coulis tomate",
            "concentre tomate", "concentré de tomate",
            "ketchup", "mayonnaise", "moutarde",
            "vinaigre", "vinaigres",
            "huile olive", "huile d olive", "huile d'olive",
            "huile tournesol", "huile colza", "huile alimentaire",
            "sel alimentaire", "sel cuisine",
            "soupe", "soupes", "veloutee", "velouté",
            "bouillon", "bouillons",
            "chips", "tuiles", "apero snack", "biscuits apero",
            "cacahuetes", "cacahuètes", "noix de cajou",
            "amande", "amandes", "noisette", "noisettes",
            "pignons", "noix",
            // ── Viandes & charcuterie ──────────────────────────────────
            "viande", "viandes",
            "boeuf", "bœuf",
            "poulet", "porc", "veau", "agneau", "canard",
            "jambon", "jambons",
            "saucisson", "saucisse", "saucisses",
            "charcuterie", "charcuteries",
            "pate de campagne", "pâté de campagne",
            "rillette", "rillettes",
            "bacon", "lardons",
            "knack", "knacks",
            "merguez", "chipolata",
            "steak hache", "steak haché",
            "escalope", "filet de poulet",
            // ── Poissons & fruits de mer ───────────────────────────────
            "poisson", "poissons",
            "saumon", "thon", "cabillaud", "colin",
            "lieu noir", "merlu",
            "sardine", "sardines",
            "hareng", "maquereau",
            "crevette", "crevettes",
            "moule", "moules",
            "coquille saint jacques",
            "noix de saint jacques",
            "surimi",
            "filet de poisson",
            // ── Fruits & Légumes ───────────────────────────────────────
            "fruits et legumes", "fruits et légumes",
            "fruit frais", "fruits frais",
            "legume frais", "légume frais",
            "legumes frais", "légumes frais",
            "compote", "compotes",
            "jus de fruits", "jus de fruit",
            "purée de fruits", "puree de fruits",
            "confiture fruits",
            // ── Boulangerie & Viennoiserie ─────────────────────────────
            "boulangerie", "viennoiserie", "viennoiseries",
            "pain ", " pain",
            "baguette", "baguettes",
            "croissant", "croissants",
            "pain au chocolat", "chausson aux pommes",
            "brioche", "pain de mie",
            "biscottes", "pain grille", "pain grillé",
            "pain complet", "pain aux cereales",
            // ── Surgelés alimentaires ──────────────────────────────────
            "surgele", "surgelé", "surgeles", "surgelés",
            "legumes surgeles", "légumes surgelés",
            "poisson surgele", "poisson surgelé",
            "plat surgele", "plat surgelé",
            "pizza surgelee", "pizza surgelée",
            "glace", "glaces", "sorbet", "sorbets",
            "creme glacee", "crème glacée",
            // ── Boissons non-alcoolisées ───────────────────────────────
            "jus de fruit", "jus de fruits",
            "nectar", "nectars",
            "sirop de fruit", "sirop alimentaire",
            "limonade", "grenadine",
            "soda", "sodas",
            "coca cola", "pepsi", "orangina", "sprite",
            "fanta", "7up", "schweppes",
            "oasis", "capri-sun", "caprisun",
            "tropicana", "innocent", "joker",
            "pressade", "andros jus",
            "danao",
            "boisson fruitee", "boisson fruitée",
            "boisson lactee", "boisson lactée",
            "boisson aux fruits",
            "boisson vegetale", "boisson végétale",
            "boisson amande", "lait amande",
            "boisson soja", "lait soja",
            "boisson avoine", "lait avoine",
            "boisson riz", "lait riz",
            "boisson cereale", "boisson céréale",
            "alpro", "bjorg boisson",
            "the froid", "thé froid", "ice tea", "iced tea",
            "lipton", "fuze tea",
            "kombucha",
            "eau de source", "eau minerale", "eau minérale",
            "eau plate", "eau gazeuse",
            "evian", "volvic", "badoit", "perrier",
            "san pellegrino", "vittel", "contrex",
            "hepar", "salvetat",
            "boisson energetique", "boisson énergétique",
            "isotonique",
            "the chaud", "thé ", " thé",
            "tisane", "infusion",
            "cafe ", " café", "cafe soluble", "café soluble",
            "capsule cafe", "capsule café",
            "nespresso", "dolce gusto",
            "cappuccino", "nescafe", "nescafé",
            "senseo", "malongo",
            "chocolat chaud", "chocolat en poudre",
            "nesquik", "milo", "ovomaltine", "ricoré",
            // ── Traiteur & plats préparés froids ──────────────────────
            "traiteur", "plat cuisine", "plat cuisiné",
            "salade composee", "salade composée",
            "sandwich", "sandwichs",
            "quiche", "quiches",
            "tarte salee", "tarte salée",
            "pizza fraiche", "pizza fraîche",
            "lasagne", "lasagnes",
            "gratin", "gratins",
            // ── Rayon alimentaire générique ────────────────────────────
            "rayon alimentaire",
            "rayon epicerie", "rayon épicerie",
            "rayon sucre", "rayon sucré",
            "rayon sale", "rayon salé",
            "rayon cremerie", "rayon crèmerie",
            "rayon boucherie",
            "rayon charcuterie",
            "rayon poissonnier", "rayon poissonnerie",
            "rayon boulangerie",
            "rayon surgeles", "rayon surgelés",
            "rayon boissons",
            "rayon boissons sans alcool",
            "rayon jus",
            "rayon petit dejeuner", "rayon petit déjeuner",
            "rayon cafe", "rayon café",
            "rayon the", "rayon thé",
            "rayon conserves",
            "rayon pates", "rayon pâtes",
            "rayon riz",
            "rayon condiments",
            "rayon sauces",
            "rayon chocolat",
            "rayon confiserie",
            "rayon biscuit", "rayon biscuits",
            "rayon snack",
            "rayon fromage", "rayon fromages",
            "rayon yaourt", "rayon yaourts",
            "rayon lait", "rayon laits",
            "rayon beurre",
            "rayon oeuf", "rayon oeufs", "rayon œufs",
            "rayon traiteur",
            "rayon viande", "rayon viandes",
            "rayon poisson",
            "rayon fruits",
            "rayon legumes", "rayon légumes",
            "rayon bio",
            "rayon vegan",
            "rayon dietetique", "rayon diététique",
            "rayon monde",
            "rayon asiatique",
            "rayon mexicain",
            "rayon italien",
        ];

        // ============================================================
        //  OPEN FOOD FACTS
        // ============================================================
        private static async Task<double?> TryOpenFoodFacts(string productName)
        {
            var q   = Uri.EscapeDataString(productName);
            var url = $"https://world.openfoodfacts.org/cgi/search.pl" +
                      $"?search_terms={q}&search_simple=1&action=process" +
                      $"&json=1&page_size=3&lc=fr&cc=fr" +
                      $"&fields=categories_tags,labels_tags,pnns_groups_1";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("products", out var prods)
                || prods.GetArrayLength() == 0) return null;
            var tags = (GetStr(prods[0], "categories_tags") + " " +
                        GetStr(prods[0], "labels_tags") + " " +
                        GetStr(prods[0], "pnns_groups_1")).ToLowerInvariant();
            if (tags.Contains("alcoholic") || tags.Contains("wines") ||
                tags.Contains("beers")     || tags.Contains("spirits")) return 0.20;
            if (tags.Contains("cosmetics") || tags.Contains("toiletries")) return 0.20;
            if (tags.Contains("en:foods")  || tags.Contains("beverages") ||
                tags.Contains("dairies")   || tags.Contains("cereals"))   return 0.055;
            return null;
        }

        // ============================================================
        //  GPT-4o-mini
        // ============================================================
        private static async Task<double?> DetectWithGpt(string productName, string apiKey)
        {
            var body = new
            {
                model = "gpt-4o-mini", max_tokens = 10, temperature = 0,
                messages = new[]
                {
                    new { role = "system", content =
                        "French VAT 2026: 20=alcohol/hygiene/cosmetics, 10=hot food, " +
                        "5.5=food/non-alcoholic drinks, 2.1=reimbursable meds. Reply ONLY: 20, 10, 5.5, or 2.1" },
                    new { role = "user", content = $"VAT for: {productName}" }
                }
            };
            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var answer = doc.RootElement
                .GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString()?.Trim() ?? "";
            return answer.Trim('.', '%', ' ') switch
            {
                "2.1" or "2,1" => 0.021,
                "5.5" or "5,5" => 0.055,
                "10"           => 0.10,
                "20"           => 0.20,
                _              => null
            };
        }

        // ============================================================
        //  UTILITAIRES
        // ============================================================
        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var d  = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (char c in d)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                    != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        private static string GetStr(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v)) return "";
            return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString();
        }

        public static string Format(double rate) =>
            rate == 0.021 ? "2,1%" :
            rate == 0.055 ? "5,5%" :
            rate == 0.10  ? "10,0%" : "20,0%";

        public static double CalculateTvaAmount(double totalTtc, double rate) =>
            Math.Round(totalTtc * rate / (1.0 + rate), 2);
    }
}
