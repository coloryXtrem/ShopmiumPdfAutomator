using ShopmiumPdfAutomator.Models;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IO;
using Microsoft.Win32;

namespace ShopmiumPdfAutomator.Services
{
    /// <summary>
    /// Service d'accès à l'API Shopmium (reverse engineered depuis l'APK Android).
    /// BASE_URL : https://api.shopmium.com/v1/
    /// Auth     : Basic base64(email:password) pour login,
    ///            Token token={accessToken} pour toutes les requêtes suivantes.
    /// </summary>
    public class ShopmiumApiService
    {
        // ── Constantes API ────────────────────────────────────────────────────
        private const string BASE_URL   = "https://api.shopmium.com/v1/";
        private const string API_KEY_FR = "frb2e7f954d3ba4a071a46a8f42b32c7797458d0dc4f938cb7031f4cd153d84809d8275d9bb5dee46babc19a9b71928a9c665c5bafd43d290a29a759d696d6c013";
        // User-Agent au format exact attendu par Shopmium :
        // Shopmium/{version} (Android; {brand} {model}; API Level {sdk}; {density})
        private const string USER_AGENT = "Shopmium/11.7.0 (Android; Google Pixel 7; API Level 34; xxhdpi)";
        private const string LANGUAGE   = "fr_FR";  // Valeur exacte de MarketDomain.getLanguageKey() pour FR
        // App-Env-Identifier : identifiant d'environnement attendu par Shopmium
        // Valeur extraite de AppConfig$AppEnvIdentifier.VALUE = Constants.ENVIRONMENT_RELEASE
        private const string APP_ENV    = "release";
        private const string REG_KEY    = @"SOFTWARE\ShopmiumHelper";

        private readonly HttpClient _http;
        private string? _accessToken;
        private string? _userEmail;
        private ShopmiumUploadService? _uploadService;

        public bool   IsConnected => !string.IsNullOrEmpty(_accessToken);
        public string UserEmail   => _userEmail ?? "";

        /// <summary>
        /// Service d'upload pour envoyer des preuves photo à des soumissions existantes
        /// (cas "PHOTO DEMANDÉE") ou créer de nouvelles soumissions complètes.
        /// Réutilise le même HttpClient (donc le même Bearer access_token).
        /// </summary>
        public ShopmiumUploadService Upload
            => _uploadService ??= new ShopmiumUploadService(_http);

        private static readonly JsonSerializerOptions _jsonOpts =
            new() { PropertyNameCaseInsensitive = true };

        public ShopmiumApiService()
        {
            _http = new HttpClient { BaseAddress = new Uri(BASE_URL) };
            // Headers exactement comme l'app Android (ShopmiumHeaderInterceptor)
            // Headers exactement comme l'app Android :
            //   - x-install-key : envoyé par ConnectApi.logIn (header per-method)
            //   - X-API-Key     : envoyé par ShopmiumHeaderInterceptor (global)
            //   - App-Env-Identifier, Language, User-Agent : interceptor global
            _http.DefaultRequestHeaders.Add("User-Agent",           USER_AGENT);
            _http.DefaultRequestHeaders.Add("x-install-key",        API_KEY_FR);
            _http.DefaultRequestHeaders.Add("X-API-Key",            API_KEY_FR);
            _http.DefaultRequestHeaders.Add("Language",             LANGUAGE);
            _http.DefaultRequestHeaders.Add("App-Env-Identifier",   APP_ENV);
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            _http.Timeout = TimeSpan.FromSeconds(30);

            // Charger le token sauvegardé si disponible
            TryLoadSavedSession();
        }

        // ── AUTH ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Connexion à l'API Shopmium en 2 étapes (reverse engineered) :
        ///   1. POST /installs avec body Install → reçoit un install_key spécifique
        ///   2. POST /sessions avec ce nouvel install_key + Basic auth → reçoit access_token
        ///
        /// Le market api key (frb2e7f9...) ne sert QUE comme X-API-Key global.
        /// Le X-Install-Key DOIT être généré via /installs avant de pouvoir login.
        /// </summary>
        public async Task<(bool success, string message)> LoginAsync(string email, string password)
        {
            try
            {
                // ─── ÉTAPE 1 : Enregistrer un install pour obtenir un install_key ───
                var installKey = await RegisterInstallAsync();
                if (string.IsNullOrEmpty(installKey.key))
                    return (false, $"Étape 1 échouée (POST /installs) :\n{installKey.error}");

                // Mettre à jour le header x-install-key avec la VRAIE clé reçue
                // .NET interdit \n et \r dans les headers — on nettoie pour éviter une exception
                var cleanKey = new string(installKey.key
                    .Where(c => !char.IsControl(c)).ToArray()).Trim();

                _http.DefaultRequestHeaders.Remove("x-install-key");
                _http.DefaultRequestHeaders.Add("x-install-key", cleanKey);

                // ─── ÉTAPE 2 : Login avec ce nouvel install_key ───
                var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{password}"));
                var sessionsBody = JsonSerializer.Serialize(new
                {
                    market   = "fr",
                    language = "fr"
                });

                var resp = await TryLoginInternal(
                    creds,
                    new StringContent(sessionsBody, Encoding.UTF8, "application/json"),
                    HttpMethod.Post,
                    "https://api.shopmium.com/v1/sessions");
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    var code = (int)resp.StatusCode;
                    var snippet = string.IsNullOrEmpty(body)
                        ? "(corps vide)"
                        : body[..Math.Min(300, body.Length)];

                    var hint = code switch
                    {
                        401 => "Identifiants refusés (mauvais email/password).",
                        403 => "Accès interdit (WAF).",
                        404 => "Endpoint introuvable (install_key invalide ?).",
                        429 => "Trop de tentatives.",
                        503 => "Erreur serveur Shopmium.",
                        _   => "",
                    };

                    return (false, $"Étape 2 échouée ({code}) :\n{hint}\n\nBody :\n{snippet}");
                }

                // ─── Parser le token (AccessResponseAPI : { "access_token": "..." }) ───
                string? token = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("access_token", out var el)
                        && el.ValueKind == JsonValueKind.String)
                        token = el.GetString();
                }
                catch { }

                if (string.IsNullOrEmpty(token))
                    return (false, $"Token introuvable dans la réponse :\n{body[..Math.Min(200, body.Length)]}");

                _accessToken = token;
                _userEmail   = email;
                SetAuthHeader();
                SaveSession(email, token);
                return (true, "Connecté");
            }
            catch (Exception ex) { return (false, $"Erreur réseau : {ex.Message}"); }
        }

        /// <summary>
        /// Étape 1 du login : enregistre un nouvel install et reçoit un install_key.
        /// Body = Install(device, os, app) au format JSON.
        /// Réponse = InstallKeyAPI { "install_key": "..." }
        /// </summary>
        private async Task<(string key, string error)> RegisterInstallAsync()
        {
            try
            {
                // Construit le body Install conformément au modèle Android extrait :
                //   - Device  : locale, country, timezone, timezone_offset, model
                //   - os      : name, version
                //   - app     : ti_id (UUID unique), platform, brand, version, api
                var installBody = JsonSerializer.Serialize(new
                {
                    device = new
                    {
                        locale          = "fr_FR",
                        country         = "FR",
                        timezone        = "Europe/Paris",
                        timezone_offset = "+02:00",
                        model           = "Pixel 7"
                    },
                    os = new
                    {
                        name    = "Android",
                        version = "14"
                    },
                    app = new
                    {
                        ti_id    = GetOrCreateInstallId(),  // UUID stable du device
                        platform = "android",
                        brand    = "shopmium",
                        version  = "11.7.0",
                        api      = 34
                    }
                });

                var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.shopmium.com/v1/installs")
                {
                    Content = new StringContent(installBody, Encoding.UTF8, "application/json")
                };
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var resp = await _http.SendAsync(request);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 300 ? body[..300] : body;
                    return ("", $"HTTP {(int)resp.StatusCode}\n{snippet}");
                }

                // Parser InstallKeyAPI : { "install_key": "..." }
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("install_key", out var el)
                    && el.ValueKind == JsonValueKind.String)
                {
                    var key = el.GetString();
                    return (key ?? "", "");
                }

                return ("", $"install_key introuvable dans la réponse :\n{body[..Math.Min(200, body.Length)]}");
            }
            catch (Exception ex)
            {
                return ("", $"Exception : {ex.Message}");
            }
        }

        /// <summary>
        /// Génère ou récupère un UUID stable pour identifier ce device dans le registre.
        /// Persisté pour ne pas s'enregistrer comme un nouvel install à chaque démarrage.
        /// </summary>
        private static string GetOrCreateInstallId()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(REG_KEY);
                var existing = key?.GetValue("install_id") as string;
                if (!string.IsNullOrEmpty(existing)) return existing;

                var newId = Guid.NewGuid().ToString();
                key?.SetValue("install_id", newId);
                return newId;
            }
            catch
            {
                return Guid.NewGuid().ToString();
            }
        }

        private async Task<HttpResponseMessage> TryLoginInternal(
            string basicCreds, HttpContent? body, HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCreds);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body != null) request.Content = body;
            return await _http.SendAsync(request);
        }

        private static string? ExtractToken(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return null;

            // Tentative 1 : désérialisation typée
            try
            {
                var session = JsonSerializer.Deserialize<ShopmiumSession>(responseBody, _jsonOpts);
                if (!string.IsNullOrEmpty(session?.AccessToken)) return session.AccessToken;
            }
            catch { }

            // Tentative 2 : recherche dans tout le JSON
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                // À la racine
                foreach (var prop in new[] { "access_token", "token", "auth_token", "session_token", "api_token" })
                    if (root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
                        return el.GetString();

                // Dans un sous-objet
                foreach (var obj in new[] { "session", "user", "data", "result" })
                    if (root.TryGetProperty(obj, out var sub))
                        foreach (var prop in new[] { "access_token", "token", "auth_token" })
                            if (sub.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
                                return el.GetString();
            }
            catch { }

            return null;
        }

        public void Logout()
        {
            _accessToken = null;
            _userEmail   = null;
            _http.DefaultRequestHeaders.Authorization = null;
            ClearSavedSession();
        }

        // ── OFFRES ────────────────────────────────────────────────────────────

        // CDN CloudFront pour les endpoints en lecture (extrait de AppConfig$Api.BASE_URL_CDN)
        private const string CDN_URL = "https://d17bo1xy1dudt8.cloudfront.net/v1/";

        /// <summary>
        /// Récupère toutes les offres disponibles pour l'utilisateur connecté.
        /// IMPORTANT : les endpoints en lecture (GET /offers, /me/offers) doivent
        /// passer par le CDN CloudFront, pas par api.shopmium.com.
        /// L'API renvoie 421 cdn_misuse sinon.
        /// </summary>
        public async Task<(List<ShopmiumOffer> offers, string error)> GetOffersAsync(
            IProgress<string>? progress = null)
        {
            if (!IsConnected) return (new(), "Non connecté");

            var allOffers = new List<ShopmiumOffer>();
            var errors    = new List<string>();
            _detailDumped     = false;
            _lastDetailError  = "";

            // ─── ÉTAPE 0 — Récupérer l'historique des soumissions en parallèle ─
            // On récupère pour chaque offre son statut de soumission (s'il existe).
            // Plus tard on attache ce statut à chaque offre pour catégorisation UI.
            progress?.Report("Récupération de votre historique…");
            var submissionsTask = GetSubmissionsBreakdownAsync();

            // ÉTAPE 1 — GET /me/nav (API) → la nav contient les IDs des offres
            // ÉTAPE 2 — Pour chaque ID, GET /me/offers/{id} (CDN) → détail complet
            try
            {
                progress?.Report("Chargement du catalogue…");
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.shopmium.com/v1/me/nav");
                req.Headers.Add("X-latitude",  "48.856614");
                req.Headers.Add("X-longitude", "2.352222");
                req.Headers.Add("X-accuracy",  "1");

                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 200 ? body[..200] : body;
                    errors.Add($"{(int)resp.StatusCode} sur /me/nav: {snippet}");
                }
                else
                {
                    // Extraire les IDs uniques depuis la nav
                    var offerIds = ExtractOfferIdsFromNav(body);
                    progress?.Report($"{offerIds.Count} offres trouvées, chargement des détails…");

                    // Récupération en lot via GET /offers?ids=X,Y,Z (CDN)
                    // On batche par 50 pour éviter une URL trop longue
                    int loaded = 0;
                    foreach (var batch in offerIds.Chunk(50))
                    {
                        var idsParam = string.Join(",", batch);
                        var url = CDN_URL + $"offers?ids={idsParam}";

                        try
                        {
                            var detailResp = await _http.GetAsync(url);
                            var detailBody = await detailResp.Content.ReadAsStringAsync();

                            if (!detailResp.IsSuccessStatusCode)
                            {
                                _lastDetailError = $"HTTP {(int)detailResp.StatusCode} sur /offers?ids=… : " +
                                                   (detailBody.Length > 200 ? detailBody[..200] : detailBody);
                                continue;
                            }

                            // Dumper la 1ère réponse pour debug
                            if (!_detailDumped)
                            {
                                _detailDumped = true;
                                try
                                {
                                    var path = Path.Combine(
                                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                        "shopmium_offers_batch.json");
                                    File.WriteAllText(path, detailBody);
                                }
                                catch { }
                            }

                            // Parser la liste d'offres
                            var offersBatch = await ParseOffersResponse(detailBody);
                            foreach (var o in offersBatch)
                            {
                                // ── Filtres d'éligibilité ──
                                // 1. Submittable explicitement false ou null → écarter
                                //    (l'API renvoie null pour les offres déjà utilisées/fermées)
                                if (o.Submittable != true) continue;

                                // 2. Articles restants à 0 → offre épuisée
                                var remain = o.Submission?.ProductSelection?.RemainingItems;
                                if (remain.HasValue && remain.Value <= 0) continue;

                                // 3. Offre déjà fermée (closed_at dépassé)
                                if (o.Lifecycle?.ClosedAt.HasValue == true
                                 && o.Lifecycle.ClosedAt.Value < DateTime.UtcNow) continue;

                                // 4. Dédoublonner
                                if (allOffers.Any(x => x.Id == o.Id)) continue;

                                allOffers.Add(o);
                            }
                        }
                        catch (Exception ex)
                        {
                            _lastDetailError = $"Exception lot offres : {ex.Message}";
                        }

                        loaded += batch.Length;
                        progress?.Report($"Chargement… {loaded}/{offerIds.Count}");
                    }
                }
            }
            catch (Exception ex) { errors.Add($"Exception /me/nav: {ex.Message}"); }

            if (allOffers.Count == 0)
            {
                if (!string.IsNullOrEmpty(_lastDetailError))
                    errors.Add("Dernière erreur détail : " + _lastDetailError);
                var errMsg = errors.Count > 0
                    ? "Aucune offre trouvée :\n\n" + string.Join("\n\n", errors)
                    : "Aucune offre trouvée.";
                return (new(), errMsg);
            }

            // ─── Attacher les statuts de soumission aux offres ─────────────────
            // On utilise le breakdown uniquement pour FILTRER les offres déjà
            // remboursées/en cours du catalogue (l'utilisateur a explicitement
            // demandé à ne plus voir le mélange offres / historique).
            try
            {
                progress?.Report("Récupération de l'historique…");
                var breakdown = await submissionsTask;

                // Filtrer les offres qui sont dans une soumission ACTIVE
                // (admin_paid / pending / processing / admin_abandoned / admin_refused)
                // → on les masque du catalogue pour éviter les doubles soumissions.
                // Les user_abandoned restent visibles (l'utilisateur peut resoumettre).
                if (breakdown.Active.Count > 0)
                {
                    var before = allOffers.Count;
                    allOffers = allOffers.Where(o => !breakdown.Active.Contains(o.Id)).ToList();
                    var hidden = before - allOffers.Count;
                    if (hidden > 0)
                        progress?.Report($"{hidden} offre(s) déjà soumise(s) masquée(s)");
                }
            }
            catch
            {
                // si submissions échoue, on garde toutes les offres
            }

            progress?.Report($"{allOffers.Count} offre(s) chargée(s)");
            return (allOffers.OrderBy(o => o.Name).ToList(), "");
        }

        /// <summary>Récupère les détails complets d'une offre via /offers?ids=ID.</summary>
        public async Task<ShopmiumOffer?> GetOfferDetailAsync(long offerId)
        {
            if (!IsConnected) return null;
            try
            {
                // Note : /me/offers/{id} n'existe plus (404). On utilise /offers?ids=
                var json = await _http.GetStringAsync(CDN_URL + $"offers?ids={offerId}");
                var list = JsonSerializer.Deserialize<List<ShopmiumOffer>>(json, _jsonOpts);
                return list?.FirstOrDefault(o => o.Id == offerId)
                    ?? list?.FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// Récupère les IDs des offres que l'utilisateur a déjà soumises
        /// (en cours, validées, en attente — tout ce qui n'est pas REJETÉ ou ANNULÉ).
        /// Utilisé pour masquer ces offres de la liste affichée à l'utilisateur.
        /// </summary>
        /// <summary>
        /// Récupère les IDs des offres que l'utilisateur a déjà soumises et
        /// dont le coupon est encore ACTIF (pas annulé/abandonné).
        ///
        /// Format API (vérifié) :
        ///   { "submissions": [ { "coupons": [ { "coupon_status": "admin_paid"/"user_abandoned"/...,
        ///                                       "offer": { "id": 62127 } } ] } ] }
        ///
        /// Statuts "actifs" (offre à masquer car déjà utilisée) :
        ///   - admin_paid           : remboursement effectué
        ///   - admin_received       : en cours de traitement
        ///   - admin_processing     : en cours de validation
        ///   - admin_validating     : en attente
        ///   - waiting              : en attente
        ///   - pending              : en attente
        ///
        /// Statuts "inactifs" (offre RE-disponible) :
        ///   - user_abandoned       : l'utilisateur a annulé
        ///   - admin_abandoned      : l'admin a abandonné (si resubmittable=true)
        ///   - admin_refused        : refus définitif
        ///   - admin_rejected       : rejet définitif
        /// </summary>
        /// <summary>
        /// Récupère la liste complète des soumissions de l'utilisateur depuis l'API
        /// officielle Shopmium (/me/submissions).
        ///
        /// SIGNATURE OFFICIELLE (depuis it3.java de l'APK) :
        ///     @GET("me/submissions")
        ///     Single&lt;PurchasesHistoryAPI&gt; e(
        ///         @Query("limit")  int limit,
        ///         @Query("offset") int offset,
        ///         @Query("sort")   String sort);
        ///
        /// La réponse est PurchasesHistoryAPI qui contient :
        ///   - submissions_count : nombre total (sert à savoir quand s'arrêter)
        ///   - submissions : List&lt;SubmissionAPI&gt; (page courante)
        ///   - wallet, dashboard, etc.
        ///
        /// On boucle avec offset += limit jusqu'à atteindre submissions_count.
        /// </summary>
        public async Task<SubmissionsResponse?> GetSubmissionsAsync()
        {
            if (!IsConnected) return null;

            const int PAGE_SIZE = 25; // limit raisonnable (l'app mobile utilise ~10)
            const int MAX_PAGES = 40; // sécurité : max 1000 soumissions

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            try
            {
                SubmissionsResponse? combined = null;
                var seenIds = new HashSet<long>();
                int offset = 0;
                string lastJsonForDump = "";

                for (int loop = 0; loop < MAX_PAGES; loop++)
                {
                    // L'API officielle exige limit, offset ET sort
                    var url = $"me/submissions?limit={PAGE_SIZE}&offset={offset}&sort=-submitted_at";

                    var resp = await _http.GetAsync(url);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // Si la requête paginée échoue (paramètres rejetés), fallback :
                        // tenter l'URL nue à la 1ère itération seulement
                        if (loop == 0)
                        {
                            resp = await _http.GetAsync("me/submissions");
                            if (!resp.IsSuccessStatusCode)
                            {
                                _lastDetailError = $"GET /me/submissions → HTTP {(int)resp.StatusCode}";
                                return null;
                            }
                        }
                        else break;
                    }

                    var json = await resp.Content.ReadAsStringAsync();
                    if (loop == 0) lastJsonForDump = json;

                    var page = JsonSerializer.Deserialize<SubmissionsResponse>(json, opts);
                    if (page == null) break;

                    // Initialiser le combiné à partir de la 1ère page (pour récupérer
                    // submissions_count et dashboard une seule fois)
                    if (combined == null)
                    {
                        combined = new SubmissionsResponse
                        {
                            SubmissionsCount = page.SubmissionsCount,
                            Dashboard        = page.Dashboard,
                            Submissions      = new List<SubmissionApiModel>()
                        };
                    }

                    // Ajouter les nouvelles soumissions de cette page
                    if (page.Submissions == null || page.Submissions.Count == 0) break;

                    int newOnThisPage = 0;
                    foreach (var sub in page.Submissions)
                    {
                        if (seenIds.Add(sub.Id))
                        {
                            combined.Submissions.Add(sub);
                            newOnThisPage++;
                        }
                    }

                    // Arrêts :
                    //   - rien de nouveau (la pagination tourne en boucle)
                    //   - on a atteint submissions_count (objectif atteint)
                    //   - la page reçue est plus petite que PAGE_SIZE (dernière page)
                    if (newOnThisPage == 0) break;
                    if (combined.SubmissionsCount > 0 && combined.Submissions.Count >= combined.SubmissionsCount) break;
                    if (page.Submissions.Count < PAGE_SIZE) break;

                    offset += page.Submissions.Count;
                }

                // Dump pour debug
                try
                {
                    var dumpPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "shopmium_my_submissions.json");
                    File.WriteAllText(dumpPath, lastJsonForDump);
                }
                catch { }

                return combined;
            }
            catch (Exception ex)
            {
                _lastDetailError = $"GetSubmissionsAsync : {ex.Message}";
                return null;
            }
        }

        public async Task<HashSet<long>> GetActiveSubmittedOfferIdsAsync()
        {
            var b = await GetSubmissionsBreakdownAsync();
            return b.Active;
        }

        /// <summary>
        /// Retourne en 2 sets : les offres "actives" (en cours/remboursées),
        /// et toutes les offres soumises (utile pour catégoriser).
        /// </summary>
        /// <summary>
        /// Récupère le breakdown des soumissions de l'utilisateur ET construit des
        /// "offres fantômes" pour les soumissions dont l'offre n'est plus dans le
        /// catalogue (offres expirées) — permet d'afficher l'historique dans les
        /// onglets "En cours" et "Remboursées" même quand les offres ont disparu.
        /// </summary>
        public async Task<SubmissionsBreakdown> GetSubmissionsBreakdownAsync()
        {
            var result = new SubmissionsBreakdown();

            if (!IsConnected) return result;

            try
            {
                var resp = await _http.GetAsync("me/submissions");
                var json = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _lastDetailError = $"GET /me/submissions → HTTP {(int)resp.StatusCode}";
                    return result;
                }

                // Dumper pour debug
                try
                {
                    var path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "shopmium_my_submissions.json");
                    File.WriteAllText(path, json);
                }
                catch { }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("submissions", out var submissions)
                 || submissions.ValueKind != JsonValueKind.Array)
                {
                    _lastDetailError = "Réponse /me/submissions : pas de tableau 'submissions'";
                    return result;
                }

                // Statuts qui RENDENT l'offre re-disponible (l'utilisateur peut
                // recréer un ticket pour cette offre — ne pas la masquer du catalogue) :
                //   - user_abandoned   : l'utilisateur a annulé lui-même
                //   - admin_abandoned  : Shopmium a abandonné (retryable)
                //   - admin_refused    : refus (l'offre est encore valide en théorie)
                //   - user_submitted   : en cours, peut être retentée
                //   - admin_accepted   : acceptée, paiement en attente, peut être retentée
                //   - admin_inquired   : photo demandée, peut être retentée
                //
                // SEULS admin_paid et eshop_done sont VRAIMENT finaux et bloquants
                // (l'utilisateur ne peut plus jamais recréer un ticket).
                var inactiveStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "user_abandoned",
                    "admin_abandoned",
                    "admin_refused",
                    "user_submitted",
                    "admin_accepted",
                    "admin_inquired",
                };

                static int StatusPriority(string s)
                {
                    s = (s ?? "").ToLowerInvariant();
                    if (s == "admin_paid" || s.Contains("paid") || s.Contains("rembours")) return 8;
                    if (s.Contains("pending") || s.Contains("processing") || s.Contains("validat") || s == "submitted") return 6;
                    if (s == "admin_abandoned") return 4;
                    if (s == "admin_refused" || s == "admin_rejected") return 2;
                    if (s == "user_abandoned") return 0;
                    return 1;
                }

                // Pour chaque offre historique : on garde la version la plus prioritaire
                // (ex: admin_paid > user_abandoned si l'offre a été soumise plusieurs fois)
                var bestCoupons = new Dictionary<long, (string status, JsonElement offer)>();

                foreach (var sub in submissions.EnumerateArray())
                {
                    if (!sub.TryGetProperty("coupons", out var coupons)
                     || coupons.ValueKind != JsonValueKind.Array) continue;

                    foreach (var coupon in coupons.EnumerateArray())
                    {
                        long offerId = 0;
                        if (coupon.TryGetProperty("offer", out var off)
                         && off.ValueKind == JsonValueKind.Object
                         && off.TryGetProperty("id", out var oidEl)
                         && oidEl.ValueKind == JsonValueKind.Number
                         && oidEl.TryGetInt64(out var oid))
                            offerId = oid;
                        if (offerId == 0) continue;

                        string status = "";
                        if (coupon.TryGetProperty("coupon_status", out var cs)
                         && cs.ValueKind == JsonValueKind.String)
                            status = cs.GetString() ?? "";
                        else if (coupon.TryGetProperty("status", out var s)
                              && s.ValueKind == JsonValueKind.String)
                            status = s.GetString() ?? "";

                        // Garder le coupon le plus prioritaire pour chaque offre
                        if (bestCoupons.TryGetValue(offerId, out var existing))
                        {
                            if (StatusPriority(status) > StatusPriority(existing.status))
                                bestCoupons[offerId] = (status, off);
                        }
                        else
                        {
                            bestCoupons[offerId] = (status, off);
                        }

                        // Construire allWithStatus avec priorité
                        if (result.AllWithStatus.TryGetValue(offerId, out var prev))
                        {
                            if (StatusPriority(status) > StatusPriority(prev))
                                result.AllWithStatus[offerId] = status;
                        }
                        else
                        {
                            result.AllWithStatus[offerId] = status;
                        }

                        if (inactiveStatuses.Contains(status)) continue;
                        result.Active.Add(offerId);
                    }
                }

                // ─── Construire les "offres fantômes" historiques ─────────────
                // À partir du payload de chaque coupon, on a déjà title, description,
                // image_url, rebate_summary → on peut reconstruire une ShopmiumOffer
                // minimale pour l'afficher dans les onglets "En cours" / "Remboursées".
                foreach (var kv in bestCoupons)
                {
                    var offerId = kv.Key;
                    var status  = kv.Value.status;
                    var offerEl = kv.Value.offer;

                    // On ne crée des fantômes QUE pour les statuts intéressants
                    //   - admin_paid / admin_abandoned / admin_refused → Remboursées
                    //   - pending / processing / validating / submitted → En cours
                    //   - user_abandoned → ignoré (offre considérée comme disponible)
                    var lower = status.ToLowerInvariant();
                    if (lower == "user_abandoned") continue;

                    var ghost = new ShopmiumOffer
                    {
                        Id   = offerId,
                        Name = TryGetString(offerEl, "title") ?? "(offre historique)",
                        Description = TryGetString(offerEl, "description"),
                        ProductImageUrl = TryGetString(offerEl, "image_url"),
                        RebateSummary = TryGetString(offerEl, "rebate_summary"),
                        RebateSummaryWithConditions = TryGetString(offerEl, "rebate_summary_with_conditions"),
                        UserSubmissionStatus = status,
                        IsGhost = true,
                    };

                    result.HistoricalOffers[offerId] = ghost;
                }
            }
            catch (Exception ex)
            {
                _lastDetailError = $"GetSubmissionsBreakdownAsync : {ex.Message}";
            }

            return result;
        }

        private static string? TryGetString(JsonElement el, string prop)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(prop, out var v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            return null;
        }

        /// <summary>Cherche l'offer_id récursivement dans une submission.</summary>
        private static long TryExtractOfferId(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return 0;

            if (el.TryGetProperty("offer_id", out var oid)
             && oid.ValueKind == JsonValueKind.Number
             && oid.TryGetInt64(out var v1)) return v1;

            if (el.TryGetProperty("offer", out var off))
            {
                if (off.ValueKind == JsonValueKind.Number && off.TryGetInt64(out var v2)) return v2;
                if (off.ValueKind == JsonValueKind.Object
                 && off.TryGetProperty("id", out var oidNested)
                 && oidNested.ValueKind == JsonValueKind.Number
                 && oidNested.TryGetInt64(out var v3)) return v3;
            }

            foreach (var key in new[] { "node_id", "offerId" })
            {
                if (el.TryGetProperty(key, out var n)
                 && n.ValueKind == JsonValueKind.Number
                 && n.TryGetInt64(out var v)) return v;
            }

            return 0;
        }

        /// <summary>Extrait le statut depuis différents emplacements possibles.</summary>
        private static string ExtractStatus(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return "";

            foreach (var key in new[] { "coupon_status", "status", "state", "result", "lifecycle_status", "current_status" })
            {
                if (el.TryGetProperty(key, out var s))
                {
                    if (s.ValueKind == JsonValueKind.String) return (s.GetString() ?? "").ToLowerInvariant();
                    if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("key", out var k))
                        return (k.GetString() ?? "").ToLowerInvariant();
                }
            }
            return "";
        }

        // ── UPLOAD ────────────────────────────────────────────────────────────

        /// <summary>
        /// Upload une image vers S3 via le système Shopmium.
        /// Retourne l'URL S3 propre (sans query params) à utiliser dans la soumission.
        /// </summary>
        public async Task<(string? s3Url, string error)> UploadImageAsync(
            string imagePath, IProgress<string>? progress = null)
        {
            if (!IsConnected) return (null, "Non connecté");
            try
            {
                // Étape 1 : obtenir URL pré-signée
                progress?.Report("Obtention de l'URL d'upload…");
                var req     = JsonSerializer.Serialize(new UploadInfoRequest());
                var postRes = await _http.PostAsync("me/upload_urls",
                    new StringContent(req, Encoding.UTF8, "application/json"));
                if (!postRes.IsSuccessStatusCode)
                    return (null, $"Erreur URL upload : {(int)postRes.StatusCode}");

                var info = JsonSerializer.Deserialize<UploadInfoResponse>(
                    await postRes.Content.ReadAsStringAsync(), _jsonOpts);
                if (string.IsNullOrEmpty(info?.Url))
                    return (null, "URL S3 vide");

                // Étape 2 : compresser + upload
                progress?.Report("Compression et upload de l'image…");
                var bytes   = CompressToJpeg90(await File.ReadAllBytesAsync(imagePath));
                using var s3 = new HttpClient();
                var content  = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                var putRes = await s3.PutAsync(info.Url, content);
                if (!putRes.IsSuccessStatusCode)
                    return (null, $"Erreur upload S3 : {(int)putRes.StatusCode}");

                var uri = new Uri(info.Url);
                progress?.Report("Image uploadée ✓");
                return ($"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}", "");
            }
            catch (Exception ex) { return (null, ex.Message); }
        }

        // ── SOUMISSION ────────────────────────────────────────────────────────

        /// <summary>Soumet un ticket à Shopmium avec tous les paramètres de l'offre.</summary>
        public async Task<(bool success, string message, long submissionId)> SubmitTicketAsync(
            ShopmiumOffer offer,
            string s3ImageUrl,
            string? chain,
            IProgress<string>? progress = null)
        {
            if (!IsConnected) return (false, "Non connecté", 0);
            try
            {
                progress?.Report("Préparation de la soumission…");

                var settings  = offer.SubmissionSettings;
                var partKey   = settings?.SubmissionPartKey ?? "";
                var steps     = settings?.AdditionalSteps ?? new();

                // Construire les preuves depuis les steps API
                var mainProof = new PostProof { Purpose = "receipt", Reference = s3ImageUrl };
                var additionalProofs = new List<PostProof> { mainProof };

                // Ajouter les preuves supplémentaires si l'API en définit
                foreach (var step in steps.Where(s => s.StepId.HasValue))
                {
                    additionalProofs.Add(new PostProof
                    {
                        Purpose  = step.Kind ?? "picture",
                        Reference = s3ImageUrl,
                        StepId   = step.StepId,
                        GroupKey = step.GroupKey,
                    });
                }

                var coupon = new PostCoupon
                {
                    Offer             = new PostOfferRef { Id = offer.Id },
                    AdditionalProofs  = additionalProofs,
                    SubmissionPartKey = partKey,
                    Products = offer.Products.Any()
                        ? offer.Products.Select(p => new PostProductRef { Id = p.Id }).ToList()
                        : null,
                };

                var submission = new PostSubmissionRequest
                {
                    Coupons = new() { coupon },
                    Chain   = string.IsNullOrWhiteSpace(chain) ? null : chain.ToUpper(),
                    Proofs  = new() { mainProof },
                };

                var json = JsonSerializer.Serialize(submission, new JsonSerializerOptions
                    { DefaultIgnoreCondition =
                        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

                progress?.Report("Envoi de la soumission à Shopmium…");
                var response = await _http.PostAsync("me/submissions",
                    new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, $"Erreur {(int)response.StatusCode} : {body}", 0);

                var result = JsonSerializer.Deserialize<SubmissionResult>(body, _jsonOpts);
                progress?.Report("Ticket soumis avec succès ✓");
                return (true, $"Ticket soumis ! Référence : #{result?.Id}", result?.Id ?? 0);
            }
            catch (Exception ex) { return (false, ex.Message, 0); }
        }

        // ── PERSISTENCE SESSION ───────────────────────────────────────────────

        private void SaveSession(string email, string token)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(REG_KEY);
                key?.SetValue("api_email", email);
                key?.SetValue("api_token", token);
            }
            catch { }
        }

        private void TryLoadSavedSession()
        {
            try
            {
                using var key   = Registry.CurrentUser.OpenSubKey(REG_KEY);
                var email       = key?.GetValue("api_email") as string;
                var token       = key?.GetValue("api_token") as string;
                if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(email))
                {
                    _accessToken = token;
                    _userEmail   = email;
                    SetAuthHeader();
                }
            }
            catch { }
        }

        private void ClearSavedSession()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(REG_KEY, true);
                key?.DeleteValue("api_email", false);
                key?.DeleteValue("api_token", false);
            }
            catch { }
        }

        // ── HELPERS ───────────────────────────────────────────────────────────

        private void SetAuthHeader() =>
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _accessToken);

        private async Task<List<ShopmiumOffer>> ParseOffersResponse(string json)
        {
            await Task.Yield();

            // Format normal : array d'offres directement
            try
            {
                var list = JsonSerializer.Deserialize<List<ShopmiumOffer>>(json, _jsonOpts);
                if (list != null && list.Count > 0)
                {
                    // Filtrer les offres invalides (sans id ou sans nom)
                    var valid = list.Where(o => o.Id > 0 && !string.IsNullOrEmpty(o.Name)).ToList();
                    return valid;
                }
                // Si list est null OU vide, on essaie le fallback
            }
            catch (Exception ex)
            {
                // Tracer l'erreur pour debug
                _lastDetailError = $"Désérialisation array : {ex.Message}";
            }

            // Fallback : si racine = object avec wrapper {offers:[...]} ou {data:[...]}
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in new[] { "offers", "data", "items", "results" })
                    {
                        if (root.TryGetProperty(prop, out var el)
                         && el.ValueKind == JsonValueKind.Array)
                        {
                            try
                            {
                                var inner = JsonSerializer.Deserialize<List<ShopmiumOffer>>(
                                    el.GetRawText(), _jsonOpts);
                                if (inner != null && inner.Count > 0)
                                    return inner.Where(o => o.Id > 0 && !string.IsNullOrEmpty(o.Name)).ToList();
                            }
                            catch (Exception ex)
                            {
                                _lastDetailError = $"Désérialisation '{prop}' : {ex.Message}";
                            }
                        }
                    }
                }
            }
            catch { }

            // Dernier recours : parser offre par offre pour identifier laquelle pose problème
            try
            {
                var result = new List<ShopmiumOffer>();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    int errCount = 0;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        idx++;
                        try
                        {
                            var o = JsonSerializer.Deserialize<ShopmiumOffer>(el.GetRawText(), _jsonOpts);
                            if (o != null && o.Id > 0 && !string.IsNullOrEmpty(o.Name))
                                result.Add(o);
                        }
                        catch (Exception ex)
                        {
                            errCount++;
                            if (errCount <= 3) // ne stocker que les 3 premières
                            {
                                var oid = el.TryGetProperty("id", out var idEl) ? idEl.ToString() : "?";
                                _lastDetailError = $"Offre idx={idx} id={oid} : {ex.Message}";
                            }
                        }
                    }
                    if (result.Count > 0) return result;
                }
            }
            catch (Exception ex)
            {
                _lastDetailError = $"Parser fallback : {ex.Message}";
            }

            return new();
        }

        /// <summary>
        /// Extrait tous les IDs d'offres uniques depuis la réponse de /me/nav.
        /// Cherche les structures { "offer": { "id": <int> } } à tous les niveaux.
        /// </summary>
        private static List<long> ExtractOfferIdsFromNav(string json)
        {
            var ids = new HashSet<long>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                ExtractIdsRecursive(doc.RootElement, ids);
            }
            catch { }
            return ids.ToList();
        }

        private static void ExtractIdsRecursive(JsonElement el, HashSet<long> bucket)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                // Pattern : "offer": { "id": <number>, ... }
                if (el.TryGetProperty("offer", out var offer)
                 && offer.ValueKind == JsonValueKind.Object
                 && offer.TryGetProperty("id", out var idEl)
                 && idEl.ValueKind == JsonValueKind.Number
                 && idEl.TryGetInt64(out var id))
                {
                    bucket.Add(id);
                }

                foreach (var prop in el.EnumerateObject())
                    ExtractIdsRecursive(prop.Value, bucket);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    ExtractIdsRecursive(item, bucket);
            }
        }

        // Pour debug : on garde la dernière erreur de fetch détail
        private string _lastDetailError = "";
        private bool   _detailDumped    = false;

        /// <summary>
        /// Récupère les détails d'une offre via GET /me/offers/{id} (passe par le CDN).
        /// </summary>
        private async Task<ShopmiumOffer?> FetchOfferDetailRawAsync(long offerId)
        {
            try
            {
                var resp = await _http.GetAsync(CDN_URL + $"me/offers/{offerId}");
                var json = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _lastDetailError = $"HTTP {(int)resp.StatusCode} sur /me/offers/{offerId} : " +
                                       (json.Length > 150 ? json[..150] : json);
                    return null;
                }

                // Dumper la 1ère réponse pour analyse
                if (!_detailDumped)
                {
                    _detailDumped = true;
                    try
                    {
                        var path = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                            "shopmium_offer_detail.json");
                        File.WriteAllText(path, json);
                    }
                    catch { }
                }

                try
                {
                    return JsonSerializer.Deserialize<ShopmiumOffer>(json, _jsonOpts);
                }
                catch (Exception ex)
                {
                    _lastDetailError = $"Désérialisation /me/offers/{offerId} : {ex.Message}";
                    return null;
                }
            }
            catch (Exception ex)
            {
                _lastDetailError = $"Exception /me/offers/{offerId} : {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Parse la réponse de /me/nav (NavAPI) qui retourne une structure
        /// hiérarchique avec sections contenant les offres. On les flatten.
        /// </summary>
        private static async Task<List<ShopmiumOffer>> ParseNavResponse(string json)
        {
            var result = new List<ShopmiumOffer>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                CollectOffers(doc.RootElement, result);
            }
            catch { }
            await Task.CompletedTask;
            return result;
        }

        /// <summary>
        /// Parcourt récursivement le JSON à la recherche d'objets qui ressemblent
        /// à des offres (avec "id" et "name") et les désérialise en ShopmiumOffer.
        /// </summary>
        private static void CollectOffers(JsonElement el, List<ShopmiumOffer> bucket)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                // Si l'objet a id + name, c'est probablement une offre
                if (el.TryGetProperty("id", out var idEl)
                 && el.TryGetProperty("name", out var nameEl)
                 && nameEl.ValueKind == JsonValueKind.String
                 && (idEl.ValueKind == JsonValueKind.Number || idEl.ValueKind == JsonValueKind.String))
                {
                    try
                    {
                        var o = JsonSerializer.Deserialize<ShopmiumOffer>(el.GetRawText(), _jsonOpts);
                        if (o != null && o.Id > 0 && !string.IsNullOrEmpty(o.Name)
                            && !bucket.Any(x => x.Id == o.Id))
                            bucket.Add(o);
                    }
                    catch { }
                }

                foreach (var prop in el.EnumerateObject())
                    CollectOffers(prop.Value, bucket);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    CollectOffers(item, bucket);
            }
        }

        private static byte[] CompressToJpeg90(byte[] source)
        {
            try
            {
                using var ms  = new MemoryStream(source);
                using var bmp = new System.Drawing.Bitmap(ms);
                using var out_ = new MemoryStream();
                var enc = System.Drawing.Imaging.ImageCodecInfo
                    .GetImageEncoders()
                    .FirstOrDefault(e => e.FormatDescription == "JPEG");
                if (enc != null)
                {
                    var p = new System.Drawing.Imaging.EncoderParameters(1);
                    p.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, 90L);
                    bmp.Save(out_, enc, p);
                }
                else bmp.Save(out_, System.Drawing.Imaging.ImageFormat.Jpeg);
                return out_.ToArray();
            }
            catch { return source; }
        }
    }

    /// <summary>
    /// Résultat de GetSubmissionsBreakdownAsync : statuts des soumissions
    /// + offres historiques fantômes pour celles disparues du catalogue.
    /// </summary>
    public class SubmissionsBreakdown
    {
        /// <summary>IDs des offres avec une soumission ACTIVE (à masquer du catalogue normal).</summary>
        public HashSet<long> Active { get; set; } = new();

        /// <summary>Toutes les soumissions avec leur statut (pour catégorisation UI).</summary>
        public Dictionary<long, string> AllWithStatus { get; set; } = new();

        /// <summary>
        /// Offres "fantômes" construites depuis le JSON des submissions
        /// (utilisées pour afficher l'historique même quand l'offre n'est plus
        /// dans le catalogue actuel — ex: offres expirées).
        /// </summary>
        public Dictionary<long, ShopmiumOffer> HistoricalOffers { get; set; } = new();
    }
}
