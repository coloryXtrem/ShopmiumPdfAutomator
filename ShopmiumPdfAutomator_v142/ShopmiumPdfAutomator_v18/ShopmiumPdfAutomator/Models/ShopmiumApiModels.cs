using System.Text.Json.Serialization;

namespace ShopmiumPdfAutomator.Models
{
    // ── Session / Auth ────────────────────────────────────────────────────────
    public class ShopmiumSession
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";
    }

    // ── Offres ────────────────────────────────────────────────────────────────
    public class ShopmiumOffer
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("short_name")]
        public string? ShortName { get; set; }

        // ── Image principale ───────────────────────────────────────────────
        [JsonPropertyName("product_image_url")]
        public string? ProductImageUrl { get; set; }

        // Compat avec l'ancien code
        [JsonPropertyName("picture_url")]
        public string? PictureUrl { get; set; }

        [JsonIgnore]
        public string? EffectiveImageUrl =>
            !string.IsNullOrEmpty(ProductImageUrl) ? ProductImageUrl : PictureUrl;

        // ── Quantité (item_min / item_max) ────────────────────────────────
        [JsonPropertyName("item_min")]
        public int? ItemMin { get; set; }

        [JsonPropertyName("item_max")]
        public int? ItemMax { get; set; }

        [JsonIgnore]
        public int EffectiveQuantity => ItemMin ?? ItemMax ?? 1;

        // ── Verification type (in_app, photos, etc.) ──────────────────────
        [JsonPropertyName("verification_type")]
        public string? VerificationType { get; set; }

        /// <summary>URL publique de la page de l'offre (ex: https://offers.shopmium.com/fr/n/apericube-1)</summary>
        [JsonPropertyName("url_web")]
        public string? UrlWeb { get; set; }

        /// <summary>
        /// True si l'offre a été construite à partir de /me/submissions (historique)
        /// et non du catalogue /me/nav. Sert à griser/désactiver le clic sur
        /// les offres historiques expirées.
        /// </summary>
        [JsonIgnore]
        public bool IsGhost { get; set; } = false;

        /// <summary>
        /// Statut de la dernière soumission de l'utilisateur pour cette offre.
        /// Renseigné par le service au moment du chargement (pas dans le JSON brut).
        /// Valeurs typiques : "admin_paid", "admin_pending", "admin_processing", "admin_abandoned",
        /// "user_abandoned", "admin_refused", ou null si jamais soumis.
        /// </summary>
        [JsonIgnore]
        public string? UserSubmissionStatus { get; set; }

        /// <summary>Catégorie d'affichage pour l'UI.</summary>
        public enum OfferCategory
        {
            /// <summary>Offre disponible classique (scan en magasin).</summary>
            Available,
            /// <summary>Offre exclusive à l'application (code promo, achat en ligne — pas de scan en magasin).</summary>
            AppExclusive,
            /// <summary>Soumission en cours de traitement.</summary>
            Pending,
            /// <summary>Soumission déjà remboursée ou clôturée définitivement.</summary>
            Refunded,
        }

        [JsonIgnore]
        public OfferCategory DisplayCategory
        {
            get
            {
                var s = (UserSubmissionStatus ?? "").ToLowerInvariant();

                // ── Remboursée / payée (DÉFINITIF) ──
                if (s == "admin_paid" || s.Contains("paid") || s.Contains("rembours"))
                    return OfferCategory.Refunded;

                // ── admin_abandoned : Shopmium a clôturé sans retour user (DÉFINITIF)
                if (s == "admin_abandoned" || s == "admin_refused" || s == "admin_rejected")
                    return OfferCategory.Refunded;

                // ── En cours de traitement ──
                if (s.Contains("pending") || s.Contains("processing") || s.Contains("validat")
                 || s == "admin_pending" || s == "submitted")
                    return OfferCategory.Pending;

                // ── App exclusive : l'offre exige autre chose qu'un simple ticket ──
                //
                //   D'après l'analyse de l'API Shopmium, une offre est "exclusive app"
                //   si elle nécessite une preuve photo SPÉCIALE que l'app gère
                //   (et qu'on ne peut pas reproduire avec un simple faux ticket) :
                //
                //   - product_selection.method = "none" → pur web/code promo
                //     (Quitoque, Ultra Premium Direct)
                //
                //   - proofs_capture contient "cut_barcode"     → découper le code-barres
                //   - proofs_capture contient "receipt_mutilation" → ticket mutilé
                //   - proofs_capture contient "cut_package"     → paquet découpé
                //   - proofs_capture contient "product"         → photo du produit (Gemini)
                //
                //   Les offres avec proofs_capture = ["receipt"] uniquement sont
                //   "normales" (un faux ticket suffit, pas besoin de l'app).
                //
                var method = (Submission?.ProductSelection?.Method ?? "").ToLowerInvariant();
                if (method == "none") return OfferCategory.AppExclusive;

                if (Submission?.ProofsCapture != null)
                {
                    foreach (var p in Submission.ProofsCapture)
                    {
                        var purpose = (p.Purpose ?? "").ToLowerInvariant();
                        if (purpose == "cut_barcode"
                         || purpose == "receipt_mutilation"
                         || purpose == "cut_package"
                         || purpose == "product"
                         || purpose == "packag"           // variant
                         || purpose == "package")
                        {
                            return OfferCategory.AppExclusive;
                        }
                    }
                }

                if (IsRemote == true) return OfferCategory.AppExclusive;

                // ── user_abandoned ou pas de soumission → offre disponible ──
                return OfferCategory.Available;
            }
        }

        /// <summary>
        /// True si l'offre nécessite une photo du produit (proofs_capture contient "product").
        /// Utilisé pour afficher conditionnellement le panneau de nettoyage EXIF.
        /// </summary>
        [JsonIgnore]
        public bool RequiresProductPhoto
        {
            get
            {
                if (Submission?.ProofsCapture == null) return false;
                foreach (var p in Submission.ProofsCapture)
                {
                    var purpose = (p.Purpose ?? "").ToLowerInvariant();
                    if (purpose == "product" || purpose == "packag" || purpose == "package"
                     || purpose == "cut_package")
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Liste toutes les preuves photo requises pour cette offre, avec un
        /// libellé lisible à afficher dans l'UI.
        /// Source : offer.submission.proofs_capture[].purpose
        ///
        /// Tuple (purpose, label) :
        ///   - "receipt" → "Ticket de caisse"
        ///   - "product" → "Photo du produit"
        ///   - "cut_barcode" → "Code-barres découpé"
        ///   - "cut_package" → "Emballage découpé"
        ///   - "receipt_mutilation" → "Ticket de caisse mutilé"
        ///   - "packag" / "package" → "Photo de l'emballage"
        /// </summary>
        [JsonIgnore]
        public List<(string Purpose, string Label)> RequiredProofPurposes
        {
            get
            {
                var list = new List<(string, string)>();
                if (Submission?.ProofsCapture == null) return list;

                foreach (var p in Submission.ProofsCapture)
                {
                    var purpose = (p.Purpose ?? "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(purpose)) continue;

                    string label = purpose switch
                    {
                        "receipt"            => "Ticket de caisse",
                        "product"            => "Photo du produit",
                        "cut_barcode"        => "Code-barres découpé",
                        "cut_package"        => "Emballage découpé",
                        "receipt_mutilation" => "Ticket de caisse mutilé",
                        "packag"             => "Photo de l'emballage",
                        "package"            => "Photo de l'emballage",
                        _                    => $"Preuve : {purpose}"
                    };

                    list.Add((purpose, label));
                }
                return list;
            }
        }

        /// <summary>Label utilisateur du statut (pour affichage dans la liste).</summary>
        [JsonIgnore]
        public string StatusLabel
        {
            get
            {
                var s = (UserSubmissionStatus ?? "").ToLowerInvariant();
                return s switch
                {
                    "admin_paid"      => "✓ Remboursée",
                    "eshop_done"      => "🛒 E-shop utilisé",
                    "user_submitted"  => "⧗ En cours de traitement",
                    "admin_accepted"  => "⧗ Acceptée — paiement en attente",
                    "admin_inquired"  => "⚠ Photo demandée par Shopmium",
                    "admin_refused"   => "✗ Refusée",
                    "admin_abandoned" => "⊘ Abandonnée par Shopmium",
                    "user_abandoned"  => "↻ Annulée — vous pouvez recréer un ticket",
                    "unknown"         => "? Statut inconnu",
                    _                 => ""
                };
            }
        }

        /// <summary>
        /// Message d'aide contextuel détaillé selon le statut.
        /// Affiché en bas de l'offre dans le catalogue pour expliquer son état.
        /// </summary>
        [JsonIgnore]
        public string StatusHelp
        {
            get
            {
                var s = (UserSubmissionStatus ?? "").ToLowerInvariant();
                return s switch
                {
                    "user_submitted"  => "Cette offre est en cours de traitement par Shopmium. Vous pouvez recréer un ticket si vous souhaitez resoumettre.",
                    "admin_accepted"  => "Cette offre a été acceptée. Le paiement est en attente de virement.",
                    "admin_inquired"  => "Cette offre nécessite une nouvelle preuve photo. Vous pouvez recréer un ticket complet.",
                    "admin_refused"   => "Cette offre a été refusée. Vous pouvez recréer un ticket pour réessayer.",
                    "admin_abandoned" => "Cette offre a été abandonnée par Shopmium. Vous pouvez essayer à nouveau.",
                    "user_abandoned"  => "Vous avez annulé cette demande. Vous pouvez recréer un ticket à tout moment.",
                    _                 => ""
                };
            }
        }

        [JsonIgnore]
        public bool HasStatusLabel => !string.IsNullOrEmpty(StatusLabel);

        [JsonIgnore]
        public bool HasStatusHelp => !string.IsNullOrEmpty(StatusHelp);

        /// <summary>
        /// Couleur du badge selon le statut (pour binding XAML).
        /// Renvoie une couleur hex string pour ColorConverter.
        /// </summary>
        [JsonIgnore]
        public System.Windows.Media.Brush StatusBrush
        {
            get
            {
                var s = (UserSubmissionStatus ?? "").ToLowerInvariant();
                var color = s switch
                {
                    "admin_paid"      => System.Windows.Media.Color.FromRgb(0x5A, 0xB8, 0x78), // vert
                    "eshop_done"      => System.Windows.Media.Color.FromRgb(0x66, 0xBB, 0xFF), // bleu
                    "user_submitted"  => System.Windows.Media.Color.FromRgb(0xFF, 0xCC, 0x44), // jaune
                    "admin_accepted"  => System.Windows.Media.Color.FromRgb(0x88, 0xCC, 0xFF), // bleu clair
                    "admin_inquired"  => System.Windows.Media.Color.FromRgb(0xFF, 0xA0, 0x40), // orange
                    "admin_refused"   => System.Windows.Media.Color.FromRgb(0xFF, 0x66, 0x66), // rouge
                    "admin_abandoned" => System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA), // gris
                    "user_abandoned"  => System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA), // gris
                    _                 => System.Windows.Media.Color.FromRgb(0xC0, 0xD0, 0xE0)
                };
                var b = new System.Windows.Media.SolidColorBrush(color);
                b.Freeze();
                return b;
            }
        }

        /// <summary>true = offre encore soumissionnable, null/false = déjà utilisée ou fermée</summary>
        [JsonPropertyName("submittable")]
        public bool? Submittable { get; set; }

        /// <summary>Cycle de vie de l'offre (dates).</summary>
        [JsonPropertyName("lifecycle")]
        public ShopmiumLifecycle? Lifecycle { get; set; }

        // ── Présentation (prix, conditions) ────────────────────────────────
        [JsonPropertyName("presentation")]
        public ShopmiumPresentation? Presentation { get; set; }

        // ── Submission (proofs, scan, etc.) ────────────────────────────────
        [JsonPropertyName("submission")]
        public ShopmiumSubmission? Submission { get; set; }

        // ── Marques / customer_brands ──────────────────────────────────────
        [JsonPropertyName("customer_brands")]
        public List<ShopmiumBrand>? CustomerBrands { get; set; }

        // ── Produits ───────────────────────────────────────────────────────
        [JsonPropertyName("products")]
        public List<ShopmiumProduct> Products { get; set; } = new();

        // ── Rebate (valeur du remboursement) ───────────────────────────────
        [JsonPropertyName("rebate_summary_breakdown")]
        public ShopmiumRebateBreakdown? RebateBreakdown { get; set; }

        [JsonPropertyName("rebate_summary")]
        public string? RebateSummary { get; set; }

        /// <summary>Texte de remboursement avec détails du pourcentage (ex: "20% à 30% de réduction sur 2 à 6 articles")</summary>
        [JsonPropertyName("rebate_summary_with_conditions")]
        public string? RebateSummaryWithConditions { get; set; }

        // ── Anciens champs (compat) ────────────────────────────────────────
        [JsonPropertyName("rebate")]          public double? Rebate { get; set; }
        [JsonPropertyName("rebate_value")]    public double? RebateValue { get; set; }
        [JsonPropertyName("max_price")]       public double? MaxPrice { get; set; }
        [JsonPropertyName("max_quantity")]    public int?    MaxQuantity { get; set; }
        [JsonPropertyName("retailers")]       public List<ShopmiumRetailer>? Retailers { get; set; }
        [JsonPropertyName("category")]        public string? Category { get; set; }
        [JsonPropertyName("description")]     public string? Description { get; set; }
        [JsonPropertyName("submission_settings")] public ShopmiumSubmissionSettings? SubmissionSettings { get; set; }
        [JsonPropertyName("end_date")]        public string? EndDate { get; set; }
        [JsonPropertyName("is_remote")]       public bool?   IsRemote { get; set; }

        // Calculé : rebate effectif (depuis breakdown ou ancien champ)
        [JsonIgnore]
        public double EffectiveRebate
        {
            get
            {
                if (Rebate.HasValue)        return Rebate.Value;
                if (RebateValue.HasValue)   return RebateValue.Value;
                // Parser depuis "1,80€" dans le breakdown
                var s = RebateBreakdown?.Highlight ?? RebateSummary ?? "";
                return ParseEuroAmount(s);
            }
        }

        // Calculé : prix unitaire MAX (depuis presentation.detail.price.strikethrough)
        // Si plage "1,68€ à 4,93€" → on prend le maximum (4,93€).
        // Si valeur unique "4,80€" → on retourne 4,80.
        [JsonIgnore]
        public double EffectivePrice
        {
            get
            {
                var s = Presentation?.Detail?.Price?.Strikethrough ?? "";
                if (!string.IsNullOrWhiteSpace(s))
                {
                    // Extraire TOUS les montants en € et prendre le MAX
                    var rx = new System.Text.RegularExpressions.Regex(@"(\d+[.,]?\d*)\s*€");
                    double max = 0;
                    foreach (System.Text.RegularExpressions.Match m in rx.Matches(s))
                    {
                        var v = m.Groups[1].Value.Replace(',', '.');
                        if (double.TryParse(v, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
                        {
                            if (d > max) max = d;
                        }
                    }
                    if (max > 0) return max;
                }
                return MaxPrice ?? 0;
            }
        }

        // Calculé : description longue
        [JsonIgnore]
        public string? EffectiveDescription =>
            Presentation?.Detail?.Outline?.Content ?? Description;

        // Calculé : nom de la marque (1ère customer_brand)
        [JsonIgnore]
        public string? BrandName =>
            CustomerBrands?.FirstOrDefault()?.Name;

        // ── 2️⃣ Montant remboursé estimé ──────────────────────────────────────
        // RÈGLE PRÉCISE (selon utilisateur) :
        //   estimation = quantité_max × prix_max × pourcentage_max
        // 
        //   Exemple Apéricube :
        //     - qty_max = 6 (item_max)
        //     - prix_max = 4,93€ (max du strikethrough "1,68€ à 4,93€")
        //     - %_max = 30% (max de "20% à 30%")
        //   → 6 × 4,93 × 0,30 = 8,874 → ≈ 8,87€ remboursés ✓
        //
        // Si pas de pourcentage trouvé → montant fixe depuis rebate_summary_breakdown.highlight
        // (cas d'une réduction fixe type "1,80€")
        [JsonIgnore]
        public double EstimatedRebate
        {
            get
            {
                // 0) Cas spécial "Pas de remboursement" → 0
                var rs = (RebateSummary ?? "").ToLowerInvariant();
                if (rs.Contains("pas de remboursement")
                 || rs.Contains("aucun remboursement")
                 || rs.Contains("pas remboursé"))
                    return 0;

                // 0bis) Cas spécial "Satisfait ou remboursé" → 100% du prix × quantité max
                if (rs.Contains("satisfait ou remboursé")
                 || rs.Contains("satisfait ou rembourse")
                 || rs.Contains("100% remboursé"))
                {
                    var unit100 = EffectivePrice;
                    var qty100  = ItemMax ?? ItemMin ?? 1;
                    if (unit100 > 0)
                        return Math.Round(unit100 * qty100, 2);
                }

                // 1) Calcul "% max" en parcourant TOUS les textes possibles
                var pctSources = new[]
                {
                    RebateBreakdown?.Highlight,           // ex: "-30%"
                    RebateSummaryWithConditions,         // ex: "20% à 30% de réduction sur 2 à 6 articles"
                    RebateSummary,                       // ex: "20% à 30% de réduction"
                };
                double maxPct = 0;
                foreach (var src in pctSources)
                {
                    if (string.IsNullOrWhiteSpace(src)) continue;
                    var p = ParsePercentage(src);
                    if (p > maxPct) maxPct = p;
                }

                if (maxPct > 0)
                {
                    var unit = EffectivePrice;                 // déjà MAX du range
                    var qty  = ItemMax ?? ItemMin ?? 1;        // toujours MAX
                    if (unit > 0)
                        return Math.Round(unit * qty * (maxPct / 100.0), 2);
                }

                // 2) Pas de %, mais un montant fixe → "1,80€"
                var fixedAmount = ParseEuroAmount(RebateBreakdown?.Highlight ?? "");
                if (fixedAmount > 0)
                {
                    var qty = ItemMax ?? ItemMin ?? 1;
                    return Math.Round(fixedAmount * qty, 2);
                }

                // 3) Fallback : rebate_summary "3€ remboursés"
                var v = ParseEuroAmount(RebateSummary ?? "");
                if (v > 0) return v;

                // 4) Champs API anciens
                if (Rebate.HasValue && Rebate.Value > 0) return Rebate.Value;
                if (RebateValue.HasValue && RebateValue.Value > 0) return RebateValue.Value;

                return 0;
            }
        }

        [JsonIgnore]
        public bool HasEstimatedRebate => EstimatedRebate > 0;

        [JsonIgnore]
        public string EstimatedRebateLabel =>
            HasEstimatedRebate
                ? $"≈ {EstimatedRebate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}€ remboursés"
                : "";

        // ── 3️⃣ Détection enseigne incompatible ───────────────────────────────
        //
        // RÈGLE CORRECTE (selon utilisateur) :
        //
        //   Le message "Ticket XXX requis" ne doit apparaître QUE si AUCUN des
        //   tickets supportés (Carrefour standard / Carrefour Drive / Leclerc)
        //   ne peut être utilisé avec l'offre.
        //
        //   Si Carrefour OU Leclerc fait partie des enseignes acceptées
        //   (sans être explicitement exclu), pas de warning.
        //
        //   Exemple OK (PAS de warning) :
        //     "chez Carrefour, Leclerc, Auchan, Intermarché UNIQUEMENT"
        //       → Carrefour accepté → ticket Carrefour standard valide
        //
        //   Exemple WARNING :
        //     "chez Lidl UNIQUEMENT"  → aucun ticket supporté → warning Lidl
        //     "en pharmacie"          → aucun ticket supporté → warning pharmacie
        //
        [JsonIgnore]
        public string IncompatibilityWarning => ComputeIncompatibilityWarning();

        [JsonIgnore]
        public bool HasIncompatibilityWarning => !string.IsNullOrEmpty(IncompatibilityWarning);

        private string ComputeIncompatibilityWarning()
        {
            // On combine retailers + texte des conditions/outline
            var retailerText = string.Join(" ",
                (Retailers ?? new List<ShopmiumRetailer>())
                    .Select(r => (r.Name ?? r.Key ?? r.Chain ?? "")));
            var conditions = Presentation?.Detail?.Conditions?.Content ?? "";
            var outline    = Presentation?.Detail?.Outline?.Content ?? "";
            var all = (retailerText + " " + conditions + " " + outline + " " + (Description ?? ""))
                .ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(all)) return "";

            // ─── Étape 1 : Carrefour / Leclerc compatibles ? ──────────────────
            //   Si OUI → aucun warning, l'app peut générer un ticket compatible
            //   Si NON → on cherche quelle enseigne est imposée

            bool carrefourCompatible = IsRetailerCompatible(all, "carrefour");
            bool leclercCompatible   = IsRetailerCompatible(all, "leclerc");

            if (carrefourCompatible || leclercCompatible) return "";

            // ─── Étape 1ter : "Toute enseigne" / "magasins" sans exclusion stricte
            // de Carrefour/Leclerc → ces enseignes sont IMPLICITEMENT compatibles.
            //
            // Exemple Tranches Végé : "dans toute enseigne vendante (Drive inclus)"
            // → ni "hors Carrefour" ni "hors Leclerc" → ticket Carrefour standard valide.
            bool isAllRetailers = System.Text.RegularExpressions.Regex.IsMatch(all,
                @"(?:dans\s+)?toute[s]?\s+(?:les\s+)?(?:enseignes?|magasins?|grandes?\s+(?:et\s+moyennes?\s+)?surfaces?)\s+(?:vendantes?|distributrices?)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (isAllRetailers
             && !System.Text.RegularExpressions.Regex.IsMatch(all,
                @"(?:hors|sauf)\s+carrefour(?!\s+(?:proxi|city|express|contact|montagne))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
             && !System.Text.RegularExpressions.Regex.IsMatch(all,
                @"(?:hors|sauf)\s+leclerc",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return ""; // Carrefour/Leclerc implicitement compatibles
            }

            // ─── Étape 1bis : Offre "Drive et livraison" sans enseigne précise ─
            // Si l'offre dit "valable en Drive et livraison" ou "Drive uniquement"
            // SANS mentionner explicitement une enseigne exclue → Carrefour Drive
            // (que l'app supporte) est compatible.
            //
            // Exemple Apéricube : "en Drive et livraison UNIQUEMENT" → on génère
            // un ticket Carrefour Drive sans problème.
            bool isDriveOnly = System.Text.RegularExpressions.Regex.IsMatch(all,
                @"(?:en\s+)?drive\s+(?:et\s+livraison\s+)?(?:à\s+domicile\s+)?(?:à\s+domicile\s+)?uniquement",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool isDriveAndDelivery = all.Contains("drive et livraison")
                || all.Contains("en drive et livraison");

            if ((isDriveOnly || isDriveAndDelivery)
                && !System.Text.RegularExpressions.Regex.IsMatch(all,
                    @"(?:hors|sauf)\s+(?:le\s+)?drive",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return ""; // Carrefour Drive est compatible
            }

            // ─── Étape 2 : aucun ticket supporté → quelle est l'enseigne requise ? ──

            // Cas particuliers
            if (IsRetailerCompatible(all, "pharmacie") || IsRetailerCompatible(all, "parapharmacie"))
                return "⚠ Ticket pharmacie requis";
            if (all.Contains("amazon") || all.Contains("e-commerce") || all.Contains("en ligne uniquement"))
                return "⚠ Achat en ligne requis";
            if (IsRetailerCompatible(all, "biocoop")
             || IsRetailerCompatible(all, "la vie claire")
             || IsRetailerCompatible(all, "naturalia"))
                return "⚠ Ticket magasin bio requis";

            // Enseignes spécifiques : on n'affiche le warning QUE si l'enseigne
            // est vraiment IMPOSÉE (pas exclue, pas "hors X")
            var checks = new (string token, string label)[]
            {
                ("lidl",         "Lidl"),
                ("aldi",         "Aldi"),
                ("picard",       "Picard"),
                ("auchan",       "Auchan"),
                ("intermarche",  "Intermarché"),
                ("intermarché",  "Intermarché"),
                ("système u",    "Système U"),
                ("systeme u",    "Système U"),
                ("magasins u",   "Système U"),
                ("hyper u",      "Système U"),
                ("super u",      "Système U"),
                ("coopérative u","Système U"),
                ("casino",       "Casino"),
                ("monoprix",     "Monoprix"),
                ("franprix",     "Franprix"),
                ("cora",         "Cora"),
                ("match",        "Match"),
            };

            // Pour éviter les doublons sur Intermarché / U / etc.
            var seen = new HashSet<string>();
            foreach (var (token, label) in checks)
            {
                if (seen.Contains(label)) continue;
                if (IsRetailerCompatible(all, token))
                {
                    seen.Add(label);
                    return $"⚠ Ticket {label} requis";
                }
            }

            // Détection générique : "valable uniquement chez X" sans Carrefour/Leclerc
            var explicitOnly = new[] {
                "valable uniquement", "uniquement chez", "uniquement en magasin",
                "exclusivement chez", "exclusivement en"
            };
            foreach (var marker in explicitOnly)
            {
                var idx = all.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var zone = all.Substring(idx, Math.Min(150, all.Length - idx));
                    if (!zone.Contains("carrefour") && !zone.Contains("leclerc"))
                        return "⚠ Offre incompatible avec les tickets disponibles";
                }
            }

            return "";
        }

        /// <summary>
        /// Vérifie qu'une enseigne est MENTIONNÉE et NON EXCLUE.
        ///
        /// Règles d'exclusion détectées :
        ///   - "hors X"               (hors Auchan, hors Carrefour Proxi)
        ///   - "sauf X"               (sauf Casino)
        ///   - "à l'exclusion de X"
        ///   - "non valable chez X"
        ///   - "non valable en X"     (en pharmacie)
        ///   - "non disponible chez X"
        ///   - "non disponible en X"  (en pharmacie)
        ///   - "n'est pas valable chez X"
        ///   - "n'est pas disponible chez X"
        ///   - "ne sont pas valables chez X"
        ///   - "offre non disponible en X"
        ///   - "interdit chez X"
        ///   - "(hors X, Y, Z)"       contenu d'une parenthèse
        ///
        /// Cas spécial Carrefour :
        ///   - "hors Carrefour Proxi" ne compte pas comme exclusion totale
        ///     (Carrefour standard reste accepté)
        /// </summary>
        private static bool IsRetailerCompatible(string lowerText, string retailerToken)
        {
            // L'enseigne est-elle au moins mentionnée ?
            if (!lowerText.Contains(retailerToken)) return false;

            // Cas spécial Carrefour : "hors Carrefour Proxi/City/etc." ne compte pas
            if (retailerToken == "carrefour")
            {
                // Cherche "hors carrefour" / "sauf carrefour" PAS suivi de proxi/city/express/contact
                var rxFull = new System.Text.RegularExpressions.Regex(
                    @"(?:hors|sauf|excl(?:u|us|usion))\s+carrefour(?!\s+(?:proxi|city|express|contact|montagne))",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (rxFull.IsMatch(lowerText)) return false;
                return true;
            }

            // Cas spécial "auchan" : "auchan supermarché" / "auchan hypermarché" peut être
            // exclu sans que Auchan tout court le soit. Mais on simplifie : si on a
            // "hors Auchan" → exclu. Si on a "hors Auchan Supermarché" sans autre exclusion
            // de Auchan → l'enseigne Auchan reste mentionnée comme acceptée potentiellement.
            // Pour le moment on garde la règle simple.

            // Patterns d'exclusion qui invalident la mention
            var exclusionPatterns = new[]
            {
                // "hors X" / "sauf X" / "excluant X" / "à l'exclusion de X"
                $@"\b(?:hors|sauf|excl(?:u|us|uant|usion(?:\s+de)?))\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "non valable chez/en X"
                $@"\bnon\s+valable(?:\s+(?:chez|en|dans|au))?\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "non disponible chez/en X"
                $@"\bnon\s+disponible(?:\s+(?:chez|en|dans|au))?\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "n'est pas valable chez/en X"
                $@"\bn['’]est\s+pas\s+valable(?:\s+(?:chez|en|dans|au))?\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "n'est pas disponible chez/en X"
                $@"\bn['’]est\s+pas\s+disponible(?:\s+(?:chez|en|dans|au))?\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "ne sont pas valables chez/en X"
                $@"\bne\s+sont\s+pas\s+valables?(?:\s+(?:chez|en|dans|au))?\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "offre non disponible en X"
                $@"\boffre\s+non\s+disponible(?:\s+(?:chez|en|dans|au))?\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "offre non valable en X"
                $@"\boffre\s+non\s+valable(?:\s+(?:chez|en|dans|au))?\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "interdit chez X"
                $@"\binterdit(?:e|s|es)?\s+(?:chez|en|dans|au)\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "pas en X" / "pas chez X"  (ex: "pas en pharmacie")
                $@"\bpas\s+(?:en|chez|au)\s+{System.Text.RegularExpressions.Regex.Escape(retailerToken)}\b",
                // "X" dans une parenthèse contenant "hors" ou "sauf"
                // Pattern : \( ... hors|sauf ... X ... \)
                @"\([^)]*?(?:hors|sauf|excl)[^)]*?\b"
                    + System.Text.RegularExpressions.Regex.Escape(retailerToken) + @"\b[^)]*?\)",

                // LISTE après marqueur d'exclusion :
                //   "hors X, Y et Z" / "sauf X, Y, Z" / "n'est pas valable chez X, Y et Z"
                //   On reconnaît "X" SEULEMENT s'il est précédé du marqueur d'exclusion
                //   suivi UNIQUEMENT de séparateurs de liste + d'autres noms d'enseignes.
                //   On limite à 80 chars max et on n'autorise QUE des caractères de noms
                //   d'enseigne séparés par "," / "et" / "&" / "+" / "ou" / "/".
                //   Ainsi "Soirée Match" dans une liste de produits n'est PAS capturé.
                @"(?:hors|sauf|excl(?:u|us|uant|usion(?:\s+de)?)|"
                    + @"non\s+(?:valable|disponible|autoris[ée]|accept[ée])"
                    + @"(?:\s+(?:chez|en|dans|au))?|"
                    + @"n['’]est\s+pas\s+(?:valable|disponible)(?:\s+(?:chez|en|dans|au))?|"
                    + @"ne\s+sont\s+pas\s+valables?(?:\s+(?:chez|en|dans|au))?|"
                    + @"offre\s+non\s+(?:valable|disponible)(?:\s+(?:chez|en|dans|au))?)"
                    + @"\s+(?:la\s+|le\s+|les\s+)?"
                    // Liste optionnelle de noms d'enseignes courts (1-25 chars chacun)
                    // séparés UNIQUEMENT par , / et / & / + / ou / / / espaces
                    + @"(?:[a-zà-ÿ][a-zà-ÿ0-9'\-]{1,25}"           // 1er nom d'enseigne
                    + @"(?:\s*(?:,|et|&|\+|ou|/)\s*[a-zà-ÿ][a-zà-ÿ0-9'\-]{1,25}){0,8}" // jusqu'à 8 noms supplémentaires
                    + @"\s*(?:,|et|&|\+|ou|/)\s*)?"
                    + @"\b" + System.Text.RegularExpressions.Regex.Escape(retailerToken) + @"\b",
            };

            foreach (var p in exclusionPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(lowerText, p,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return false;
            }

            // L'enseigne est mentionnée et n'est pas dans une zone d'exclusion → compatible
            return true;
        }

        // Helper : extraire un % depuis "20%", "20 %", "jusqu'à 30%"
        private static double ParsePercentage(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var rx = new System.Text.RegularExpressions.Regex(@"(\d+(?:[.,]\d+)?)\s*%");
            var matches = rx.Matches(s);
            double max = 0;
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var v = m.Groups[1].Value.Replace(',', '.');
                if (double.TryParse(v, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    if (d > max) max = d;
                }
            }
            return max;
        }

        // Helper pour extraire un montant depuis "1,80€" ou "-1,80€" ou "4.80 €"
        private static double ParseEuroAmount(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            // CRITIQUE : si la chaîne contient %, ce n'est PAS un montant € → 0
            if (s.Contains('%')) return 0;
            var cleaned = s.Replace("€", "").Replace("-", "").Replace(" ", "")
                           .Replace(",", ".").Trim();
            return double.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        public override string ToString() =>
            $"{Name}{(EffectiveRebate > 0 ? $" — {EffectiveRebate:F2}€" : "")}";
    }

    public class ShopmiumRebateBreakdown
    {
        [JsonPropertyName("highlight")]
        public string? Highlight { get; set; }

        [JsonPropertyName("after")]
        public string? After { get; set; }
    }

    public class ShopmiumLifecycle
    {
        [JsonPropertyName("opened_at")]      public DateTime? OpenedAt      { get; set; }
        [JsonPropertyName("displayed_at")]   public DateTime? DisplayedAt   { get; set; }
        [JsonPropertyName("near_closed_at")] public DateTime? NearClosedAt  { get; set; }
        [JsonPropertyName("closed_at")]      public DateTime? ClosedAt      { get; set; }
        [JsonPropertyName("hidden_at")]      public DateTime? HiddenAt      { get; set; }
        [JsonPropertyName("last_submit_at")] public DateTime? LastSubmitAt  { get; set; }
    }

    public class ShopmiumPresentation
    {
        [JsonPropertyName("detail")]
        public ShopmiumDetail? Detail { get; set; }
    }

    public class ShopmiumDetail
    {
        [JsonPropertyName("price")]
        public ShopmiumPrice? Price { get; set; }

        [JsonPropertyName("outline")]
        public ShopmiumOutline? Outline { get; set; }

        [JsonPropertyName("conditions")]
        public ShopmiumOutline? Conditions { get; set; }   // {heading, content}

        [JsonPropertyName("date_conditions")]
        public ShopmiumDateConditions? DateConditions { get; set; }
    }

    public class ShopmiumDateConditions
    {
        [JsonPropertyName("heading")]
        public string? Heading { get; set; }

        [JsonPropertyName("labels")]
        public ShopmiumDateLabels? Labels { get; set; }
    }

    public class ShopmiumDateLabels
    {
        [JsonPropertyName("displayed")]
        public string? Displayed { get; set; }

        [JsonPropertyName("still_submittable")]
        public string? StillSubmittable { get; set; }
    }

    public class ShopmiumPrice
    {
        [JsonPropertyName("before")]
        public string? Before { get; set; }

        [JsonPropertyName("highlight")]
        public string? Highlight { get; set; }

        [JsonPropertyName("strikethrough")]
        public string? Strikethrough { get; set; }

        [JsonPropertyName("after")]
        public string? After { get; set; }
    }

    public class ShopmiumOutline
    {
        [JsonPropertyName("heading")]
        public string? Heading { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    public class ShopmiumSubmission
    {
        [JsonPropertyName("product_selection")]
        public ShopmiumProductSelection2? ProductSelection { get; set; }

        [JsonPropertyName("proofs_capture")]
        public List<ShopmiumProofCapture>? ProofsCapture { get; set; }

        [JsonPropertyName("eligible_to_multi_submit")]
        public bool? EligibleToMultiSubmit { get; set; }
    }

    public class ShopmiumProductSelection2
    {
        [JsonPropertyName("method")]
        public string? Method { get; set; }     // "scan" / "tap" / etc.

        [JsonPropertyName("items_min")]
        public int? ItemsMin { get; set; }

        [JsonPropertyName("items_max")]
        public int? ItemsMax { get; set; }

        /// <summary>Nombre d'articles encore exploitables (0 = épuisé)</summary>
        [JsonPropertyName("remaining_items")]
        public int? RemainingItems { get; set; }
    }

    public class ShopmiumProofCapture
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("group_key")]
        public string? GroupKey { get; set; }

        [JsonPropertyName("purpose")]
        public string? Purpose { get; set; }    // "receipt" / "barcode" / etc.

        [JsonPropertyName("overlay")]
        public string? Overlay { get; set; }

        [JsonPropertyName("camera")]
        public string? Camera { get; set; }

        [JsonPropertyName("skippable")]
        public bool? Skippable { get; set; }

        [JsonPropertyName("allow_multiple_pictures")]
        public bool? AllowMultiplePictures { get; set; }
    }

    public class ShopmiumBrand
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("offer_detail_logo_image_url")]
        public string? OfferDetailLogoImageUrl { get; set; }

        [JsonPropertyName("offer_list_logo_image_url")]
        public string? OfferListLogoImageUrl { get; set; }
    }

    public class ShopmiumProduct
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        // L'API renvoie "barcode" (pas "ean") — on mappe les deux pour compat
        [JsonPropertyName("barcode")]
        public string? Barcode { get; set; }

        [JsonPropertyName("ean")]
        public string? EanLegacy { get; set; }

        // Propriété calculée — ne pas tenter de la sérialiser/désérialiser
        [JsonIgnore]
        public string? Ean => !string.IsNullOrEmpty(Barcode) ? Barcode : EanLegacy;

        [JsonPropertyName("price")]
        public double? Price { get; set; }

        [JsonPropertyName("brand")]
        public string? Brand { get; set; }

        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; set; }
    }

    public class ShopmiumRetailer
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("chain")]
        public string? Chain { get; set; }
    }

    // ── Submission Settings ───────────────────────────────────────────────────
    public class ShopmiumSubmissionSettings
    {
        [JsonPropertyName("submission_part_key")]
        public string? SubmissionPartKey { get; set; }

        /// <summary>Étapes supplémentaires (preuves photo, etc.)</summary>
        [JsonPropertyName("additional_steps")]
        public List<ShopmiumAdditionalStep>? AdditionalSteps { get; set; }

        [JsonPropertyName("product_selection")]
        public ShopmiumProductSelection? ProductSelection { get; set; }

        [JsonPropertyName("is_multi_submit")]
        public bool? IsMultiSubmit { get; set; }
    }

    /// <summary>
    /// Étape supplémentaire de soumission — c'est ici que Shopmium définit
    /// les preuves photo requises (photo barcode barré, photo emballage, etc.)
    /// </summary>
    public class ShopmiumAdditionalStep
    {
        [JsonPropertyName("step_id")]
        public int? StepId { get; set; }

        [JsonPropertyName("group_key")]
        public string? GroupKey { get; set; }

        /// <summary>Type de l'étape : "receipt", "picture", "barcode", etc.</summary>
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        /// <summary>Sous-type : "crossed_barcode", "packaging", "price_tag", etc.</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>Description lisible de ce qui est demandé</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("is_mandatory")]
        public bool? IsMandatory { get; set; }
    }

    public class ShopmiumProductSelection
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("max_quantity")]
        public int? MaxQuantity { get; set; }
    }

    // ── Upload ────────────────────────────────────────────────────────────────
    public class UploadInfoRequest
    {
        [JsonPropertyName("mime_type")]
        public string MimeType { get; set; } = "image/jpeg";

        [JsonPropertyName("purpose")]
        public string Purpose { get; set; } = "receipt";
    }

    public class UploadInfoResponse
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }

    // ── Submission ────────────────────────────────────────────────────────────
    public class PostSubmissionRequest
    {
        [JsonPropertyName("coupons")]
        public List<PostCoupon> Coupons { get; set; } = new();

        [JsonPropertyName("chain")]
        public string? Chain { get; set; }

        [JsonPropertyName("proofs")]
        public List<PostProof> Proofs { get; set; } = new();
    }

    public class PostCoupon
    {
        [JsonPropertyName("offer")]
        public PostOfferRef Offer { get; set; } = new();

        [JsonPropertyName("products")]
        public List<PostProductRef>? Products { get; set; }

        [JsonPropertyName("additional_proofs")]
        public List<PostProof> AdditionalProofs { get; set; } = new();

        [JsonPropertyName("submission_part_key")]
        public string SubmissionPartKey { get; set; } = "";
    }

    public class PostOfferRef
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }

    public class PostProductRef
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }

    public class PostProof
    {
        [JsonPropertyName("purpose")]
        public string Purpose { get; set; } = "receipt";

        [JsonPropertyName("reference")]
        public string Reference { get; set; } = "";

        [JsonPropertyName("step_id")]
        public int? StepId { get; set; }

        [JsonPropertyName("group_key")]
        public string? GroupKey { get; set; }
    }

    public class SubmissionResult
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }
}
