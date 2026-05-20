using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ShopmiumPdfAutomator.Models;

namespace ShopmiumPdfAutomator.Services
{
    // ═══════════════════════════════════════════════════════════════════
    //  MODÈLES UPLOAD (calqués sur l'APK officielle Shopmium)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Body de la requête POST /me/upload_urls
    /// Source : com.shopmium.sdk.core.model.upload.UploadInfoRequest
    ///
    /// public class UploadInfoRequest {
    ///     @SerializedName("mime_type") String mMimeType;
    ///     @SerializedName("purpose")   String mPurpose;
    /// }
    /// </summary>
    public class UploadInfoRequest
    {
        [JsonPropertyName("mime_type")]
        public string MimeType { get; set; } = "image/jpeg";

        /// <summary>
        /// Type de preuve. Valeurs connues (depuis proofs_capture.purpose) :
        ///   - receipt              : ticket de caisse
        ///   - product              : photo du produit
        ///   - cut_barcode          : code-barres découpé
        ///   - cut_package          : emballage découpé
        ///   - receipt_mutilation   : ticket déchiré
        ///   - packag / package     : emballage entier
        /// </summary>
        [JsonPropertyName("purpose")]
        public string Purpose { get; set; } = "receipt";
    }

    /// <summary>
    /// Réponse de POST /me/upload_urls
    /// Source : com.shopmium.sdk.core.model.upload.UploadInfo
    ///
    /// public class UploadInfo {
    ///     String getUrl();        // URL S3 pré-signée pour le PUT
    ///     String getReference();  // ID interne Shopmium à mettre dans proofs[]
    /// }
    /// </summary>
    public class UploadInfoResponse
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("reference")]
        public string? Reference { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SERVICE D'UPLOAD - flow 3 étapes calqué sur UploadStackManager
    // ═══════════════════════════════════════════════════════════════════

    public class ShopmiumUploadService
    {
        private readonly HttpClient _http;
        private readonly HttpClient _s3Client; // client séparé pour S3 (sans les headers Shopmium)

        public ShopmiumUploadService(HttpClient sharedAuthenticatedClient)
        {
            _http = sharedAuthenticatedClient;
            _s3Client = new HttpClient();
            _s3Client.Timeout = TimeSpan.FromMinutes(2); // upload S3 peut être lent
        }

        /// <summary>
        /// ÉTAPE 1 : Génère une URL S3 pré-signée auprès de Shopmium.
        /// POST /v1/me/upload_urls avec body { mime_type, purpose }
        /// </summary>
        public async Task<(string? url, string? reference, string? error)> GenerateUploadUrlAsync(
            string mimeType,
            string purpose)
        {
            try
            {
                var body = JsonSerializer.Serialize(new UploadInfoRequest
                {
                    MimeType = mimeType,
                    Purpose  = purpose
                });

                var resp = await _http.PostAsync("me/upload_urls",
                    new StringContent(body, Encoding.UTF8, "application/json"));

                var respBody = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return (null, null, $"HTTP {(int)resp.StatusCode} : {Truncate(respBody, 300)}");

                var info = JsonSerializer.Deserialize<UploadInfoResponse>(respBody);
                if (info == null || string.IsNullOrEmpty(info.Url) || string.IsNullOrEmpty(info.Reference))
                    return (null, null, $"Réponse invalide : {Truncate(respBody, 300)}");

                return (info.Url, info.Reference, null);
            }
            catch (Exception ex)
            {
                return (null, null, $"Exception : {ex.Message}");
            }
        }

        /// <summary>
        /// ÉTAPE 2 : Upload des bytes de l'image sur l'URL S3 pré-signée.
        /// PUT https://shopmium-prod-uploads.s3.amazonaws.com/...?<signature>
        /// Le client séparé évite d'envoyer les headers Shopmium (X-API-Key etc.)
        /// qui pourraient faire échouer la signature AWS.
        /// </summary>
        public async Task<(bool ok, string? error)> UploadToS3Async(
            string s3Url,
            byte[] imageBytes,
            string mimeType)
        {
            try
            {
                using var content = new ByteArrayContent(imageBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

                var resp = await _s3Client.PutAsync(s3Url, content);

                if (!resp.IsSuccessStatusCode)
                {
                    var respBody = await resp.Content.ReadAsStringAsync();
                    return (false, $"S3 HTTP {(int)resp.StatusCode} : {Truncate(respBody, 300)}");
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"S3 Exception : {ex.Message}");
            }
        }

        /// <summary>
        /// Flow complet : GENERATE → UPLOAD → retourne la référence à mettre dans proofs[].
        /// </summary>
        public async Task<(string? reference, string? error)> UploadPictureAsync(
            byte[] imageBytes,
            string mimeType,
            string purpose)
        {
            // Étape 1 : générer l'URL
            var (s3Url, reference, err1) = await GenerateUploadUrlAsync(mimeType, purpose);
            if (err1 != null) return (null, $"Étape 1 (upload_urls) : {err1}");
            if (string.IsNullOrEmpty(s3Url) || string.IsNullOrEmpty(reference))
                return (null, "Étape 1 : URL ou référence vide");

            // Étape 2 : upload S3
            var (ok, err2) = await UploadToS3Async(s3Url, imageBytes, mimeType);
            if (!ok) return (null, $"Étape 2 (S3 upload) : {err2}");

            // L'étape 3 (envoyer la submission avec la référence) est faite par
            // l'appelant car ça dépend du contexte (nouvelle submission vs update).
            return (reference, null);
        }

        // ───────────────────────────────────────────────────────────────
        //  ÉTAPE 3a : Ajouter une preuve à une soumission EXISTANTE
        //  (cas "PHOTO DEMANDÉE" — admin_inquired)
        //
        //  Endpoint officiel (depuis AuthenticatedApi de l'APK) :
        //    @PUT("me/submissions/{id}")
        //    Single<ShmSubmission> updateSubmission(@Body PutSubmission, @Path("id") Integer);
        //
        //  Body officiel (depuis PutSubmission.kt) :
        //    public class PutSubmission {
        //        @SerializedName("proofs") List<PostProof> mResubmissionProofs;
        //    }
        //
        //  Body PostProof minimal pour resoumission (depuis PostProof.kt) :
        //    {
        //      "purpose":    "product" | "receipt" | "cut_barcode" | ...,
        //      "reference":  "<reference S3 reçue à l'étape 1>"
        //      // les champs step_id, group_key, picture_size sont optionnels (null)
        //    }
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Ajoute une preuve photo à une soumission existante (cas "PHOTO DEMANDÉE").
        /// Flow complet : génère URL → upload S3 → PUT submission avec la référence.
        /// </summary>
        /// <param name="submissionId">ID de la soumission existante (depuis /me/submissions)</param>
        /// <param name="imageBytes">Bytes de l'image à uploader</param>
        /// <param name="purpose">Type de preuve : "product", "receipt", "cut_barcode", etc.</param>
        public async Task<(bool ok, string? error, string? reference)> AddProofToSubmissionAsync(
            long submissionId,
            byte[] imageBytes,
            string purpose)
        {
            // Étapes 1 + 2 : générer URL S3 et uploader
            var (reference, err) = await UploadPictureAsync(imageBytes, "image/jpeg", purpose);
            if (err != null) return (false, err, null);
            if (string.IsNullOrEmpty(reference))
                return (false, "Référence vide après upload", null);

            // Étape 3 : PUT /v1/me/submissions/{id} avec la référence
            try
            {
                var bodyObj = new
                {
                    proofs = new[]
                    {
                        new
                        {
                            purpose   = purpose,
                            reference = reference
                        }
                    }
                };

                var body = JsonSerializer.Serialize(bodyObj);

                var request = new HttpRequestMessage(HttpMethod.Put, $"me/submissions/{submissionId}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };

                var resp = await _http.SendAsync(request);
                var respBody = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return (false,
                        $"Étape 3 (PUT submission) : HTTP {(int)resp.StatusCode}\n{Truncate(respBody, 400)}",
                        reference);

                return (true, null, reference);
            }
            catch (Exception ex)
            {
                return (false, $"Étape 3 exception : {ex.Message}", reference);
            }
        }

        // ───────────────────────────────────────────────────────────────
        //  ÉTAPE 3b : Créer une NOUVELLE soumission complète (CAS 2)
        //
        //  Endpoint officiel (depuis AuthenticatedApi.kt) :
        //    @POST("me/submissions")
        //    Single<...> sendSubmission(@Body PostSubmission);
        //
        //  Body officiel (depuis PostSubmission.kt + PostCoupon.kt + PostProof.kt) :
        //  {
        //    "coupons": [
        //      {
        //        "offer": { "id": 12345 },
        //        "products": [{ "id": null, "barcode": "3073780971225" }],
        //        "additional_proofs": [
        //          { "purpose": "product", "reference": "<S3 ref photo produit>" }
        //        ],
        //        "submission_part_key": "1747234567890",
        //        "survey_answer": null
        //      }
        //    ],
        //    "chain": "CARREFOUR",
        //    "proofs": [
        //      { "purpose": "receipt", "reference": "<S3 ref ticket photographié>" }
        //    ],
        //    "location": null,
        //    "quotient_continuity_refs": []
        //  }
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Représente une preuve photo à uploader (avant que le S3 ne soit fait).
        /// Le service se charge d'uploader chaque image et de construire le PostProof.
        /// </summary>
        public record ProofToUpload(
            byte[] ImageBytes,
            string Purpose       // "receipt" | "product" | "cut_barcode" | etc.
        );

        /// <summary>
        /// Représente un produit à déclarer dans la soumission avec sa quantité.
        /// Chaque "Quantity" sera dupliquée comme une entrée individuelle dans
        /// products[] (Shopmium ne gère pas de champ "quantity", chaque article
        /// scanné = une entrée).
        /// </summary>
        public record ProductWithQuantity(
            long? ProductId,     // ID Shopmium du produit (depuis offer.products[].id)
            string Barcode,      // EAN du produit
            int Quantity         // Nombre d'articles (= nombre de doublons dans products[])
        );

        /// <summary>
        /// Crée une nouvelle soumission Shopmium en uploadant toutes les preuves
        /// (ticket + preuves additionnelles) puis en envoyant le PostSubmission complet.
        ///
        /// Flow :
        ///   1. Upload chaque "common proof" (ticket photographié) → reference S3
        ///   2. Upload chaque "additional proof" (photo produit, code-barre découpé...) → reference S3
        ///   3. POST /me/submissions avec le PostSubmission complet
        ///
        /// IMPORTANT — Format products[] :
        ///   D'après SubmissionManager + Coupon.toPostCoupon() de l'APK officielle,
        ///   chaque article scanné = une entrée dans products[]. Si l'utilisateur
        ///   achète 3 fois le même produit, le tableau products[] contient 3 entrées
        ///   identiques. Il n'existe PAS de champ "quantity" dans le modèle Shopmium.
        /// </summary>
        /// <param name="offerId">ID de l'offre Shopmium (ex: 62890)</param>
        /// <param name="products">Liste des produits déclarés avec leur quantité</param>
        /// <param name="chain">Nom de l'enseigne (CARREFOUR, LECLERC, etc.)</param>
        /// <param name="receiptProofs">Preuves "commun" (typiquement le ticket photographié)</param>
        /// <param name="additionalProofs">Preuves "additionnelles" (product, cut_barcode, etc.)</param>
        /// <param name="location">Géoloc optionnelle (peut être null)</param>
        public async Task<(bool ok, string? error, long? submissionId)> CreateSubmissionAsync(
            long offerId,
            System.Collections.Generic.List<ProductWithQuantity> products,
            string chain,
            System.Collections.Generic.List<ProofToUpload> receiptProofs,
            System.Collections.Generic.List<ProofToUpload> additionalProofs,
            (double lat, double lng, int accuracy)? location = null)
        {
            try
            {
                // ─── Étape 1 : upload toutes les preuves "commun" (tickets) ───
                var commonRefs = new System.Collections.Generic.List<(string reference, string purpose)>();
                foreach (var p in receiptProofs)
                {
                    var (reference, err) = await UploadPictureAsync(p.ImageBytes, "image/jpeg", p.Purpose);
                    if (err != null) return (false, $"Upload receipt ({p.Purpose}) : {err}", null);
                    if (string.IsNullOrEmpty(reference)) return (false, "Reference receipt vide", null);
                    commonRefs.Add((reference, p.Purpose));
                }

                // ─── Étape 2 : upload toutes les preuves "additionnelles" ───
                var additionalRefs = new System.Collections.Generic.List<(string reference, string purpose)>();
                foreach (var p in additionalProofs)
                {
                    var (reference, err) = await UploadPictureAsync(p.ImageBytes, "image/jpeg", p.Purpose);
                    if (err != null) return (false, $"Upload additional ({p.Purpose}) : {err}", null);
                    if (string.IsNullOrEmpty(reference)) return (false, "Reference additional vide", null);
                    additionalRefs.Add((reference, p.Purpose));
                }

                // ─── Étape 3 : construire le body PostSubmission ───
                // submission_part_key = timestamp ms (comme generateRandomId() côté APK)
                var submissionPartKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

                // Construire le tableau products[] en dupliquant chaque entrée
                // selon sa quantité (1 scan = 1 entrée).
                var productsList = new System.Collections.Generic.List<object>();
                foreach (var prod in products)
                {
                    for (int i = 0; i < prod.Quantity; i++)
                    {
                        productsList.Add(new
                        {
                            id      = prod.ProductId,
                            barcode = prod.Barcode
                        });
                    }
                }

                object locationObj = location.HasValue
                    ? (object)new
                    {
                        latitude  = location.Value.lat,
                        longitude = location.Value.lng,
                        accuracy  = location.Value.accuracy
                    }
                    : null!;

                var bodyObj = new
                {
                    coupons = new[]
                    {
                        new
                        {
                            offer = new { id = (int)offerId },
                            products = productsList.ToArray(),
                            additional_proofs = additionalRefs.ConvertAll(r => new
                            {
                                purpose   = r.purpose,
                                reference = r.reference
                            }),
                            submission_part_key = submissionPartKey,
                            survey_answer       = (object?)null
                        }
                    },
                    chain = chain,
                    proofs = commonRefs.ConvertAll(r => new
                    {
                        purpose   = r.purpose,
                        reference = r.reference
                    }),
                    location = locationObj,
                    quotient_continuity_refs = System.Array.Empty<string>()
                };

                var body = JsonSerializer.Serialize(bodyObj, new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                // Dump pour debug
                try
                {
                    var dumpPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "shopmium_last_post_submission.json");
                    File.WriteAllText(dumpPath, body);
                }
                catch { }

                // ─── Étape 4 : envoyer le POST /me/submissions ───
                var resp = await _http.PostAsync("me/submissions",
                    new StringContent(body, Encoding.UTF8, "application/json"));

                var respBody = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return (false,
                        $"POST submission : HTTP {(int)resp.StatusCode}\n{Truncate(respBody, 500)}",
                        null);

                // Tenter de récupérer l'ID de la soumission créée depuis la réponse
                long? newId = null;
                try
                {
                    using var doc = JsonDocument.Parse(respBody);
                    if (doc.RootElement.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.Number
                        && idEl.TryGetInt64(out var id))
                        newId = id;
                }
                catch { }

                return (true, null, newId);
            }
            catch (Exception ex)
            {
                return (false, $"CreateSubmission exception : {ex.Message}", null);
            }
        }

        private static string Truncate(string s, int max)
            => s.Length > max ? s[..max] : s;
    }
}
