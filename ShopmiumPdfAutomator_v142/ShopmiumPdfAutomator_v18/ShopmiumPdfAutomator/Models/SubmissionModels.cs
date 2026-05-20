using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace ShopmiumPdfAutomator.Models
{
    /// <summary>
    /// Représente une soumission Shopmium (= une demande de remboursement).
    /// Modèle calqué sur SubmissionAPI.kt de l'app officielle (vu via JADX).
    /// Une soumission contient 1 ou plusieurs coupons (généralement 1).
    /// </summary>
    public class SubmissionApiModel
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }

        [JsonPropertyName("submitted_at")]
        public string? SubmittedAt { get; set; }

        [JsonPropertyName("coupons")]
        public List<CouponApiModel> Coupons { get; set; } = new();

        [JsonPropertyName("resubmittable")]
        public bool Resubmittable { get; set; }

        [JsonPropertyName("total_amount_formatted")]
        public string? TotalAmountFormatted { get; set; }

        [JsonPropertyName("given_chain_alias")]
        public string? GivenChainAlias { get; set; }

        [JsonPropertyName("profile_completion")]
        public bool ProfileCompletion { get; set; }

        [JsonPropertyName("account_presence")]
        public bool AccountPresence { get; set; }

        [JsonPropertyName("first_submission")]
        public bool? FirstSubmission { get; set; }

        // ─── Propriétés calculées pour le binding UI ───────────────────────

        /// <summary>Le coupon principal (généralement le seul) de la soumission.</summary>
        [JsonIgnore]
        public CouponApiModel? MainCoupon
            => Coupons.Count > 0 ? Coupons[0] : null;

        /// <summary>Date de soumission formatée pour l'affichage français.</summary>
        [JsonIgnore]
        public string SubmittedDateFormatted
        {
            get
            {
                var s = SubmittedAt ?? CreatedAt;
                if (string.IsNullOrEmpty(s)) return "";
                if (DateTime.TryParse(s, out var dt))
                    return dt.ToString("dd/MM/yyyy à HH:mm");
                return s;
            }
        }
    }

    /// <summary>
    /// Représente un coupon (= le résultat d'une soumission).
    /// Modèle calqué sur CouponAPI.kt de l'app officielle.
    /// </summary>
    public class CouponApiModel
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        /// <summary>
        /// Statut du coupon. Valeurs possibles (depuis l'enum officiel d90/g90) :
        ///   admin_accepted, admin_abandoned, admin_inquired, admin_paid,
        ///   admin_refused, eshop_done, user_abandoned, user_submitted, unknown
        /// </summary>
        [JsonPropertyName("coupon_status")]
        public string? CouponStatus { get; set; }

        /// <summary>Titre lisible du statut (ex: "Remboursement effectué").</summary>
        [JsonPropertyName("status_title")]
        public string? StatusTitle { get; set; }

        /// <summary>Raison détaillée du statut (ex: "Virement PayPal de 3,14€ effectué le 20/04/2026").</summary>
        [JsonPropertyName("status_reason")]
        public string? StatusReason { get; set; }

        [JsonPropertyName("items_count")]
        public int ItemsCount { get; set; }

        [JsonPropertyName("amount")]
        public double? Amount { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("submission_id")]
        public long SubmissionId { get; set; }

        [JsonPropertyName("abandonable")]
        public bool? Abandonable { get; set; }

        [JsonPropertyName("offer")]
        public CouponOfferEmbed? Offer { get; set; }

        // ─── Propriétés calculées pour le binding UI ───────────────────────

        /// <summary>Label coloré du statut.</summary>
        [JsonIgnore]
        public string StatusBadge
        {
            get
            {
                var s = (CouponStatus ?? "").ToLowerInvariant();
                return s switch
                {
                    "admin_paid"      => "✓ REMBOURSÉE",
                    "admin_accepted"  => "⧗ ACCEPTÉE",
                    "user_submitted"  => "⧗ EN COURS",
                    "admin_inquired"  => "⚠ PHOTO DEMANDÉE",
                    "admin_refused"   => "✗ REFUSÉE",
                    "admin_abandoned" => "⊘ ABANDONNÉE",
                    "user_abandoned"  => "✗ ANNULÉE PAR VOUS",
                    "eshop_done"      => "🛒 E-SHOP UTILISÉ",
                    "unknown"         => "? INCONNU",
                    _                 => StatusTitle ?? CouponStatus ?? ""
                };
            }
        }

        /// <summary>Couleur du badge selon le statut.</summary>
        [JsonIgnore]
        public Brush StatusColor
        {
            get
            {
                var s = (CouponStatus ?? "").ToLowerInvariant();
                Color c = s switch
                {
                    "admin_paid"      => Color.FromRgb(0x5A, 0xB8, 0x78), // vert
                    "admin_accepted"  => Color.FromRgb(0x88, 0xCC, 0xFF), // bleu clair
                    "user_submitted"  => Color.FromRgb(0xFF, 0xCC, 0x44), // jaune
                    "admin_inquired"  => Color.FromRgb(0xFF, 0xA0, 0x40), // orange
                    "admin_refused"   => Color.FromRgb(0xFF, 0x66, 0x66), // rouge
                    "admin_abandoned" => Color.FromRgb(0xAA, 0xAA, 0xAA), // gris
                    "user_abandoned"  => Color.FromRgb(0xAA, 0xAA, 0xAA), // gris
                    "eshop_done"      => Color.FromRgb(0x66, 0xBB, 0xFF), // bleu
                    _                 => Color.FromRgb(0xC0, 0xD0, 0xE0),
                };
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }

        /// <summary>Vrai si l'utilisateur peut resoumettre cette offre (user_abandoned).</summary>
        [JsonIgnore]
        public bool CanResubmit
            => string.Equals(CouponStatus, "user_abandoned", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Vrai si le statut est FINAL (l'offre ne peut plus être recréée).
        /// Statuts finaux : admin_paid (remboursée), eshop_done (e-shop utilisé).
        /// </summary>
        [JsonIgnore]
        public bool IsFinalStatus
        {
            get
            {
                var s = (CouponStatus ?? "").ToLowerInvariant();
                return s == "admin_paid" || s == "eshop_done";
            }
        }

        /// <summary>
        /// Vrai si l'utilisateur peut ENVOYER UNE PREUVE PHOTO supplémentaire
        /// directement depuis l'app via PUT /me/submissions/{id}.
        /// Cas concret : Shopmium a demandé une nouvelle photo (admin_inquired).
        /// </summary>
        [JsonIgnore]
        public bool CanAddProof
            => string.Equals(CouponStatus, "admin_inquired", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Vrai si l'offre peut être retentée (statut non-final ET non-actif).
        /// = annulée, refusée, abandonnée par Shopmium.
        /// </summary>
        [JsonIgnore]
        public bool IsRetryable
        {
            get
            {
                var s = (CouponStatus ?? "").ToLowerInvariant();
                return s == "user_abandoned"
                    || s == "admin_abandoned"
                    || s == "admin_refused";
            }
        }

        /// <summary>
        /// Vrai si l'offre est encore EN COURS de traitement chez Shopmium
        /// (l'utilisateur ne peut pas resoumettre tant qu'elle est en cours).
        /// </summary>
        [JsonIgnore]
        public bool IsActive
        {
            get
            {
                var s = (CouponStatus ?? "").ToLowerInvariant();
                return s == "user_submitted"
                    || s == "admin_accepted"
                    || s == "admin_inquired";
            }
        }

        /// <summary>Montant formaté (ex: "3,14 €") ou chaîne vide si pas d'amount.</summary>
        [JsonIgnore]
        public string AmountFormatted
        {
            get
            {
                if (!Amount.HasValue || Amount.Value <= 0) return "";
                return Amount.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                       .Replace(".", ",") + " €";
            }
        }

        [JsonIgnore]
        public bool HasAmount => Amount.HasValue && Amount.Value > 0;

        /// <summary>Nom de l'offre (depuis l'embed).</summary>
        [JsonIgnore]
        public string OfferTitle => Offer?.Title ?? "(offre inconnue)";

        /// <summary>URL de l'image de l'offre.</summary>
        [JsonIgnore]
        public string? OfferImageUrl => Offer?.ImageUrl;

        /// <summary>Description courte de l'offre (rebate summary).</summary>
        [JsonIgnore]
        public string OfferSummary => Offer?.RebateSummary ?? "";

        /// <summary>Date de soumission (extrait de created_at).</summary>
        [JsonIgnore]
        public string CreatedDateFormatted
        {
            get
            {
                if (string.IsNullOrEmpty(CreatedAt)) return "";
                if (DateTime.TryParse(CreatedAt, out var dt))
                    return dt.ToString("dd/MM/yyyy");
                return CreatedAt;
            }
        }
    }

    /// <summary>
    /// Données embarquées d'une offre dans un coupon
    /// (sous-ensemble allégé de ShopmiumOffer).
    /// </summary>
    public class CouponOfferEmbed
    {
        [JsonPropertyName("id")]         public long    Id          { get; set; }
        [JsonPropertyName("node_id")]    public long?   NodeId      { get; set; }
        [JsonPropertyName("title")]      public string? Title       { get; set; }
        [JsonPropertyName("description")]public string? Description { get; set; }
        [JsonPropertyName("image_url")]  public string? ImageUrl    { get; set; }

        [JsonPropertyName("rebate_summary")]
        public string? RebateSummary { get; set; }

        [JsonPropertyName("rebate_summary_with_conditions")]
        public string? RebateSummaryWithConditions { get; set; }
    }

    /// <summary>Réponse complète de /me/submissions.</summary>
    public class SubmissionsResponse
    {
        [JsonPropertyName("submissions_count")]
        public int SubmissionsCount { get; set; }

        [JsonPropertyName("submissions")]
        public List<SubmissionApiModel> Submissions { get; set; } = new();

        [JsonPropertyName("dashboard")]
        public SubmissionsDashboard? Dashboard { get; set; }
    }

    /// <summary>Dashboard global avec totaux.</summary>
    public class SubmissionsDashboard
    {
        [JsonPropertyName("total_rebated_amount")]
        public double TotalRebatedAmount { get; set; }

        [JsonPropertyName("total_rebated_amount_formatted")]
        public string? TotalRebatedAmountFormatted { get; set; }

        [JsonPropertyName("coupons_count")]
        public int CouponsCount { get; set; }
    }
}
