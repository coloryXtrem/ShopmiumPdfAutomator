using System.Linq;
namespace ShopmiumPdfAutomator.Models
{
    /// <summary>
    /// Type de ticket/facture à utiliser pour cette offre.
    /// Détecté automatiquement via le warning popup du HTML produit.
    /// </summary>
    public enum TicketType
    {
        Standard,        // template.psd (ticket classique, magasin physique)
        Leclerc,         // leclerc.psd
        CarrefourDrive,  // carrefour-drive.psd (commande Drive)
    }

    /// <summary>
    /// Type de preuve photo exige par Shopmium pour cette offre.
    /// </summary>
    public enum ProofType
    {
        None,
        Ticket,
        PhotoArticles,
        PhotoBarcodeRaye,
        PhotoEtiquettePrix,
        PhotoEmballage,
        PhotoArticlesEtTicket,
        PhotoProduitHorsEmballage,    // Photo du produit EN DEHORS de son emballage
        PhotoEmballageDecoupe,        // Photo emballage découpé au niveau du code-barres
    }

    /// <summary>
    /// Une exigence de preuve : type classifié + texte exact Shopmium.
    /// Le texte brut est injecté dans le prompt IA pour coller exactement
    /// à ce que Shopmium demande.
    /// </summary>
    public class ProofRequirement
    {
        public ProofType Type     { get; set; }
        public string    RawText  { get; set; } = string.Empty; // texte exact de la page
        public string    Label    { get; set; } = string.Empty; // label lisible pour le bouton

        public static ProofRequirement From(ProofType type, string rawText)
        {
            var label = rawText.Length > 0 ? rawText : DescribeDefault(type);
            // Capitaliser + tronquer si trop long
            if (label.Length > 60) label = label[..57] + "...";
            label = char.ToUpperInvariant(label[0]) + label[1..];
            return new ProofRequirement
            {
                Type    = type,
                RawText = rawText,
                Label   = label
            };
        }

        private static string DescribeDefault(ProofType t) => t switch
        {
            ProofType.PhotoArticles         => "Photo de tous les articles",
            ProofType.PhotoBarcodeRaye      => "Code-barres raye au stylo/feutre",
            ProofType.PhotoEtiquettePrix    => "Photo etiquette prix en rayon",
            ProofType.PhotoEmballage        => "Photo de l emballage",
            ProofType.PhotoArticlesEtTicket => "Photo articles + ticket",
            ProofType.Ticket                => "Photo du ticket de caisse",
            ProofType.PhotoProduitHorsEmballage => "Photo du produit hors emballage",
            ProofType.PhotoEmballageDecoupe => "Photo emballage découpé (code-barres)",
            _                               => "Preuve photo"
        };
    }

    /// <summary>
    /// Donnees extraites d'une offre Shopmium.
    /// </summary>
    public class ProductData
    {
        public string ProductName  { get; set; } = string.Empty;
        public double MaxPrice     { get; set; }
        public double MinPrice     { get; set; }
        public int    MaxArticles  { get; set; } = 1;
        public string StartDate    { get; set; } = string.Empty;
        public string TimeHHMM     { get; set; } = string.Empty;
        public double TvaRate      { get; set; } = 0.055;
        /// <summary>Vrai si le taux TVA n'a pu être déterminé avec certitude → UI demande confirmation</summary>
        public bool   TvaNeedsConfirmation { get; set; } = false;
        /// <summary>Texte "Au rayon XXX" extrait du HTML — affiché dans l'UI pour info</summary>
        public string? RayonText { get; set; }
        public string SourceUrl    { get; set; } = string.Empty;
        public string RawConditions{ get; set; } = string.Empty;
        public string? BarcodeEan  { get; set; }
        public string? Brand        { get; set; }  // Marque extraite du dataLayer
        public TicketType TicketType { get; set; } = TicketType.Standard;
        public string? WarningText  { get; set; }  // Texte du warning popup
        public bool TicketMismatch  { get; set; }  // true = ticket auto ≠ conditions offre

        // ── Calculs derives ──────────────────────────────────────────────────
        public double TotalTTC  => Math.Round(MaxArticles * MaxPrice, 2);
        public double TvaAmount => Math.Round(TotalTTC * TvaRate / (1 + TvaRate), 2);

        // ── Preuves detectees (avec texte exact Shopmium) ────────────────────
        public List<ProofRequirement> ProofRequirements { get; set; } = [];

        // Compatibilité backward
        public List<ProofType> RequiredProofs =>
            ProofRequirements.Select(r => r.Type).ToList();
        public ProofType RequiredProof =>
            ProofRequirements.Count > 0 ? ProofRequirements[0].Type : ProofType.None;

        // ── Image produit reelle (telechargee depuis Shopmium) ────────────────
        public byte[]? ProductImageData { get; set; }
        public string? ProductImageUrl  { get; set; }

        // ── Toutes les images produit (pour affichage + copie dans Gemini) ───
        public List<string> AllImageUrls { get; set; } = [];

        /// <summary>
        /// Références éligibles enrichies. Chaque entrée porte :
        ///   - Name     : nom commercial affiché (ex: "Danao 900ml Chocolat")
        ///   - Price    : prix associé (0 si absent du texte conditions)
        ///   - Barcode  : EAN propre à cette référence (ex: "3228857000764")
        ///   - ImageUrl : URL de la miniature produit Shopmium
        ///   - ProductId: ID Shopmium interne (pour le body PostSubmission)
        /// </summary>
        public List<EligibleRef> AllEligibleRefs { get; set; } = new();

        // Pas de prix → l'utilisateur doit choisir la référence manuellement
        public bool NeedsManualRefChoice =>
            AllEligibleRefs.Count > 1 && AllEligibleRefs.All(r => r.Price == 0);

        // ── Offre exclusive à l'application (scan code-barres requis) ────────
        /// Quand Shopmium demande de scanner le code-barres → EAN obligatoire.
        public bool IsAppExclusive { get; set; }

        /// true = le panneau EAN doit être affiché et la recherche lancée.
        /// Uniquement si l'EAN est STRICTEMENT nécessaire :
        ///   - Offre app-exclusive (Shopmium demande de scanner le code-barres)
        ///   - Photo du code-barres barré au stylo (besoin de localiser le code)
        ///   - Photo de l'emballage découpé au niveau du code-barres
        public bool NeedsEanSearch =>
            IsAppExclusive || ProofRequirements.Any(r =>
                r.Type is ProofType.PhotoBarcodeRaye
                       or ProofType.PhotoEmballageDecoupe);

        // ── Image sélectionnée intelligemment (meilleur match produit) ────────
        public List<(string Url, string Alt, string Source, int Score)>
            AllImagesWithAlt { get; set; } = [];
        public string? BestImageUrl   { get; set; }  // URL de la meilleure image
        public string? BestImageAlt   { get; set; }  // Alt text de cette image
        public bool    BestImageMatch { get; set; }  // true = correspondance exacte
        public string? BestImageNote  { get; set; }  // note de correspondance
        public byte[]? BestImageData  { get; set; }  // bytes téléchargés
    }

    /// <summary>
    /// Référence produit éligible enrichie — nom commercial, prix, EAN,
    /// URL miniature et ID Shopmium interne.
    /// </summary>
    public class EligibleRef
    {
        public string  Name      { get; set; } = "";
        public double  Price     { get; set; }
        public string  Barcode   { get; set; } = "";
        public string? ImageUrl  { get; set; }
        public long    ProductId { get; set; }
    }
}
