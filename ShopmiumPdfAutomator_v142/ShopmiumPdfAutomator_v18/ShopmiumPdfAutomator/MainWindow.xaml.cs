using ShopmiumPdfAutomator.Models;
using ShopmiumPdfAutomator.Services;
using System.Diagnostics;
using System.Windows.Navigation;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Linq;
using System.Windows.Input;

namespace ShopmiumPdfAutomator
{
    /// <summary>Bulle de message dans le chat IA.</summary>
    public class ChatBubble
    {
        public string Sender      { get; set; } = "";
        public string Content     { get; set; } = "";
        public string Background  { get; set; } = "#0D1B2E";
        public string SenderColor { get; set; } = "#00D4FF";
    }

    public partial class MainWindow : Window
    {
        // ── State ─────────────────────────────────────────────────────────────
        private string?     _lastPngPath;
        private byte[]?     _lastPreviewBytes;
        private ProductData? _extractedData;
        private bool _refChosen = false; // true = référence sélectionnée manuellement
        private readonly List<ShopmiumParser.FavoriteItem> _favorites = [];
        private string?     _selectedProductUrl;
        // Chat IA
        private readonly ChatService _chat = new();
        private readonly System.Collections.ObjectModel.ObservableCollection<ChatBubble> _chatItems = [];
        private byte[]? _chatImageData = null;

        // ── Shopmium API ──────────────────────────────────────────────────────
        private readonly ShopmiumApiService _apiService = new();
        private ShopmiumOffer? _selectedApiOffer;
        private string? _apiSelectedImagePath;

        public MainWindow()
        {
            InitializeComponent();

            // ── Vérification des mises à jour (en arrière-plan, sans bloquer) ─
            Loaded += async (_, _) =>
            {
                await Task.Delay(2000); // Attendre que l'UI soit bien chargée
                await CheckForUpdatesAsync();
            };

            // ── Surveillance expiration licence en temps réel ─────────────────
            // Vérifie toutes les 30 secondes :
            //   - 15 min avant : bannière orange d'avertissement
            //   - À l'heure exacte : overlay rouge bloquant + génération désactivée
            Loaded += (_, _) => StartLicenseWatchdog();

            // ── Initialiser l'UI de connexion (comptes mémorisés + auto-login) ─
            Loaded += async (_, _) => await InitializeLoginUiAsync();

            // Sélectionner "Standard" par défaut
            if (CbTicketType != null) CbTicketType.SelectedIndex = 0;
            InitChat();
            UpdateCalc();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  SURVEILLANCE LICENCE EN TEMPS RÉEL
        // ══════════════════════════════════════════════════════════════════════
        //
        //  Logique d'affichage selon la durée totale de la licence :
        //
        //  ┌──────────────────┬────────────────────────────────────────────────┐
        //  │ Durée ≤ 3 jours  │ Chrono hh:mm:ss toujours visible (type 24h)   │
        //  │ (ex : 24h)       │ Orange < 15 min, bleu sinon                    │
        //  ├──────────────────┼────────────────────────────────────────────────┤
        //  │ Durée > 3 jours  │ Phase 1 (> 15 min) : jours restants (bleu)    │
        //  │ (ex : 30/90/180j)│ Phase 2 (≤ 15 min) : mm:ss précis (orange)    │
        //  └──────────────────┴────────────────────────────────────────────────┘

        private System.Windows.Threading.DispatcherTimer? _licenseTimer;
        private bool _licenseBlocked = false;

        private void StartLicenseWatchdog()
        {
            var expiresAt = LicenseService.GetExpiresAt();
            if (!expiresAt.HasValue) return; // licence permanente → aucune surveillance

            _licenseTimer?.Stop();
            _licenseTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _licenseTimer.Tick += (_, _) => CheckLicenseExpiry();
            _licenseTimer.Start();
            CheckLicenseExpiry(); // vérification immédiate au démarrage
        }

        private void CheckLicenseExpiry()
        {
            var expiresAt = LicenseService.GetExpiresAt();
            if (!expiresAt.HasValue) { _licenseTimer?.Stop(); return; }

            var now       = DateTime.Now;
            var remaining = expiresAt.Value - now;

            // ── LICENCE EXPIRÉE ───────────────────────────────────────────────
            if (remaining <= TimeSpan.Zero)
            {
                if (_licenseBlocked) return;
                _licenseBlocked = true;
                _licenseTimer?.Stop();
                if (BtnGenerateAuto   != null) BtnGenerateAuto.IsEnabled   = false;
                if (BtnGenerateManual != null) BtnGenerateManual.IsEnabled = false;
                ShowLicenseExpiredOverlay();
                return;
            }

            // Durée totale pour choisir le mode d'affichage
            var totalDays = LicenseService.GetLicenceDays() ?? 9999;
            bool isShortLicence = totalDays <= 3; // 24h, 48h, 3j → chrono complet

            string txt;
            bool   isWarning;

            if (isShortLicence)
            {
                // ── LICENCE COURTE (≤ 3 jours) : chrono hh:mm:ss toujours affiché ──
                var hh = (int)remaining.TotalHours;
                var mm = remaining.Minutes;
                var ss = remaining.Seconds;
                isWarning = remaining.TotalMinutes <= 15;

                if (hh > 0)
                    txt = $"⏱  Accès {totalDays * 24}h — expire dans {hh:D2}h {mm:D2}m {ss:D2}s";
                else if (mm > 0)
                    txt = $"⚠  Accès {totalDays * 24}h — expire dans {mm:D2}m {ss:D2}s !";
                else
                    txt = $"⚠  Accès {totalDays * 24}h — expire dans {ss:D2} secondes !!";
            }
            else if (remaining <= TimeSpan.FromMinutes(15))
            {
                // ── LICENCE LONGUE — PHASE FINALE : mm:ss précis ─────────────
                var mm2 = (int)remaining.TotalMinutes;
                var ss2 = remaining.Seconds;
                isWarning = true;

                txt = mm2 > 0
                    ? $"⚠  Licence expire dans {mm2:D2}:{ss2:D2}  (à {expiresAt.Value:HH:mm:ss})  — Pensez à renouveler"
                    : $"⚠  Licence expire dans {ss2:D2} secondes  (à {expiresAt.Value:HH:mm:ss})  — RENOUVELEZ !";
            }
            else
            {
                // ── LICENCE LONGUE — PHASE NORMALE : jours restants ──────────
                var days = (int)Math.Ceiling(remaining.TotalDays);
                isWarning = days <= 3;

                txt = days switch
                {
                    1 => "⚠  Il vous reste 1 jour sur votre licence — Pensez à renouveler",
                    2 => "⚠  Il vous reste 2 jours sur votre licence",
                    3 => "ℹ  Il vous reste 3 jours sur votre licence",
                    _ => $"ℹ  Il vous reste {days} jours sur votre licence",
                };
            }

            SetLicenseBanner(txt, isWarning);
        }

        private void SetLicenseBanner(string msg, bool isWarning)
        {
            if (LicenseWarningBanner == null) return;

            var colorWarning = System.Windows.Media.Color.FromRgb(0xFF, 0x8C, 0x00);
            var colorInfo    = System.Windows.Media.Color.FromRgb(0x00, 0xAA, 0xFF);
            var color  = isWarning ? colorWarning : colorInfo;
            var brush  = new System.Windows.Media.SolidColorBrush(color);

            var bgWarning = System.Windows.Media.Color.FromRgb(0x1A, 0x0D, 0x00);
            var bgInfo    = System.Windows.Media.Color.FromRgb(0x00, 0x0E, 0x1A);
            var bgBrush   = new System.Windows.Media.SolidColorBrush(
                isWarning ? bgWarning : bgInfo);

            if (LicenseWarningRow != null && LicenseWarningBanner.Visibility != Visibility.Visible)
                LicenseWarningRow.Height = new GridLength(28);

            LicenseWarningBanner.Visibility  = Visibility.Visible;
            LicenseWarningBanner.BorderBrush = brush;
            LicenseWarningBanner.Background  = bgBrush;

            if (LicenseWarningText != null)
            {
                LicenseWarningText.Text       = msg;
                LicenseWarningText.Foreground = brush;
            }
        }

        private void ShowLicenseExpiredOverlay()
        {
            if (LicenseExpiredOverlay == null) return;

            // Flouter le contenu
            if (MainContent != null)
                MainContent.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 };

            LicenseExpiredOverlay.Visibility = Visibility.Visible;
        }

        private void BtnRenewLicense_Click(object sender, RoutedEventArgs e)
        {
            // Masquer l'overlay expiré
            if (LicenseExpiredOverlay != null)
                LicenseExpiredOverlay.Visibility = Visibility.Collapsed;
            if (MainContent != null)
                MainContent.Effect = null;

            // Ouvrir la fenêtre d'activation
            LicenseService.Deactivate();
            var win = new ActivationWindow(expired: true) { Owner = this };
            if (win.ShowDialog() == true)
            {
                // Licence renouvelée → réactiver les boutons et relancer le watchdog
                if (BtnGenerateAuto    != null) BtnGenerateAuto.IsEnabled    = true;
                if (BtnGenerateManual  != null) BtnGenerateManual.IsEnabled  = true;
                _licenseBlocked      = false;
                if (LicenseWarningBanner != null)
                    LicenseWarningBanner.Visibility = Visibility.Collapsed;
                StartLicenseWatchdog();
            }
            else
            {
                // L'utilisateur a annulé → on rebloque
                ShowLicenseExpiredOverlay();
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  NAVIGATION ONGLETS
        // ══════════════════════════════════════════════════════════════════════
        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelManuel == null || PanelAuto == null) return;
            var tag = ((RadioButton)sender).Tag?.ToString();
            PanelManuel.Visibility = tag == "Manual" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelChat != null)
                PanelChat.Visibility = tag == "Chat" ? Visibility.Visible : Visibility.Collapsed;
            PanelAuto.Visibility   = tag == "Auto"   ? Visibility.Visible : Visibility.Collapsed;
            if (PanelSubmissions != null)
                PanelSubmissions.Visibility = tag == "Submissions" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelParams != null)
                PanelParams.Visibility = tag == "Params" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelShopmiumApi != null)
                PanelShopmiumApi.Visibility = tag == "ShopmiumApi" ? Visibility.Visible : Visibility.Collapsed;

            // Rafraîchir les infos du panneau Params quand il devient visible
            if (tag == "Params") RefreshParamsPanel();

            // Charger les soumissions à la 1ère ouverture de l'onglet Mes soumissions
            if (tag == "Submissions" && !_submissionsLoaded)
                _ = LoadSubmissionsAsync();
        }

        /// <summary>Met à jour les informations affichées dans l'onglet Params.</summary>
        private void RefreshParamsPanel()
        {
            try
            {
                // État API
                if (_apiService.IsConnected)
                {
                    ParamsApiStatus.Text       = "✓ Connecté";
                    ParamsApiStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xA0));
                    ParamsApiAccount.Text      = (ApiEmailCombo.Text ?? "").Trim();
                    ParamsApiOffersCount.Text  = ApiOfferList.Items.Count > 0
                        ? $"{ApiOfferList.Items.Count} disponibles"
                        : "Pas encore chargées";
                }
                else
                {
                    ParamsApiStatus.Text       = "Non connecté";
                    ParamsApiStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x44));
                    ParamsApiAccount.Text      = "—";
                    ParamsApiOffersCount.Text  = "—";
                }

                // Infos licence
                try
                {
                    var licStr = LicenseService.GetLicenseInfo();
                    if (licStr.Contains("permanente", StringComparison.OrdinalIgnoreCase))
                    {
                        ParamsLicenseType.Text   = "Permanente";
                        ParamsLicenseExpiry.Text = "Jamais";
                    }
                    else if (licStr.StartsWith("Valide"))
                    {
                        ParamsLicenseType.Text   = "Temporaire";
                        // Extraire la date après "jusqu'au "
                        var idx = licStr.IndexOf("jusqu'au ");
                        ParamsLicenseExpiry.Text = idx >= 0
                            ? licStr.Substring(idx + "jusqu'au ".Length)
                            : licStr;
                    }
                    else
                    {
                        ParamsLicenseType.Text   = licStr;
                        ParamsLicenseExpiry.Text = "—";
                    }
                }
                catch { }

                // Version
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    var ver = asm.GetName().Version?.ToString() ?? "—";
                    ParamsVersion.Text = $"Version : v{ver}";
                }
                catch { }
            }
            catch { }
        }

        /// <summary>Bouton : ouvre le serveur Discord dans le navigateur.</summary>
        private void BtnOpenDiscord_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://discord.gg/kjSnAVCxrG")
                    { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("err", $"Impossible d'ouvrir le navigateur : {ex.Message}");
            }
        }

        /// <summary>Réinitialise l'historique des identités générées (Carrefour Drive).</summary>
        private void BtnClearIdentityHistory_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "Effacer l'historique des identités générées pour les tickets Carrefour Drive ?\n" +
                "Les prochaines générations pourront réutiliser n'importe quelle combinaison.",
                "Confirmer",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                FrenchIdentityGenerator.ClearHistory();
                SetStatus("ok", "Historique des identités effacé.");
            }
        }

        /// <summary>Efface tous les comptes Shopmium mémorisés (Logins + Switch).</summary>
        private void BtnClearSavedAccounts_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "Effacer tous les comptes Shopmium mémorisés ?\n" +
                "Vous devrez vous reconnecter manuellement.",
                "Confirmer",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            foreach (var email in _savedEmails.ToList())
                DeleteAccount(email);
            _savedEmails.Clear();

            // Effacer aussi le LastAccount
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(LastAccountKey, true);
                key?.DeleteValue("LastAccount", false);
            }
            catch { }

            BtnSwitchAccount.Visibility = Visibility.Collapsed;
            SetStatus("ok", "Tous les comptes mémorisés ont été effacés.");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CALCUL EN TEMPS RÉEL
        // ══════════════════════════════════════════════════════════════════════
        private void Calc_Changed(object sender, EventArgs e) => UpdateCalc();

        private void UpdateCalc()
        {
            if (InputQty == null || InputPrice == null) return;
            if (!int.TryParse(InputQty.Text, out var qty))    qty   = 0;
            if (!double.TryParse(InputPrice.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var price))                                price = 0;

            var tvaRate = GetSelectedTvaRate();
            var total   = Math.Round(qty * price, 2);
            var tva     = Math.Round(total * tvaRate / (1 + tvaRate), 2);

            if (LblTotal != null) LblTotal.Text = $"{total:F2} €";
            if (LblTva   != null) LblTva.Text   = $"{tva:F2} € ({tvaRate*100:0.#}%)";
        }

        private double GetSelectedTvaRate()
        {
            if (CbTva?.SelectedItem is ComboBoxItem item &&
                double.TryParse(item.Tag?.ToString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var r)) return r;
            return 0.20;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  MODE MANUEL — Générer
        // ══════════════════════════════════════════════════════════════════════
        private async void BtnGenerateManual_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(InputQty.Text, out var qty) || qty < 1)
            { ShowError("Quantité invalide."); return; }

            if (!double.TryParse(InputPrice.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var price) || price <= 0)
            { ShowError("Prix invalide."); return; }

            var data = new ProductData
            {
                ProductName  = InputProduct.Text.Trim(),
                MaxArticles  = qty,
                MaxPrice     = price,
                StartDate    = InputDate.Text.Trim(),
                TimeHHMM     = InputTime.Text.Trim(),
                TvaRate      = GetSelectedTvaRate(),
            };
            await GenerateAndDisplay(data);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  MODE AUTO
        // ══════════════════════════════════════════════════════════════════════
        private void BtnParseFavorites_Click(object sender, RoutedEventArgs e)
        {
            var html = InputFavHtml.Text.Trim();
            if (string.IsNullOrEmpty(html))
            { ShowError("Collez d'abord le HTML de la page favoris."); return; }

            try
            {
                _favorites.Clear();
                _favorites.AddRange(ShopmiumParser.ParseFavorites(html));
                ProductList.Items.Clear();
                foreach (var item in _favorites) ProductList.Items.Add(item.Name);

                if (_favorites.Count == 0) ShowError("Aucun produit trouvé.");
                else SetStatus("ok", $"{_favorites.Count} produit(s) — sélectionnez en un");
            }
            catch (Exception ex) { ShowError($"Erreur parsing : {ex.Message}"); }
        }

        private void ProductList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var idx = ProductList.SelectedIndex;
            if (idx < 0 || idx >= _favorites.Count) return;
            var fav = _favorites[idx];
            _selectedProductUrl = fav.Url;
            LblStep3.Text = $"Ouvrez \"{fav.Name}\"\npuis Ctrl+U → Ctrl+A → Ctrl+C et collez :";
            BtnOpenProduct.Visibility = Visibility.Visible;
            BtnOpenProduct.Tag        = fav.Url;
            SetStatus("idle", $"Produit sélectionné : {fav.Name}");
        }

        private void BtnOpenProduct_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedProductUrl == null) return;
            Process.Start(new ProcessStartInfo(_selectedProductUrl) { UseShellExecute = true });
        }

        private async void BtnParseProduct_Click(object sender, RoutedEventArgs e)
        {
            var html = InputProdHtml.Text.Trim();
            if (string.IsNullOrEmpty(html))
            { ShowError("Collez d'abord le HTML de la page produit."); return; }

            try
            {
                _extractedData = ShopmiumParser.ParseProduct(html);
                _refChosen = false; // Réinitialiser à chaque nouveau produit
                ExtractedPanel.Visibility = Visibility.Visible;
                if (RefChosenBar != null) RefChosenBar.Visibility = Visibility.Collapsed;
                ExtProduit.Text   = _extractedData.ProductName;
                ExtArticles.Text  = $"{_extractedData.MaxArticles} article(s)";
                ExtPrix.Text      = $"{_extractedData.MaxPrice:F2} €";
                ExtTotal.Text     = $"{_extractedData.TotalTTC:F2} €";
                // Affichage initial avec taux détecté depuis le rayon Shopmium
                var rayonInfo = string.IsNullOrEmpty(_extractedData.RayonText)
                    ? "" : $" ({_extractedData.RayonText})";
                ExtTva.Text = $"{TvaCalculator.Format(_extractedData.TvaRate)} — {_extractedData.TvaAmount:F2} €{rayonInfo}";
                ExtDate.Text      = _extractedData.StartDate;
                BtnGenerateAuto.IsEnabled = true;

                // Afficher le type de ticket détecté et l'avertissement éventuel
                UpdateTicketTypeDisplay(_extractedData);

                // Afficher le panneau de preuve si requis
                UpdateProofPanel(_extractedData);

                // Bannière orange si TVA incertaine → forcer confirmation utilisateur
                UpdateTvaWarning(_extractedData);

                SetStatus("ok", "Données extraites — analyses en cours...");

                // Lancer en parallèle : TVA + image + EAN (si nécessaire)
                var tasks = new List<Task>
                {
                    RefreshTvaRateAsync(_extractedData),
                    FetchProductImageAsync(_extractedData, html),
                };

                // Recherche EAN : manuelle uniquement via le bouton "Rechercher sur Auchan"
                // La recherche automatique a été supprimée (instable)

                await Task.WhenAll(tasks);
            }
            catch (Exception ex) { ShowError($"Erreur parsing : {ex.Message}"); }
        }

        /// <summary>
        /// Interroge Open Food Facts + Open Beauty Facts + FoodData Central
        /// et met a jour le taux TVA si une meilleure source est trouvee.
        /// </summary>
        private async Task RefreshTvaRateAsync(ProductData data)
        {
            try
            {
                // Si l'utilisateur a forcé un taux manuellement → ne pas écraser
                if (_tvaManualOverride) return;

                SetStatus("working", $"Recherche TVA pour : {data.ProductName}...");

                // Injecter la clé OpenAI pour GPT-4o-mini TVA
                TvaCalculator.OpenAiKey = ImageGenerationService.LoadSettings().OpenAiKey;

                var rate = await TvaCalculator.DetectAsync(data.ProductName, data.RayonText);

                // Mettre a jour le modele et l'affichage
                data.TvaRate = rate;
                data.TvaNeedsConfirmation = false; // async a résolu le doute
                ExtTva.Text  = $"{TvaCalculator.Format(rate)} — {data.TvaAmount:F2} €";

                // Synchroniser les ComboBox
                SyncTvaComboBox(rate);
                // Masquer le warning TVA : le taux est maintenant confirmé
                Dispatcher.Invoke(() => { UpdateTvaWarning(data); });
                // Remettre CbAutoTva sur "Automatique" pour indiquer que c'est auto
                Dispatcher.Invoke(() => {
                    if (CbAutoTva != null && !_tvaManualOverride)
                        CbAutoTva.SelectedIndex = 0;
                });

                SetStatus("ok", $"TVA detectee : {TvaCalculator.Format(rate)} ({data.ProductName})");
            }
            catch
            {
                // Echec silencieux : on garde le taux mots-cles
                SetStatus("ok", "Données extraites — cliquez sur Générer");
            }
        }

        /// <summary>Selectionne le bon taux dans le ComboBox TVA de l'onglet manuel.</summary>
        private void SyncTvaComboBox(double rate)
        {
            if (CbTva == null) return;
            foreach (ComboBoxItem item in CbTva.Items)
            {
                if (double.TryParse(item.Tag?.ToString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double tagRate)
                    && Math.Abs(tagRate - rate) < 0.0001)
                {
                    CbTva.SelectedItem = item;
                    break;
                }
            }
        }

        /// <summary>
        /// Affiche ou masque la bannière orange "TVA à confirmer".
        /// Si TvaNeedsConfirmation=true → bannière visible + CbAutoTva mis en évidence.
        /// </summary>
        private void UpdateTvaWarning(ProductData data)
        {
            if (TvaWarningPanel == null) return;

            if (data.TvaNeedsConfirmation)
            {
                // Afficher la bannière
                TvaWarningPanel.Visibility = Visibility.Visible;
                // Mettre le texte du taux estimé dans le label
                if (TvaWarningLabel != null)
                    TvaWarningLabel.Text =
                        $"⚠ TVA estimée à {TvaCalculator.Format(data.TvaRate)} " +
                        $"— produit non reconnu. Vérifiez et corrigez si nécessaire.";
                // Sélectionner l'entrée correspondante dans CbAutoTva
                SyncCbAutoTva(data.TvaRate);
            }
            else
            {
                TvaWarningPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void SyncCbAutoTva(double rate)
        {
            if (CbAutoTva == null) return;
            foreach (ComboBoxItem item in CbAutoTva.Items)
            {
                if (double.TryParse(item.Tag?.ToString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double tagRate)
                    && Math.Abs(tagRate - rate) < 0.0001)
                {
                    CbAutoTva.SelectedItem = item;
                    return;
                }
            }
        }

        /// <summary>
        /// L'utilisateur a choisi manuellement le taux TVA depuis la bannière d'alerte.
        /// On applique ce taux et on masque la bannière.
        /// </summary>
        private void CbTvaWarning_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_extractedData == null) return;
            if (CbTvaWarning?.SelectedItem is not ComboBoxItem item) return;
            if (!double.TryParse(item.Tag?.ToString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double rate)) return;

            _extractedData.TvaRate              = rate;
            _extractedData.TvaNeedsConfirmation = false;
            ExtTva.Text = $"{TvaCalculator.Format(rate)} — {_extractedData.TvaAmount:F2} € (confirmé manuellement)";

            // Masquer la bannière
            if (TvaWarningPanel != null)
                TvaWarningPanel.Visibility = Visibility.Collapsed;

            // Sync les autres ComboBox TVA
            SyncTvaComboBox(rate);
            SyncCbAutoTva(rate);
        }

        private async void BtnGenerateAuto_Click(object sender, RoutedEventArgs e)
        {
            if (_extractedData == null) { ShowError("Aucune donnée extraite."); return; }

            // Bloquer si la sélection de référence n'est pas complétée
            if (_extractedData.NeedsManualRefChoice && !_refChosen)
            {
                ShowError("Sélectionnez d'abord une référence dans le panneau \"PLUSIEURS RÉFÉRENCES\" avant de générer.");
                return;
            }

            // Bloquer si la TVA n'est pas confirmée
            if (_extractedData.TvaNeedsConfirmation)
            {
                ShowError("Le taux de TVA n'a pas pu être détecté automatiquement.\nSélectionnez le taux correct dans la bannière verte avant de générer.");
                if (TvaWarningPanel != null) TvaWarningPanel.Visibility = Visibility.Visible;
                return;
            }

            await GenerateAndDisplay(_extractedData);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PIPELINE DE GÉNÉRATION — via Photoshop JSX
        // ══════════════════════════════════════════════════════════════════════
        private async Task GenerateAndDisplay(ProductData data)
        {
            // Désactiver les boutons
            BtnGenerateManual.IsEnabled = false;
            if (BtnGenerateAuto != null) BtnGenerateAuto.IsEnabled = false;

            LoadingPanel.Visibility  = Visibility.Visible;
            ModifiedBadge.Visibility = Visibility.Collapsed;
            ChangesCard.Visibility   = Visibility.Collapsed;
            DownloadPanel.Visibility = Visibility.Collapsed;
            SetStatus("working", "Lancement de Photoshop...");

            try
            {
                // Dossier : Bureau/TicketsCarrefour/NomProduit/
                // Protection null sur ProductName (source du "path1 null")
                var rawName = data?.ProductName;
                if (string.IsNullOrWhiteSpace(rawName)) rawName = "Produit";
                var safeName = string.Concat(
                    rawName.Split(Path.GetInvalidFileNameChars(),
                                  StringSplitOptions.RemoveEmptyEntries));
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "Produit";
                if (safeName.Length > 50) safeName = safeName[..50];

                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (string.IsNullOrEmpty(desktopPath))
                    desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                var outDir = Path.Combine(desktopPath, "TicketsCarrefour", safeName);
                Directory.CreateDirectory(outDir);

                // Progress handler
                var progress = new Progress<string>(msg => SetStatus("working", msg));

                // Lancer Photoshop + JSX en arrière-plan
                // Render() gère lui-même le thread STA pour COM — pas besoin de Task.Run imbriqué
                var result = await Task.Run(() =>
                    PhotoshopService.Render(data, outDir, progress));

                _lastPngPath      = result.PngPath;
                _lastPreviewBytes = result.PreviewPngBytes;

                // Auto-remplir l'image dans l'onglet API si pas déjà définie
                UpdateApiSubmitButton();

                // Afficher l'aperçu
                if (result.PreviewPngBytes.Length > 0)
                    ShowPreview(result.PreviewPngBytes);

                ModifiedBadge.Visibility = Visibility.Visible;
                DownloadPanel.Visibility = Visibility.Visible;

                // Carte des modifications
                RenderChangesCard(data);
                ChangesCard.Visibility = Visibility.Visible;

                SetStatus("ok", $"✓ PNG généré — {Path.GetFileName(result.PngPath)}");
                ShowSuccess($"PNG généré avec succès !\n{result.PngPath}");
            }
            catch (FileNotFoundException fnf)
            {
                ShowError(fnf.Message);
                SetStatus("err", "Erreur — fichier introuvable");
            }
            catch (TimeoutException tex)
            {
                ShowError(tex.Message);
                SetStatus("err", "Timeout Photoshop");
            }
            catch (Exception ex)
            {
                ShowError($"Erreur :\n{ex.Message}");
                SetStatus("err", "Erreur de génération");
            }
            finally
            {
                LoadingPanel.Visibility     = Visibility.Collapsed;
                BtnGenerateManual.IsEnabled = true;
                if (BtnGenerateAuto != null) BtnGenerateAuto.IsEnabled = _extractedData != null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  APERÇU
        // ══════════════════════════════════════════════════════════════════════
        private void ShowPreview(byte[] imgBytes)
        {
            var bi = new BitmapImage();
            using var ms = new MemoryStream(imgBytes);
            bi.BeginInit();
            bi.CacheOption  = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            PreviewImage.Source = bi;
        }

        private void RenderChangesCard(ProductData data)
        {
            ChangesGrid.Children.Clear();
            var items = new[]
            {
                ("Date",      $"{data.StartDate} à {data.TimeHHMM}"),
                ("Produit",   data.ProductName.Length > 25 ? data.ProductName[..25]+"…" : data.ProductName),
                ("Qté × PU", $"{data.MaxArticles} × {data.MaxPrice:F2} €"),
                ("Total TTC", $"{data.TotalTTC:F2} €"),
                ("TVA",       $"{data.TvaAmount:F2} € ({TvaCalculator.Format(data.TvaRate)})"),
            };
            foreach (var (key, val) in items)
            {
                var card = new Border
                {
                    Background   = new SolidColorBrush(Color.FromRgb(0x1A, 0x22, 0x30)),
                    CornerRadius = new CornerRadius(5),
                    Padding      = new Thickness(10, 7, 10, 7),
                    Margin       = new Thickness(0, 0, 6, 6),
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = key, FontFamily = new FontFamily("Consolas"), FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x6A, 0x80)), Margin = new Thickness(0,0,0,2) });
                sp.Children.Add(new TextBlock { Text = val, FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xFF)), FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap });
                card.Child = sp;
                ChangesGrid.Children.Add(card);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  TÉLÉCHARGEMENTS
        // ══════════════════════════════════════════════════════════════════════
        private void BtnDownloadPng_Click(object sender, RoutedEventArgs e)
        {
            if (_lastPngPath == null || !File.Exists(_lastPngPath))
            { ShowError("PNG introuvable."); return; }

            // Ouvrir le dossier NomProduit dans l'explorateur
            // et sélectionner le fichier PNG
            var folder = Path.GetDirectoryName(_lastPngPath) ?? "";
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = "/select,\"" + _lastPngPath + "\"",
                UseShellExecute = true,
            });
        }

        private void BtnDownloadJpg_Click(object sender, RoutedEventArgs e)
        {
            if (_lastPreviewBytes == null || _lastPreviewBytes.Length == 0)
            { ShowError("Aucun aperçu disponible."); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Enregistrer l'aperçu",
                Filter   = "Image PNG|*.png",
                FileName = $"ticket_apercu_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllBytes(dlg.FileName, _lastPreviewBytes);
                ShowSuccess($"Aperçu PNG enregistré :\n{dlg.FileName}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  HELPERS UI
        // ══════════════════════════════════════════════════════════════════════
        private void SetStatus(string type, string message)
        {
            Dispatcher.Invoke(() =>
            {
                StatusBarText.Text = message;
                StatusBarDot.Fill  = type switch
                {
                    "ok"      => new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xA0)),
                    "err"     => new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x55)),
                    "working" => new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x00)),
                    "warn"    => new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
                    _         => new SolidColorBrush(Color.FromRgb(0x5A, 0x6A, 0x80)),
                };
            });
        }

        private static void ShowError(string msg) =>
            MessageBox.Show(msg, "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
        private static void ShowSuccess(string msg) =>
            MessageBox.Show(msg, "Succès", MessageBoxButton.OK, MessageBoxImage.Information);
        // ====================================================================
        //  TELECHARGEMENT IMAGE PRODUIT REELLE
        // ====================================================================

        /// <summary>
        /// Telecharge l image reelle du produit depuis le HTML Shopmium.
        /// Met a jour ProductData.ProductImageData et l apercu dans le panneau.
        /// </summary>
        private async Task FetchProductImageAsync(ProductData data, string html)
        {
            try
            {
                SetStatus("working", "Analyse des images produit...");

                var progress = new Progress<string>(msg =>
                    Dispatcher.Invoke(() => SetStatus("working", msg)));

                // Récupérer toutes les images avec leur alt text
                var allWithAlt = ProductImageService.ExtractImageUrlsWithAlt(
                    html, data.ProductName);

                data.AllImagesWithAlt = allWithAlt;
                data.AllImageUrls     = allWithAlt
                    .Where(i => i.Url.Contains("offer_slide_images")
                             || i.Url.Contains("offer_products"))
                    .Select(i => i.Url)
                    .Distinct()
                    .Take(10)
                    .ToList();

                // Sélectionner la meilleure image pour ce produit
                var (bestUrl, bestAlt, isPerfect, note) =
                    ProductImageService.SelectBestImageForProduct(
                        allWithAlt, data.ProductName);

                data.BestImageUrl   = bestUrl;
                data.BestImageAlt   = bestAlt;
                data.BestImageMatch = isPerfect;
                data.BestImageNote  = note;

                // Télécharger la meilleure image
                if (!string.IsNullOrEmpty(bestUrl))
                {
                    try
                    {
                        var req = new System.Net.Http.HttpRequestMessage(
                            System.Net.Http.HttpMethod.Get, bestUrl);
                        req.Headers.UserAgent.ParseAdd(
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0.0.0");
                        using var client = new System.Net.Http.HttpClient();
                        var resp = await client.SendAsync(req);
                        if (resp.IsSuccessStatusCode)
                        {
                            data.BestImageData  = await resp.Content.ReadAsByteArrayAsync();
                            data.ProductImageData = data.BestImageData;
                            data.ProductImageUrl  = bestUrl;
                        }
                    }
                    catch { /* Image non disponible, on continue */ }
                }

                // Fallback FetchBestImageAsync si téléchargement échoué
                if (data.ProductImageData == null)
                {
                    var result = await ProductImageService.FetchBestImageAsync(
                        html, data.ProductName, progress);
                    if (result.Success)
                    {
                        data.ProductImageData = result.ImageData;
                        data.ProductImageUrl  = result.ImageUrl;
                        data.BestImageData    = result.ImageData;
                    }
                }

                // Régénérer les cartes avec toutes les images
                Dispatcher.Invoke(() =>
                {
                    if (_extractedData?.ProofRequirements.Count > 0)
                        UpdateProofPanel(_extractedData);
                    else
                    {
                        // N'afficher le panneau EAN que si l'offre le requiert
                        if (_extractedData?.NeedsEanSearch == true)
                            EanPanel.Visibility = Visibility.Visible;
                    }
                });
            }
            catch (Exception ex)
            {
                SetStatus("ok", "Image produit : erreur (" + ex.Message + ")");
            }
        }

                // ====================================================================
        //  MODELE POUR LES BOUTONS DE CHOIX
        // ====================================================================
        // ====================================================================
        //  MODELE : CARTE D'EXIGENCE
        // ====================================================================
        public class ProofRequirementCard
        {
            public string       Title              { get; set; } = "";
            public string       BtnLabel           { get; set; } = "";
            public string       Visibility         { get; set; } = "Visible";
            public string       BtnVisibility      { get; set; } = "Visible";
            public string       GeminiUrl          { get; set; } = "";
            public string       Prompt             { get; set; } = "";
            public string       PromptVisibility   { get; set; } = "Visible";
            // Images produit
            public List<string> ImageUrls          { get; set; } = [];
            public string       ImagesVisibility   { get; set; } = "Collapsed";
            // Image sélectionnée automatiquement (meilleur match)
            public string       BestImageUrl       { get; set; } = "";
            public string       BestImageNote      { get; set; } = "";
            public string       BestImageVisibility{ get; set; } = "Collapsed";
            public string       BestImageWarning   { get; set; } = "";
            public string       WarnVisibility     { get; set; } = "Collapsed";
            public byte[]?      BestImageData      { get; set; }
        }

        // ====================================================================
        //  PANNEAU EXIGENCES — MISE A JOUR
        // ====================================================================
        private void UpdateProofPanel(ProductData data)
        {
            // EAN visible si l'offre le nécessite OU si ticket = Carrefour Drive
            // (le ticket Drive contient le Code EAN13 → champ obligatoire)
            bool needsEan = data.NeedsEanSearch
                         || data.TicketType == ShopmiumPdfAutomator.Models.TicketType.CarrefourDrive;
            if (EanPanel != null)
                EanPanel.Visibility = needsEan ? Visibility.Visible : Visibility.Collapsed;

            // (Bannière "renseignez l'EAN" retirée — l'EAN est rempli automatiquement via l'API)

            // Rendre ProofPanel visible seulement si des exigences existent
            if (ProofPanel != null)
                ProofPanel.Visibility = data.ProofRequirements.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed;

            if (ProofPanel == null && !needsEan) return;

            if (needsEan)
            {
                TbEanCode.Text = data.BarcodeEan ?? "";
                bool eanFound  = !string.IsNullOrEmpty(data.BarcodeEan) && data.BarcodeEan != "";
                if (TbEanHint != null)
                {
                    bool isDrive = data.TicketType ==
                        ShopmiumPdfAutomator.Models.TicketType.CarrefourDrive;
                    TbEanHint.Text = eanFound
                        ? (isDrive ? "EAN saisi — le ticket Drive sera généré avec cet EAN"
                                   : "EAN présent — modifiable si besoin")
                        : (isDrive ? "⚠ Saisissez le code EAN-13 avant de générer le ticket Drive"
                                   : "Recherchez le produit sur Auchan ou Leclerc ci-dessous");
                }
            }

            // ── Panel sélection référence (si pas de prix dans les références) ──
            // IMPORTANT : si l'utilisateur a DÉJÀ choisi (_refChosen=true), on ne
            // réaffiche PAS le panneau. On garde l'état "réf sélectionnée".
            if (data.NeedsManualRefChoice && data.AllEligibleRefs.Count > 0 && !_refChosen)
            {
                RefChoiceList.Items.Clear();
                foreach (var r in data.AllEligibleRefs)
                    RefChoiceList.Items.Add(BuildRefChoiceItem(r));
                RefChoicePanel.Visibility  = Visibility.Visible;
                ExtractedPanel.Visibility  = Visibility.Collapsed;
                if (RefChosenBar != null) RefChosenBar.Visibility = Visibility.Collapsed;
                if (BtnGenerateAuto != null) BtnGenerateAuto.IsEnabled = false;
                SetStatus("warn", "Choisissez la référence du produit ↑");
            }
            else
            {
                RefChoicePanel.Visibility = Visibility.Collapsed;
                ExtractedPanel.Visibility = Visibility.Visible;
                if (BtnGenerateAuto != null) BtnGenerateAuto.IsEnabled = true;
            }

            var requirements = data.ProofRequirements;

            if (requirements.Count == 0)
            {
                ProofPanel.Visibility = Visibility.Collapsed;
                TbProofCount.Text     = "";
                return;
            }

            ProofPanel.Visibility = Visibility.Visible;
            TbProofCount.Text     = requirements.Count == 1
                ? "1 exigence detectee"
                : $"{requirements.Count} exigences detectees";

            // Construire les cartes
            var cards = requirements.Select(r => BuildCard(r, data)).ToList();
            ProofRequirementCards.ItemsSource = cards;
        }

        private ProofRequirementCard BuildCard(
            ProofRequirement r, ProductData data)
        {
            var ean     = _extractedData?.BarcodeEan ?? "?";
            var name    = data.ProductName;
            var url     = _selectedProductUrl ?? "https://offers.shopmium.com/fr";
            var gemini  = "https://gemini.google.com/";
            var count   = data.MaxArticles > 0 ? data.MaxArticles : 1;

            // Image sélectionnée intelligemment
            var bestUrl   = data.BestImageUrl ?? "";
            var bestAlt   = data.BestImageAlt ?? "";
            var bestMatch = data.BestImageMatch;
            var bestNote  = data.BestImageNote ?? "";
            // Adapter le prompt si mismatch (ex: "8 portions" → "16 portions")
            string AdaptedName()
            {
                if (bestMatch || string.IsNullOrEmpty(bestAlt)) return name;
                // Remplacer les nombres dans le prompt par le nom cible
                return name; // Le prompt mentionne le nom cible; l'image est approchante
            }
            var promptName = AdaptedName();

            // Infos image pour la carte
            bool hasImage = !string.IsNullOrEmpty(bestUrl);
            string imgVis  = hasImage ? "Visible" : "Collapsed";
            string warnVis = hasImage && !bestMatch ? "Visible" : "Collapsed";
            string warning = !bestMatch && hasImage
                ? $"⚠ Image approchante : « {bestAlt} »\n   → Le prompt décrit bien « {name} »"
                : "";

            return r.Type switch
            {
                ProofType.PhotoArticles => new ProofRequirementCard
                {
                    Title          = "📦  Photo des articles achetés",
                    BtnLabel       = "🔗  Ouvrir Gemini pour générer l'image",
                    BtnVisibility  = "Visible",
                    GeminiUrl      = gemini,
                    Prompt         = $"Génère moi une photo réaliste de ce produit : {name} ({url})\n" +
                                     $"comme si la photo avait été prise depuis un iPhone, posée sur la table d'une maison avec une lumière naturelle d'intérieur.\n" +
                                     $"Le produit doit apparaître {count} fois sur la photo.",
                    PromptVisibility    = "Visible",
                    ImageUrls           = data.AllImageUrls,
                    ImagesVisibility    = data.AllImageUrls.Count > 0 ? "Visible" : "Collapsed",
                    BestImageUrl        = bestUrl,
                    BestImageNote       = bestNote,
                    BestImageVisibility = imgVis,
                    BestImageWarning    = warning,
                    WarnVisibility      = warnVis,
                    BestImageData       = data.BestImageData
                },

                ProofType.PhotoEmballage => new ProofRequirementCard
                {
                    Title          = "📦  Photo de la boîte / emballage abîmé(e)",
                    BtnLabel       = "🔗  Ouvrir Gemini pour générer l'image",
                    BtnVisibility  = "Visible",
                    GeminiUrl      = gemini,
                    Prompt         = $"Génère moi une photo réaliste de ce produit : {name} ({url})\n" +
                                     $"comme si la photo avait été prise depuis un iPhone, posée sur la table d'une maison avec une lumière naturelle d'intérieur.\n" +
                                     $"La boîte du produit doit être abîmée / déchirée.",
                    PromptVisibility    = "Visible",
                    ImageUrls           = data.AllImageUrls,
                    ImagesVisibility    = data.AllImageUrls.Count > 0 ? "Visible" : "Collapsed",
                    BestImageUrl        = bestUrl,
                    BestImageNote       = bestNote,
                    BestImageVisibility = imgVis,
                    BestImageWarning    = warning,
                    WarnVisibility      = warnVis,
                    BestImageData       = data.BestImageData
                },

                ProofType.PhotoBarcodeRaye => new ProofRequirementCard
                {
                    Title          = "🔢  Photo du code-barres rayé",
                    BtnLabel       = "🔗  Ouvrir Gemini pour générer l'image",
                    BtnVisibility  = "Visible",
                    GeminiUrl      = gemini,
                    Prompt         = $"Génère moi une photo réaliste de ce produit : {name} ({url})\n" +
                                     $"comme si la photo avait été prise depuis un iPhone, posée sur la table d'une maison avec une lumière naturelle d'intérieur.\n" +
                                     $"Le code-barres du produit (EAN : {ean}) doit être rayé au stylo/feutre.",
                    PromptVisibility    = "Visible",
                    ImageUrls           = data.AllImageUrls,
                    ImagesVisibility    = data.AllImageUrls.Count > 0 ? "Visible" : "Collapsed",
                    BestImageUrl        = bestUrl,
                    BestImageNote       = bestNote,
                    BestImageVisibility = imgVis,
                    BestImageWarning    = warning,
                    WarnVisibility      = warnVis,
                    BestImageData       = data.BestImageData
                },

                ProofType.Ticket or ProofType.PhotoArticlesEtTicket => new ProofRequirementCard
                {
                    Title          = "🧾  " + r.RawText,
                    BtnLabel       = "",
                    BtnVisibility  = "Collapsed",
                    GeminiUrl      = "",
                    Prompt         = "",
                    PromptVisibility = "Collapsed"
                },

                ProofType.PhotoProduitHorsEmballage => new ProofRequirementCard
                {
                    Title          = "📦  Photo du produit hors emballage",
                    BtnLabel       = "🔗  Ouvrir Gemini pour générer l'image",
                    BtnVisibility  = "Visible",
                    GeminiUrl      = gemini,
                    Prompt         = $"Génère moi une photo réaliste de ce produit comme si elle avait été prise avec un iPhone.\n" +
                                     $"Le produit doit apparaître en dehors de son emballage.\n" +
                                     $"Produit : {name} ({url})",
                    PromptVisibility    = "Visible",
                    ImageUrls           = data.AllImageUrls,
                    ImagesVisibility    = data.AllImageUrls.Count > 0 ? "Visible" : "Collapsed",
                    BestImageUrl        = bestUrl,
                    BestImageNote       = bestNote,
                    BestImageVisibility = imgVis,
                    BestImageWarning    = warning,
                    WarnVisibility      = warnVis,
                    BestImageData       = data.BestImageData
                },

                ProofType.PhotoEmballageDecoupe => new ProofRequirementCard
                {
                    Title          = "✂️  Photo de l'emballage découpé (code-barres)",
                    BtnLabel       = "🔗  Ouvrir Gemini pour générer l'image",
                    BtnVisibility  = "Visible",
                    GeminiUrl      = gemini,
                    Prompt         = $"Génère moi une photo réaliste de ce produit comme si elle avait été prise avec un iPhone.\n" +
                                     $"L'emballage doit être découpé au niveau du code-barres.\n" +
                                     $"Produit : {name} ({url})",
                    PromptVisibility    = "Visible",
                    ImageUrls           = data.AllImageUrls,
                    ImagesVisibility    = data.AllImageUrls.Count > 0 ? "Visible" : "Collapsed",
                    BestImageUrl        = bestUrl,
                    BestImageNote       = bestNote,
                    BestImageVisibility = imgVis,
                    BestImageWarning    = warning,
                    WarnVisibility      = warnVis,
                    BestImageData       = data.BestImageData
                },

                _ => new ProofRequirementCard
                {
                    Title          = "📷  " + r.RawText,
                    BtnLabel       = "🔗  Ouvrir Gemini pour générer l'image",
                    BtnVisibility  = "Visible",
                    GeminiUrl      = gemini,
                    Prompt         = $"Génère moi une photo réaliste de ce produit : {name} ({url})\n" +
                                     $"comme si la photo avait été prise depuis un iPhone, posée sur la table d'une maison avec une lumière naturelle d'intérieur.",
                    PromptVisibility    = "Visible",
                    ImageUrls           = data.AllImageUrls,
                    ImagesVisibility    = data.AllImageUrls.Count > 0 ? "Visible" : "Collapsed",
                    BestImageUrl        = bestUrl,
                    BestImageNote       = bestNote,
                    BestImageVisibility = imgVis,
                    BestImageWarning    = warning,
                    WarnVisibility      = warnVis,
                    BestImageData       = data.BestImageData
                }
            };
        }

        // ── Injecter les ImageUrls dans toutes les cartes d'une liste ─────
        private static void InjectImages(
            ProofRequirementCard card, ProductData data)
        {
            card.ImageUrls        = data.AllImageUrls;
            card.ImagesVisibility = data.AllImageUrls.Count > 0 ? "Visible" : "Collapsed";
        }

        // ====================================================================
        //  HANDLERS EAN
        // ====================================================================
        private void BtnCopyEan_Click(object sender, RoutedEventArgs e)
        {
            var ean = TbEanCode?.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(ean) && !string.IsNullOrEmpty(ean))
            {
                Clipboard.SetText(ean);
                SetStatus("ok", "EAN copié : " + ean);
            }
        }

        // ── Sélection manuelle de référence (quand pas de prix) ─────────────
        /// <summary>
        /// Construit un item visuel pour la liste de choix de référence :
        /// miniature produit | nom commercial | EAN
        /// Le Tag porte l'EligibleRef complet pour récupérer tous les champs au clic.
        /// </summary>
        private Button BuildRefChoiceItem(EligibleRef r)
        {
            var btn = new System.Windows.Controls.Button
            {
                Tag               = r,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background        = new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(0x08, 0x10, 0x18)),
                BorderBrush       = new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(0x1A, 0x30, 0x50)),
                BorderThickness   = new Thickness(1),
                Padding           = new Thickness(10, 8, 10, 8),
                Margin            = new Thickness(0, 0, 0, 6),
                Cursor            = System.Windows.Input.Cursors.Hand,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Miniature produit
            var img = new System.Windows.Controls.Image
            {
                Width  = 44,
                Height = 44,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Margin  = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!string.IsNullOrEmpty(r.ImageUrl))
            {
                try
                {
                    img.Source = new System.Windows.Media.Imaging.BitmapImage(
                        new Uri(r.ImageUrl, UriKind.Absolute));
                }
                catch { }
            }
            Grid.SetColumn(img, 0);
            grid.Children.Add(img);

            // Textes
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text       = r.Name,
                Foreground = new System.Windows.Media.SolidColorBrush(
                                 System.Windows.Media.Color.FromRgb(0xC0, 0xE0, 0xFF)),
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(r.Barcode))
                stack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text       = $"EAN : {r.Barcode}",
                    Foreground = new System.Windows.Media.SolidColorBrush(
                                     System.Windows.Media.Color.FromRgb(0x40, 0x60, 0x78)),
                    FontSize   = 9,
                    Margin     = new Thickness(0, 2, 0, 0),
                });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            btn.Content = grid;
            btn.Click  += BtnRefChoice_Click;
            return btn;
        }

        private void BtnRefChoice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn
                || btn.Tag is not EligibleRef chosen
                || _extractedData == null)
                return;

            // ── Appliquer le nom ──
            _extractedData.ProductName = chosen.Name;
            _refChosen = true;

            // ── Appliquer l'EAN ── (point 3 : chaque référence a son propre EAN)
            if (!string.IsNullOrEmpty(chosen.Barcode))
            {
                _extractedData.BarcodeEan = chosen.Barcode;
                if (TbEanCode != null) TbEanCode.Text = chosen.Barcode;
            }

            // ── Appliquer l'image produit ── (point 5 : bonne image pour le prompt)
            if (!string.IsNullOrEmpty(chosen.ImageUrl))
            {
                _extractedData.ProductImageUrl = chosen.ImageUrl;
                _extractedData.BestImageUrl    = chosen.ImageUrl;
            }

            // ── Fermer le panneau de choix ──
            RefChoicePanel.Visibility = Visibility.Collapsed;
            ExtractedPanel.Visibility = Visibility.Visible;
            RefChosenBar.Visibility   = Visibility.Visible;
            TbRefChosenName.Text      = $"✅  Référence : {chosen.Name}";
            ExtProduit.Text           = chosen.Name;

            // ── Sync contexte chat ──
            _chat.ProductContext = chosen.Name;

            // ── Reconstruire panneau preuves + prompt Gemini ──
            UpdateProofPanel(_extractedData);

            SetStatus("ok", $"Référence sélectionnée : {chosen.Name}");
            if (BtnGenerateAuto != null) BtnGenerateAuto.IsEnabled = true;
        }


        private void BtnChangeRef_Click(object sender, RoutedEventArgs e)
        {
            // Réafficher le panneau de choix de référence
            _refChosen = false;
            RefChosenBar.Visibility   = Visibility.Collapsed;
            RefChoicePanel.Visibility = Visibility.Visible;
        }

        private async void BtnGenerateBarcode_Click(object sender, RoutedEventArgs e)
        {
            var ean = TbEanCode?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(ean) || string.IsNullOrEmpty(ean))
            {
                SetStatus("warn", "Saisissez d'abord un code EAN-13.");
                return;
            }
            if (!BarcodeService.IsValidEan13(ean))
            {
                SetStatus("warn", $"'{ean}' n'est pas un EAN-13 valide (13 chiffres).");
                return;
            }
            if (_extractedData != null) _extractedData.BarcodeEan = ean;

            // Toggle : si déjà affiché → masquer
            if (EanBarcodePanel.Visibility == Visibility.Visible)
            {
                EanBarcodePanel.Visibility = Visibility.Collapsed;
                if (BarcodeHelpText != null) BarcodeHelpText.Visibility = Visibility.Collapsed;
                return;
            }

            // Génération via tec-it.com (renvoie un PNG directement)
            // URL : https://barcode.tec-it.com/barcode.ashx?data=XXX&code=EAN13&dpi=96
            try
            {
                var url = $"https://barcode.tec-it.com/barcode.ashx?data={ean}&code=EAN13&dpi=96&imagetype=Png";
                using var http = new System.Net.Http.HttpClient();
                http.Timeout = TimeSpan.FromSeconds(8);
                var bytes = await http.GetByteArrayAsync(url);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                EanBarcodeImage.Source     = bmp;
                EanBarcodeText.Text        = ean;
                EanBarcodePanel.Visibility = Visibility.Visible;
                if (BarcodeHelpText != null) BarcodeHelpText.Visibility = Visibility.Visible;
                SetStatus("ok", $"Code-barres EAN-13 généré : {ean}");
            }
            catch (Exception ex)
            {
                // Fallback : ouvrir tec-it dans le navigateur si génération locale a échoué
                SetStatus("warn", $"Génération locale impossible ({ex.Message}). Ouverture dans le navigateur…");
                try
                {
                    var fallbackUrl = $"https://barcode.tec-it.com/fr/EAN13?data={ean}";
                    Process.Start(new ProcessStartInfo(fallbackUrl) { UseShellExecute = true });
                }
                catch { SetStatus("err", "Impossible d'ouvrir le générateur de code-barres."); }
            }
        }

        /// <summary>Clic droit → Enregistrer sous : sauvegarder l'image du code-barres.</summary>
        private void BtnSaveBarcode_Click(object sender, RoutedEventArgs e)
        {
            if (EanBarcodeImage?.Source is not BitmapSource bmp)
            {
                SetStatus("warn", "Aucun code-barres à enregistrer.");
                return;
            }

            var ean = TbEanCode?.Text?.Trim() ?? "barcode";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Image PNG|*.png|Image JPEG|*.jpg|Tous les fichiers|*.*",
                FileName = $"barcode_{ean}.png",
                DefaultExt = ".png",
                Title = "Enregistrer le code-barres"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
                System.Windows.Media.Imaging.BitmapEncoder enc = ext switch
                {
                    ".jpg" or ".jpeg" => new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 },
                    _ => new System.Windows.Media.Imaging.PngBitmapEncoder(),
                };
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = new FileStream(dlg.FileName, FileMode.Create);
                enc.Save(fs);
                SetStatus("ok", $"Code-barres enregistré : {dlg.FileName}");
            }
            catch (Exception ex)
            {
                SetStatus("err", $"Erreur enregistrement : {ex.Message}");
            }
        }

        /// <summary>Clic droit → Copier l'image : presse-papiers.</summary>
        private void BtnCopyBarcodeImage_Click(object sender, RoutedEventArgs e)
        {
            if (EanBarcodeImage?.Source is BitmapSource bmp)
            {
                try
                {
                    System.Windows.Clipboard.SetImage(bmp);
                    SetStatus("ok", "Image du code-barres copiée dans le presse-papiers.");
                }
                catch (Exception ex)
                {
                    SetStatus("err", $"Erreur copie : {ex.Message}");
                }
            }
        }

        // ====================================================================
        //  SUPPRESSION DES METADATA EXIF
        // ====================================================================

        private byte[]? _exifSourceBytes;
        private byte[]? _exifCleanedBytes;
        private string? _exifSourceName;

        private void BtnExifImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tiff;*.tif|Tous les fichiers|*.*",
                Title  = "Importer l'image générée par Gemini"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                _exifSourceBytes = File.ReadAllBytes(dlg.FileName);
                _exifSourceName  = Path.GetFileName(dlg.FileName);
                _exifCleanedBytes = null;

                var sizeKb = _exifSourceBytes.Length / 1024.0;
                var fmt    = Services.ExifCleanerService.DetectFormat(_exifSourceBytes);
                ExifImportedLabel.Text =
                    $"✓ Importé : {_exifSourceName} ({fmt}, {sizeKb:F1} KB)";
                ExifImportedLabel.Visibility = Visibility.Visible;

                BtnExifClean.IsEnabled  = true;
                BtnExifVerify.IsEnabled = false;
                BtnExifSave.IsEnabled   = false;
                ExifVerifyPanel.Visibility = Visibility.Collapsed;

                SetStatus("ok", $"Image importée : {_exifSourceName}");
            }
            catch (Exception ex)
            {
                SetStatus("err", $"Erreur import : {ex.Message}");
            }
        }

        private void BtnExifClean_Click(object sender, RoutedEventArgs e)
        {
            if (_exifSourceBytes == null)
            {
                SetStatus("warn", "Aucune image importée.");
                return;
            }

            var result = Services.ExifCleanerService.Clean(_exifSourceBytes);
            if (!result.Success)
            {
                SetStatus("err", $"Échec nettoyage : {result.Error}");
                return;
            }

            _exifCleanedBytes = result.CleanedBytes;
            var sizeKb = _exifCleanedBytes!.Length / 1024.0;
            ExifImportedLabel.Text =
                $"✓ Nettoyé : {_exifSourceName} → {result.OriginalFormat}, {result.Width}×{result.Height}, {sizeKb:F1} KB";

            BtnExifVerify.IsEnabled = true;
            BtnExifSave.IsEnabled   = true;
            ExifVerifyPanel.Visibility = Visibility.Collapsed;

            SetStatus("ok", "Metadata retirées. Cliquez sur Vérifier puis Télécharger.");
        }

        private void BtnExifVerify_Click(object sender, RoutedEventArgs e)
        {
            if (_exifCleanedBytes == null)
            {
                SetStatus("warn", "Aucune image nettoyée à vérifier.");
                return;
            }

            var v = Services.ExifCleanerService.Verify(_exifCleanedBytes);
            if (v.Error != null)
            {
                ExifVerifyText.Text       = $"⚠ Erreur lors de la vérification : {v.Error}";
                ExifVerifyText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x44));
            }
            else if (v.HasMetadata)
            {
                ExifVerifyText.Text       = v.Summary;
                ExifVerifyText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x44));
            }
            else
            {
                ExifVerifyText.Text       = v.Summary;
                ExifVerifyText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x5A, 0xB8, 0x78));
            }
            ExifVerifyPanel.Visibility = Visibility.Visible;
        }

        private void BtnExifSave_Click(object sender, RoutedEventArgs e)
        {
            if (_exifCleanedBytes == null)
            {
                SetStatus("warn", "Aucune image nettoyée à enregistrer.");
                return;
            }

            // Déterminer l'extension à partir du format détecté
            var fmt = Services.ExifCleanerService.DetectFormat(_exifCleanedBytes).ToLowerInvariant();
            var defaultExt = fmt switch
            {
                "jpeg" => ".jpg",
                "png"  => ".png",
                "bmp"  => ".bmp",
                "tiff" => ".tif",
                _      => ".jpg"
            };

            var baseName = string.IsNullOrEmpty(_exifSourceName)
                ? "image_nettoyee"
                : Path.GetFileNameWithoutExtension(_exifSourceName) + "_clean";

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Image PNG|*.png|Image JPEG|*.jpg|Tous les fichiers|*.*",
                FileName = baseName + defaultExt,
                DefaultExt = defaultExt,
                Title = "Enregistrer l'image nettoyée"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                File.WriteAllBytes(dlg.FileName, _exifCleanedBytes);
                SetStatus("ok", $"Image nettoyée enregistrée : {dlg.FileName}");
            }
            catch (Exception ex)
            {
                SetStatus("err", $"Erreur enregistrement : {ex.Message}");
            }
        }

        // ====================================================================
        //  GESTION DU TYPE DE TICKET
        // ====================================================================

        /// <summary>
        /// Met à jour l'affichage du type de ticket détecté automatiquement
        /// et sélectionne l'item correspondant dans le ComboBox.
        /// </summary>
        private void UpdateTicketTypeDisplay(ShopmiumPdfAutomator.Models.ProductData data)
        {
            if (TbTicketTypeAuto == null || CbTicketType == null) return;

            // Label lisible du type de ticket détecté
            TbTicketTypeAuto.Text = data.TicketType switch
            {
                ShopmiumPdfAutomator.Models.TicketType.Leclerc        => "→ Leclerc (auto)",
                ShopmiumPdfAutomator.Models.TicketType.CarrefourDrive => "→ Carrefour Drive (auto)",
                _                                                      => "→ Standard (auto)"
            };

            // Sélectionner l'item correspondant dans le ComboBox (sans déclencher SelectionChanged)
            CbTicketType.SelectionChanged -= CbTicketType_SelectionChanged;
            foreach (System.Windows.Controls.ComboBoxItem item in CbTicketType.Items)
            {
                if (item.Tag?.ToString() == data.TicketType.ToString())
                {
                    CbTicketType.SelectedItem = item;
                    break;
                }
            }
            CbTicketType.SelectionChanged += CbTicketType_SelectionChanged;

            // Avertissement uniquement si l'enseigne de l'offre est EXCLUSIVEMENT
            // une enseigne sans template dédié (ex: pharmacie seule, Auchan seul)
            // → Ne pas alerter pour les offres multi-enseignes qui retournent Standard normalement
            bool mismatch = data.TicketMismatch ||
                (!string.IsNullOrEmpty(data.WarningText) &&
                 data.TicketType == ShopmiumPdfAutomator.Models.TicketType.Standard &&
                 IsExclusiveUnsupportedStore(data.WarningText));

            if (TicketMismatchPanel != null)
                TicketMismatchPanel.Visibility = mismatch
                    ? Visibility.Visible : Visibility.Collapsed;

            if (mismatch && TbTicketMismatch != null && !string.IsNullOrEmpty(data.WarningText))
                TbTicketMismatch.Text =
                    "⚠ Le type de ticket ne correspond pas aux conditions de l'offre :\n" +
                    $"\"{data.WarningText}\"";
        }

        /// <summary>
        /// Retourne true si le texte d'avertissement mentionne une enseigne spécifique
        /// pour laquelle on n'a pas de template dédié (ex: pharmacie, Intermarché, etc.).
        /// </summary>
        /// <summary>
        /// Retourne true UNIQUEMENT si l'offre est valable EXCLUSIVEMENT
        /// dans une enseigne sans template dédié (pharmacie seule, Auchan seul...).
        /// Ne déclenche PAS pour les offres multi-enseignes (Carrefour + Leclerc + Intermarché)
        /// qui retournent Standard de façon intentionnelle.
        /// </summary>
        /// <summary>
        /// Retourne true uniquement si l'offre est valable EXCLUSIVEMENT
        /// dans une enseigne pour laquelle on n'a aucun template (ex: pharmacie seule).
        ///
        /// Règle : en mode EXCLUSION, le ticket Standard est toujours correct
        /// car on a simplement exclu certaines enseignes — Carrefour reste disponible.
        /// → ne jamais afficher de mismatch en mode exclusion.
        /// </summary>
        private static bool IsExclusiveUnsupportedStore(string warningText)
        {
            var t = warningText.ToLowerInvariant();

            // Mode EXCLUSION ("sauf X", "hors X", "pas valable chez X", "toute enseigne sauf X")
            // → le Standard est intentionnel, jamais de mismatch
            bool isExclusion = t.Contains("pas valable")    || t.Contains("non valable") ||
                               t.Contains("n'est pas")      || t.Contains("sauf chez")   ||
                               t.Contains("sauf carrefour") || t.Contains("hors carrefour") ||
                               t.Contains("hors leclerc")   || t.Contains("hors auchan")  ||
                               t.Contains("hors casino")    || t.Contains("hors monoprix")||
                               t.Contains("toute enseigne") || t.Contains("à l'exclusion")||
                               t.Contains("a l'exclusion");
            if (isExclusion) return false;

            // Mode INCLUSION : mismatch uniquement si AUCUNE enseigne avec template n'est citée
            bool hasLeclerc     = t.Contains("leclerc");
            bool hasCarrefour   = t.Contains("carrefour");
            bool hasIntermarche = t.Contains("intermarché") || t.Contains("intermarche");
            bool hasSystemeU    = t.Contains("système u")   || t.Contains("systeme u");
            bool hasPharmacy    = t.Contains("pharmacie");

            // Si Leclerc ou Carrefour mentionné → on a un template → pas de mismatch
            if (hasLeclerc || hasCarrefour) return false;

            // Pharmacie seule = mismatch (pas de template pharmacie)
            if (hasPharmacy && !hasIntermarche && !hasSystemeU) return true;

            return false;
        }

        /// <summary>
        /// Quand l'utilisateur change manuellement le type de ticket dans le ComboBox.
        /// </summary>
        private void CbTicketType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_extractedData == null || CbTicketType?.SelectedItem is not
                System.Windows.Controls.ComboBoxItem selected) return;

            var tag = selected.Tag?.ToString() ?? "Standard";
            _extractedData.TicketType = tag switch
            {
                "Leclerc"        => ShopmiumPdfAutomator.Models.TicketType.Leclerc,
                "CarrefourDrive" => ShopmiumPdfAutomator.Models.TicketType.CarrefourDrive,
                _                => ShopmiumPdfAutomator.Models.TicketType.Standard
            };

            // Mettre à jour le label
            if (TbTicketTypeAuto != null)
                TbTicketTypeAuto.Text = tag switch
                {
                    "Leclerc"        => "→ Leclerc (manuel)",
                    "CarrefourDrive" => "→ Carrefour Drive (manuel)",
                    _                => "→ Standard (manuel)"
                };

            SetStatus("ok", $"Type de ticket changé : {selected.Content}");
        }

                /// <summary>
        /// Construit la requête de recherche la plus précise possible.
        /// Stratégie : "MARQUE NomRéférence" — la marque est préfixée pour
        /// lever l'ambiguïté quand le nom seul est trop générique.
        /// Ex : "Toucher Magique Unique" → "RENOVA Toucher Magique Unique"
        /// </summary>
        private string BuildSearchQuery()
        {
            // Utiliser uniquement le nom du produit, sans préfixer la marque
            // (Auchan et Leclerc trouvent mieux avec le nom seul)
            string baseName = "";
            if (_extractedData?.AllEligibleRefs.Count > 0)
                baseName = _extractedData.AllEligibleRefs[0].Name?.Trim() ?? "";
            if (string.IsNullOrEmpty(baseName))
                baseName = _extractedData?.ProductName?.Trim() ?? "";
            if (string.IsNullOrEmpty(baseName))
                return TbEanCode?.Text?.Trim() ?? "";
            return baseName;
        }

        private void BtnSearchManual_Click(object sender, RoutedEventArgs e)
        {
            var q   = Uri.EscapeDataString(BuildSearchQuery());
            var url = $"https://www.auchan.fr/recherche?text={q}";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { SetStatus("err", "Impossible d'ouvrir la recherche Auchan."); }
        }

        private void BtnSearchLeclerc_Click(object sender, RoutedEventArgs e)
        {
            var q   = Uri.EscapeDataString(BuildSearchQuery());
            var url = $"https://www.e.leclerc/recherche?q={q}";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { SetStatus("err", "Impossible d'ouvrir la recherche Leclerc."); }
        }



        private void TbEanCode_TextChanged(object sender, TextChangedEventArgs e)
        {
            var txt   = TbEanCode?.Text?.Trim() ?? "";
            var valid = BarcodeService.IsValidEan13(txt);
            // Mettre à jour le produit courant si EAN valide
            if (_extractedData != null && valid)
                _extractedData.BarcodeEan = txt;
        }

        // ====================================================================
        //  HANDLERS GEMINI + PROMPT
        // ====================================================================
        private void BtnOpenGemini_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var url = btn.Tag?.ToString() ?? "https://gemini.google.com/";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private void BtnCopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var prompt = btn.Tag?.ToString() ?? "";
            if (!string.IsNullOrEmpty(prompt))
            {
                Clipboard.SetText(prompt);
                SetStatus("ok", "Prompt copié dans le presse-papier !");
            }
        }

        private void BtnCopyImageUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var url = btn.Tag?.ToString() ?? "";
            if (!string.IsNullOrEmpty(url))
            {
                Clipboard.SetText(url);
                SetStatus("ok", "Lien image copié !");
            }
        }

        private void BtnCopyImageData_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var imageData = btn.Tag as byte[];
            if (imageData == null || imageData.Length == 0)
            {
                SetStatus("err", "Aucune image chargée à copier.");
                return;
            }
            try
            {
                var bmp = BytesToBitmap(imageData);
                Clipboard.SetImage(bmp);
                SetStatus("ok", "Image copiée dans le presse-papier — collez dans Gemini !");
            }
            catch (Exception ex)
            {
                SetStatus("err", "Erreur copie image : " + ex.Message);
            }
        }

        private void BestProductImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Image img) return;
            var url = img.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(url)) return;

            // Trouver les données dans _extractedData
            if (_extractedData?.BestImageData != null)
            {
                try { img.Source = BytesToBitmap(_extractedData.BestImageData); }
                catch { }
            }
            else if (_extractedData?.ProductImageData != null)
            {
                try { img.Source = BytesToBitmap(_extractedData.ProductImageData); }
                catch { }
            }
        }

        // ====================================================================
        //  (SAUVEGARDE IMAGE — CONSERVÉE VIDE POUR COMPATIBILITÉ)
        // ====================================================================
        private void BtnSaveProofImage_Click(object sender, RoutedEventArgs e) { }

        // ====================================================================
        // ── Parametres API ─────────────────────────────────────────────────────
        // Handler conservé pour compatibilité — le panneau OpenAI a été retiré
        // de l'UI (la TVA est maintenant gérée 100% via les sources publiques).
        private void BtnSaveImageApi_Click(object sender, RoutedEventArgs e)
        {
            // No-op : le panneau a été retiré de l'interface.
        }

        // ── Menu TVA onglet Auto ──────────────────────────────────────────────
        private bool _tvaManualOverride = false;

        private void CbAutoTva_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbAutoTva?.SelectedItem is not ComboBoxItem item) return;
            var tag = item.Tag?.ToString() ?? "auto";

            if (tag == "auto")
            {
                _tvaManualOverride = false;
                // Relancer la détection auto si un produit est chargé
                if (_extractedData != null)
                {
                    _ = RefreshTvaRateAsync(_extractedData);
                }
            }
            else if (double.TryParse(tag,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rate))
            {
                _tvaManualOverride  = true;
                if (_extractedData != null)
                {
                    _extractedData.TvaRate = rate;
                    ExtTva.Text = TvaCalculator.Format(rate) +
                                  " — " + _extractedData.TvaAmount.ToString("F2") + " € (forcé)";
                }
            }
        }

        private void BtnOpenAiLink_Click(object sender, MouseButtonEventArgs e) =>
            Process.Start(new ProcessStartInfo(
                "https://platform.openai.com/api-keys")
                { UseShellExecute = true });

        /// <summary>
        /// Handler universel pour tous les Hyperlink du XAML.
        /// Ouvre l'URL dans le navigateur par défaut.
        /// </summary>
        private void Hyperlink_RequestNavigate(object sender,
            System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
                    { UseShellExecute = true });
                e.Handled = true;
            }
            catch { /* ignorer si le navigateur ne s'ouvre pas */ }
        }

        // ====================================================================
        //  CHAT IA — ÉTAT
        // ====================================================================


        // ====================================================================
        //  CHAT IA — INITIALISATION
        // ====================================================================
        private void InitChat()
        {
            ChatMessages.ItemsSource = _chatItems;
            AddBotMessage(
                "Bonjour ! Je suis GPT-4o integre dans votre application.\n" +
                "\n" +
                "Vous pouvez :\n" +
                "• Me parler d un produit (cliquez 'Injecter contexte' apres extraction)\n" +
                "• Importer une image produit pour que je la voie\n" +
                "• Ajuster la TVA avec le menu deroulant\n" +
                "• Poser n importe quelle question en francais");
        }

        // ====================================================================
        //  INJECTER LE CONTEXTE PRODUIT DANS LE CHAT
        // ====================================================================
        private void BtnInjectContext_Click(object sender, RoutedEventArgs e)
        {
            if (_extractedData == null)
            {
                AddBotMessage("Aucun produit extrait. Allez d abord dans l onglet Auto.");
                return;
            }

            // Construire le contexte produit
            var ctx = new System.Text.StringBuilder();
            ctx.AppendLine($"Produit : {_extractedData.ProductName}");
            ctx.AppendLine($"Prix max : {_extractedData.MaxPrice:F2} €");
            ctx.AppendLine($"Articles max : {_extractedData.MaxArticles}");
            ctx.AppendLine($"Date debut : {_extractedData.StartDate}");
            ctx.AppendLine($"TVA : {TvaCalculator.Format(_extractedData.TvaRate)}");
            ctx.AppendLine($"Total TTC : {_extractedData.TotalTTC:F2} €");

            if (!string.IsNullOrEmpty(_extractedData.BarcodeEan))
                ctx.AppendLine($"EAN-13 : {_extractedData.BarcodeEan}");

            if (_extractedData.ProofRequirements.Count > 0)
            {
                ctx.AppendLine("Exigences de preuve :");
                foreach (var r in _extractedData.ProofRequirements)
                    ctx.AppendLine($"  - {r.RawText}");
            }

            _chat.ProductContext = ctx.ToString();

            // Mettre à jour l'affichage du contexte
            ChatContextPanel.Visibility    = Visibility.Visible;
            TbChatProductName.Text         = _extractedData.ProductName;
            TbChatProductDetails.Text      =
                $"{_extractedData.MaxArticles} article(s) • " +
                $"{_extractedData.MaxPrice:F2} € • " +
                $"TVA {TvaCalculator.Format(_extractedData.TvaRate)}";

            // Exigences
            if (_extractedData.ProofRequirements.Count > 0)
            {
                ChatRequirementsPanel.Visibility = Visibility.Visible;
                ChatRequirementsList.ItemsSource =
                    _extractedData.ProofRequirements
                        .Select((r, i) => $"{i+1}) {r.RawText}")
                        .ToList();
            }

            // Image produit
            if (_extractedData.ProductImageData != null)
            {
                _chatImageData              = _extractedData.ProductImageData;
                ChatImagePreview.Visibility = Visibility.Visible;
                ChatProductThumb.Source     =
                    ProductImageService.ToBitmap(_extractedData.ProductImageData);
                TbChatImageStatus.Text      = "Image officielle Shopmium chargee";
            }

            // Sélectionner la TVA dans le ComboBox
            SyncChatTvaComboBox(_extractedData.TvaRate);

            AddBotMessage(
                $"Contexte charge pour : {_extractedData.ProductName}\n" +
                $"Prix : {_extractedData.MaxPrice:F2} € — " +
                $"TVA : {TvaCalculator.Format(_extractedData.TvaRate)}\n" +
                (_extractedData.ProofRequirements.Count > 0
                    ? $"Exigences detectees : " +
                      string.Join(", ", _extractedData.ProofRequirements.Select(r => r.Label))
                    : "Aucune exigence specifique detectee") +
                "\n\nComment puis-je vous aider avec ce produit ?");
        }

        // ====================================================================
        //  IMPORT IMAGE MANUELLE
        // ====================================================================
        private void BtnChatImportImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Importer une image produit",
                Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Tous|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                _chatImageData              = File.ReadAllBytes(dlg.FileName);
                ChatImagePreview.Visibility = Visibility.Visible;
                ChatProductThumb.Source     = BytesToBitmap(_chatImageData);
                TbChatImageStatus.Text      =
                    $"Image importee : {Path.GetFileName(dlg.FileName)}";
                ChatContextPanel.Visibility = Visibility.Visible;

                AddBotMessage(
                    "Image importee ! Elle sera jointe a votre prochain message " +
                    "pour que je puisse la voir.");
            }
            catch (Exception ex)
            {
                AddBotMessage($"Erreur import : {ex.Message}");
            }
        }

        // ====================================================================
        //  TVA — MENU DÉROULANT CHAT
        // ====================================================================
        private void SyncChatTvaComboBox(double rate)
        {
            if (CbChatTva == null) return;
            foreach (ComboBoxItem item in CbChatTva.Items)
            {
                if (item.Tag is string tag &&
                    double.TryParse(tag,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var r)
                    && Math.Abs(r - rate) < 0.001)
                {
                    CbChatTva.SelectedItem = item;
                    return;
                }
            }
        }

        private void CbChatTva_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbChatTva?.SelectedItem is not ComboBoxItem item) return;
            if (!double.TryParse(item.Tag?.ToString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var rate)) return;

            if (_extractedData != null)
            {
                _extractedData.TvaRate = rate;
                TbChatProductDetails.Text =
                    $"{_extractedData.MaxArticles} article(s) • " +
                    $"{_extractedData.MaxPrice:F2} € • " +
                    $"TVA {TvaCalculator.Format(rate)}";
                // Re-injecter le contexte mis à jour
                if (_chat.ProductContext != null)
                    _chat.ProductContext =
                        _chat.ProductContext
                            .Replace(
                                System.Text.RegularExpressions.Regex.Match(
                                    _chat.ProductContext,
                                    @"TVA : [0-9,\.]+%").Value,
                                $"TVA : {TvaCalculator.Format(rate)}");
            }
        }

        // ====================================================================
        //  ENVOI MESSAGES
        // ====================================================================
        private async void BtnSendChat_Click(object sender, RoutedEventArgs e)
            => await SendChatMessage();

        private async void TbChatInput_KeyDown(
            object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter
                && (System.Windows.Input.Keyboard.Modifiers
                    & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                await SendChatMessage();
            }
        }

        private async Task SendChatMessage()
        {
            var text = TbChatInput.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var apiKey = ImageGenerationService.LoadSettings().OpenAiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                AddBotMessage(
                    "Cle API OpenAI manquante.\n" +
                    "Configurez-la dans l onglet Params.",
                    isError: true);
                return;
            }

            // Capturer si une image est en attente
            var imageToSend  = _chatImageData;
            _chatImageData   = null; // consommer l'image

            // Afficher message utilisateur
            _chatItems.Add(new ChatBubble
            {
                Sender      = "Vous" + (imageToSend != null ? " + 📷" : ""),
                Content     = text,
                Background  = "#0A1A30",
                SenderColor = "#88AACC"
            });
            TbChatInput.Text = "";
            ScrollChatToBottom();

            // Indicateur ...
            var thinking = new ChatBubble
            {
                Sender      = "Assistant",
                Content     = "...",
                Background  = "#0D1B2E",
                SenderColor = "#00D4FF"
            };
            _chatItems.Add(thinking);
            BtnSendChat.IsEnabled = false;
            ScrollChatToBottom();

            try
            {
                string response;
                if (imageToSend != null)
                    response = await _chat.SendWithImageAsync(text, imageToSend, apiKey);
                else
                    response = await _chat.SendAsync(text, apiKey);

                var idx = _chatItems.IndexOf(thinking);
                if (idx >= 0)
                    _chatItems[idx] = new ChatBubble
                    {
                        Sender      = "Assistant",
                        Content     = response,
                        Background  = "#0D1B2E",
                        SenderColor = "#00D4FF"
                    };
            }
            catch (Exception ex)
            {
                var idx = _chatItems.IndexOf(thinking);
                if (idx >= 0)
                    _chatItems[idx] = new ChatBubble
                    {
                        Sender      = "Erreur",
                        Content     = ex.Message,
                        Background  = "#1A0A0A",
                        SenderColor = "#FF4444"
                    };
            }
            finally
            {
                BtnSendChat.IsEnabled = true;
                ScrollChatToBottom();

                // Si image consommée, remettre badge image
                if (imageToSend != null)
                    TbChatImageStatus.Text = "Image envoyee. Importez-en une nouvelle si besoin.";
            }
        }

        // ====================================================================
        //  EFFACER CONVERSATION
        // ====================================================================
        private void BtnClearChat_Click(object sender, RoutedEventArgs e)
        {
            _chat.ClearHistory();
            _chatItems.Clear();
            _chatImageData                  = null;
            ChatContextPanel.Visibility     = Visibility.Collapsed;
            ChatRequirementsPanel.Visibility= Visibility.Collapsed;
            ChatImagePreview.Visibility     = Visibility.Collapsed;

            AddBotMessage(
                "Nouvelle conversation. Cliquez sur 'Injecter contexte' " +
                "pour charger les donnees du produit en cours.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void AddBotMessage(string text, bool isError = false)
        {
            _chatItems.Add(new ChatBubble
            {
                Sender      = isError ? "Erreur" : "Assistant",
                Content     = text,
                Background  = isError ? "#1A0A0A" : "#0D1B2E",
                SenderColor = isError ? "#FF4444" : "#00D4FF"
            });
            ScrollChatToBottom();
        }

        private static System.Windows.Media.Imaging.BitmapImage BytesToBitmap(byte[] data)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new System.IO.MemoryStream(data);
            bmp.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void ScrollChatToBottom()
        {
            Dispatcher.InvokeAsync(() =>
            {
                ChatScrollViewer?.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }


        // ══════════════════════════════════════════════════════════════════════
        //  MISE À JOUR SILENCIEUSE + OVERLAY BLOQUANT
        // ══════════════════════════════════════════════════════════════════════
        private async Task CheckForUpdatesAsync()
        {
            try
            {
                var info = await UpdateService.CheckAsync();
                if (info == null) return;

                // Afficher l'overlay IMMÉDIATEMENT (bloque l'app)
                // Le bouton "Redémarrer" reste désactivé pendant le téléchargement
                Dispatcher.Invoke(() => ShowUpdateOverlay(info, ready: false));

                // Téléchargement silencieux pendant que l'overlay est affiché
                UpdateService.DownloadProgress += (received, total, pct) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (OverlayProgressPanel != null)
                            OverlayProgressPanel.Visibility = Visibility.Visible;
                        if (OverlayProgressBar != null) OverlayProgressBar.Value = pct;
                        if (OverlayProgressLabel != null)
                            OverlayProgressLabel.Text = $"Téléchargement... {pct}%";
                        if (OverlaySizeLabel != null && total > 0)
                            OverlaySizeLabel.Text =
                                $"{received/1024/1024.0:F1} Mo / {total/1024/1024.0:F1} Mo";
                    });
                };

                UpdateService.UpdateReady += (readyInfo) =>
                {
                    Dispatcher.Invoke(() => ShowUpdateOverlay(readyInfo, ready: true));
                };

                await UpdateService.DownloadSilentlyAsync(info);
            }
            catch { /* silencieux */ }
        }

        /// <summary>
        /// Affiche ou met à jour l'overlay de mise à jour bloquant.
        /// ready=false → bouton désactivé (téléchargement en cours)
        /// ready=true  → bouton activé (prêt à redémarrer)
        /// </summary>
        private void ShowUpdateOverlay(UpdateService.UpdateInfo info, bool ready)
        {
            if (UpdateOverlay == null) return;

            // Remplir les infos
            if (OverlayVersionLabel != null)
                OverlayVersionLabel.Text =
                    $"v{UpdateService.CurrentVersion}  →  v{info.Version}";

            if (OverlayNotesLabel != null)
                OverlayNotesLabel.Text = string.IsNullOrWhiteSpace(info.Notes)
                    ? "Améliorations et corrections de bugs."
                    : info.Notes.Replace("\\n", "\n");

            // Activer/désactiver le bouton
            if (OverlayRestartBtn != null)
            {
                OverlayRestartBtn.IsEnabled = ready;
                OverlayRestartBtn.Content   = ready
                    ? "🔄  Redémarrer et installer la mise à jour"
                    : "🔄  Redémarrer et installer la mise à jour";
            }

            if (OverlayWaitLabel != null)
                OverlayWaitLabel.Visibility = ready
                    ? Visibility.Collapsed : Visibility.Visible;

            if (ready && OverlayProgressPanel != null)
            {
                OverlayProgressPanel.Visibility = Visibility.Collapsed;
                if (OverlayProgressBar != null) OverlayProgressBar.Value = 100;
            }

            // Afficher l'overlay — bloque toute interaction avec l'app
            UpdateOverlay.Visibility = Visibility.Visible;

            // Flouter le contenu derrière
            if (MainContent != null)
                MainContent.Effect = new System.Windows.Media.Effects.BlurEffect
                    { Radius = 6 };
        }

        private void OverlayRestartBtn_Click(object sender, RoutedEventArgs e)
        {
            UpdateService.InstallAndRestart();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  SHOPMIUM API — CONNEXION, OFFRES, SOUMISSION
        // ══════════════════════════════════════════════════════════════════════

        // ── Gestion des identifiants mémorisés ────────────────────────────────
        // Stockés dans HKCU\Software\ShopmiumHelper\Accounts (sous-clés par email)
        // Le mot de passe est chiffré avec DPAPI (lié au compte Windows)

        private const string AccountsKey = @"Software\ShopmiumHelper\Accounts";
        private const string LastAccountKey = @"Software\ShopmiumHelper";

        private List<string> LoadSavedEmails()
        {
            var emails = new List<string>();
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AccountsKey);
                if (key != null)
                {
                    foreach (var sub in key.GetSubKeyNames())
                        emails.Add(sub);
                }
            }
            catch { }
            return emails;
        }

        private string? LoadPasswordFor(string email)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{AccountsKey}\{email}");
                if (key?.GetValue("Password") is byte[] encrypted)
                {
                    var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                        encrypted, null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
            }
            catch { }
            return null;
        }

        private void SaveAccount(string email, string password)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{AccountsKey}\{email}");
                var bytes = System.Text.Encoding.UTF8.GetBytes(password);
                var encrypted = System.Security.Cryptography.ProtectedData.Protect(
                    bytes, null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                key.SetValue("Password", encrypted, Microsoft.Win32.RegistryValueKind.Binary);
                key.SetValue("LastUsed", DateTime.Now.ToString("O"));
            }
            catch { }
        }

        private void DeleteAccount(string email)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AccountsKey, true);
                key?.DeleteSubKey(email, false);
            }
            catch { }
        }

        private string? LoadLastAccountEmail()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LastAccountKey);
                return key?.GetValue("LastAccount") as string;
            }
            catch { return null; }
        }

        private void SaveLastAccountEmail(string email)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(LastAccountKey);
                key.SetValue("LastAccount", email);
            }
            catch { }
        }

        // ── État de la popup d'autocomplétion email ──────────────────────────
        // _savedEmails contient TOUS les emails mémorisés ; on filtre dynamiquement
        // selon ce que tape l'utilisateur. L'autoLogin se fait uniquement au
        // démarrage si on a un mot de passe enregistré.
        private List<string> _savedEmails = new();

        /// <summary>
        /// Au démarrage : charger la liste des comptes mémorisés. Pré-remplir
        /// l'email courant (mais SANS forcer la sélection du dropdown) et
        /// tenter une reconnexion auto si on a un mot de passe.
        /// </summary>
        private async Task InitializeLoginUiAsync()
        {
            _savedEmails = LoadSavedEmails();

            var lastEmail = LoadLastAccountEmail();
            if (!string.IsNullOrEmpty(lastEmail) && _savedEmails.Contains(lastEmail))
            {
                // Pré-remplir le champ (texte uniquement, pas de popup ouverte)
                ApiEmailCombo.Text = lastEmail;
                var pwd = LoadPasswordFor(lastEmail);
                if (!string.IsNullOrEmpty(pwd))
                {
                    ApiPassword.Password = pwd;
                    // Reconnexion auto silencieuse
                    await TryAutoLoginAsync(lastEmail, pwd);
                }
            }

            BtnSwitchAccount.Visibility = _savedEmails.Count > 1
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task TryAutoLoginAsync(string email, string password)
        {
            ApiLoginStatus.Text = "Reconnexion automatique…";
            ApiLoginStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x80, 0xA0, 0xC0));

            var (success, _) = await _apiService.LoginAsync(email, password);
            if (success)
            {
                OnLoginSuccess(email, autoSilent: true);
                // Charger automatiquement les offres si on a réussi le login auto
                _ = AutoLoadOffersOnStartupAsync();
            }
            else
            {
                ApiLoginStatus.Text = "Connectez-vous pour démarrer";
                ApiLoginStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x80, 0xA0, 0xC0));
            }
        }

        private async Task AutoLoadOffersOnStartupAsync()
        {
            await Task.Delay(250); // laisser l'UI s'afficher
            if (_apiService.IsConnected && ApiOfferList.Items.Count == 0)
            {
                Dispatcher.Invoke(() => BtnApiLoadOffers_Click(this, new RoutedEventArgs()));
            }
        }

        private void OnLoginSuccess(string email, bool autoSilent = false)
        {
            ApiLoginStatus.Text       = $"✓ Connecté : {email}";
            ApiLoginStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xA0));
            BtnApiLoadOffers.IsEnabled = true;
            BtnApiLogin.Visibility     = Visibility.Collapsed;
            BtnApiLogout.Visibility    = Visibility.Visible;
            if (!autoSilent)
                SetStatus("ok", "API Shopmium : connecté !");
        }

        /// <summary>Connexion à l'API Shopmium.</summary>
        private async void BtnApiLogin_Click(object sender, RoutedEventArgs e)
        {
            var email    = (ApiEmailCombo.Text ?? "").Trim();
            var password = string.IsNullOrEmpty(ApiPassword.Password)
                ? ApiPasswordVisible.Text
                : ApiPassword.Password;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                ApiLoginStatus.Text       = "⚠ Email ou mot de passe vide";
                ApiLoginStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x00));
                return;
            }

            BtnApiLogin.IsEnabled  = false;
            ApiLoginStatus.Text    = "Connexion en cours…";
            ApiLoginStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x80, 0xA0, 0xC0));

            var (success, message) = await _apiService.LoginAsync(email, password);
            BtnApiLogin.IsEnabled = true;

            if (success)
            {
                if (ChkRememberMe.IsChecked == true)
                {
                    SaveAccount(email, password);
                    SaveLastAccountEmail(email);

                    // Mettre à jour la liste interne (sans ouvrir de popup)
                    if (!_savedEmails.Contains(email))
                        _savedEmails.Add(email);
                    BtnSwitchAccount.Visibility = _savedEmails.Count > 1
                        ? Visibility.Visible : Visibility.Collapsed;
                }
                OnLoginSuccess(email);
            }
            else
            {
                ApiLoginStatus.Text       = $"✗ {message}";
                ApiLoginStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44));
            }
        }

        /// <summary>Déconnexion (efface aussi le mot de passe mémorisé).</summary>
        private void BtnApiLogout_Click(object sender, RoutedEventArgs e)
        {
            var email = (ApiEmailCombo.Text ?? "").Trim();
            _apiService.Logout();

            // Effacer le password mémorisé pour ce compte (mais garder l'email
            // dans la liste si l'utilisateur veut revenir)
            // On vide juste le password local, pas la clé registre
            ApiPassword.Password = "";
            ApiPasswordVisible.Text = "";

            ApiLoginStatus.Text       = "Déconnecté";
            ApiLoginStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x80, 0xA0, 0xC0));
            BtnApiLogin.Visibility    = Visibility.Visible;
            BtnApiLogout.Visibility   = Visibility.Collapsed;
            BtnApiLoadOffers.IsEnabled = false;
            ApiOfferList.Items.Clear();
            ApiOfferDetail.Visibility = Visibility.Collapsed;
            SetStatus("ok", "Déconnecté");
        }

        /// <summary>Affiche la liste complète des comptes mémorisés (bouton "Changer de compte").</summary>
        private void BtnSwitchAccount_Click(object sender, RoutedEventArgs e)
        {
            ShowEmailSuggestions(filter: null);
        }

        /// <summary>Ouvre la popup quand l'utilisateur clique dans le champ email.</summary>
        private void ApiEmailCombo_GotFocus(object sender, RoutedEventArgs e)
        {
            // Ne montrer la popup que s'il y a des comptes mémorisés
            ShowEmailSuggestions(filter: ApiEmailCombo.Text);
        }

        /// <summary>Ferme la popup quand le focus quitte le champ (sauf si on clique dans la popup).</summary>
        private void ApiEmailCombo_LostFocus(object sender, RoutedEventArgs e)
        {
            // Petit délai : permet aux clics dans la popup d'être traités avant fermeture
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ApiEmailSuggestionsList?.IsKeyboardFocusWithin == true) return;
                ApiEmailSuggestionsPopup.IsOpen = false;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Filtre dynamiquement les suggestions selon ce que tape l'utilisateur.</summary>
        private void ApiEmailCombo_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!ApiEmailCombo.IsKeyboardFocused && !ApiEmailCombo.IsKeyboardFocusWithin) return;
            ShowEmailSuggestions(filter: ApiEmailCombo.Text);
        }

        /// <summary>Flèches haut/bas → naviguer dans les suggestions, Entrée → choisir, Échap → fermer.</summary>
        private void ApiEmailCombo_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!ApiEmailSuggestionsPopup.IsOpen)
            {
                if (e.Key == System.Windows.Input.Key.Down
                 && ApiEmailSuggestionsList.Items.Count > 0)
                {
                    ApiEmailSuggestionsPopup.IsOpen = true;
                    ApiEmailSuggestionsList.SelectedIndex = 0;
                    var item = ApiEmailSuggestionsList.ItemContainerGenerator
                        .ContainerFromIndex(0) as System.Windows.Controls.ListBoxItem;
                    item?.Focus();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == System.Windows.Input.Key.Escape)
            {
                ApiEmailSuggestionsPopup.IsOpen = false;
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Down
                  && ApiEmailSuggestionsList.Items.Count > 0)
            {
                ApiEmailSuggestionsList.SelectedIndex = 0;
                var item = ApiEmailSuggestionsList.ItemContainerGenerator
                    .ContainerFromIndex(0) as System.Windows.Controls.ListBoxItem;
                item?.Focus();
                e.Handled = true;
            }
        }

        /// <summary>Clic sur une suggestion : remplit le champ + le mot de passe.</summary>
        private void ApiEmailSuggestionsList_PreviewMouseLeftButtonUp(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            // Vérifier qu'on n'a pas cliqué sur le bouton ×
            if (e.OriginalSource is System.Windows.DependencyObject src)
            {
                var btn = FindAncestorButton(src);
                if (btn != null) return; // c'est le clic du bouton supprimer
            }

            if (ApiEmailSuggestionsList.SelectedItem is string email)
                ApplySelectedEmail(email);
        }

        private void ApiEmailSuggestionsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter
             && ApiEmailSuggestionsList.SelectedItem is string email)
            {
                ApplySelectedEmail(email);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                ApiEmailSuggestionsPopup.IsOpen = false;
                ApiEmailCombo.Focus();
                e.Handled = true;
            }
        }

        /// <summary>Applique l'email sélectionné : remplit le champ + charge le mot de passe.</summary>
        private void ApplySelectedEmail(string email)
        {
            if (string.IsNullOrEmpty(email)) return;

            ApiEmailCombo.Text = email;
            ApiEmailCombo.CaretIndex = email.Length;

            var pwd = LoadPasswordFor(email);
            if (!string.IsNullOrEmpty(pwd))
            {
                ApiPassword.Password = pwd;
                ApiPasswordVisible.Text = pwd;
            }

            ApiEmailSuggestionsPopup.IsOpen = false;
            ApiPassword.Focus();
        }

        /// <summary>Affiche la popup avec les emails mémorisés (filtrés par le texte tapé).</summary>
        private void ShowEmailSuggestions(string? filter)
        {
            if (_savedEmails.Count == 0)
            {
                ApiEmailSuggestionsPopup.IsOpen = false;
                return;
            }

            List<string> filtered;
            if (string.IsNullOrWhiteSpace(filter))
            {
                filtered = _savedEmails.ToList();
            }
            else
            {
                var f = filter.Trim();
                filtered = _savedEmails
                    .Where(e => e.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                             && !string.Equals(e, f, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (filtered.Count == 0)
            {
                ApiEmailSuggestionsPopup.IsOpen = false;
                return;
            }

            ApiEmailSuggestionsList.ItemsSource = filtered;
            ApiEmailSuggestionsPopup.IsOpen = true;
        }

        /// <summary>Bouton × dans la suggestion : supprime ce compte de la mémoire.</summary>
        private void BtnRemoveSavedAccount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn
             && btn.Tag is string email)
            {
                DeleteAccount(email);
                _savedEmails.Remove(email);

                if (_savedEmails.Count == 0)
                {
                    ApiEmailSuggestionsPopup.IsOpen = false;
                    BtnSwitchAccount.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ShowEmailSuggestions(filter: ApiEmailCombo.Text);
                    BtnSwitchAccount.Visibility = _savedEmails.Count > 1
                        ? Visibility.Visible : Visibility.Collapsed;
                }

                // Si on supprime le compte actuellement affiché → vider les champs
                if (string.Equals(ApiEmailCombo.Text, email, StringComparison.OrdinalIgnoreCase))
                {
                    ApiEmailCombo.Text = "";
                    ApiPassword.Password = "";
                    ApiPasswordVisible.Text = "";
                }
            }
            e.Handled = true;
        }

        /// <summary>Helper : remonte l'arborescence visuelle pour trouver un bouton parent.</summary>
        private static System.Windows.Controls.Button? FindAncestorButton(System.Windows.DependencyObject start)
        {
            var current = start;
            while (current != null)
            {
                if (current is System.Windows.Controls.Button b) return b;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>Toggle icône œil = mot de passe visible/masqué.</summary>
        private bool _passwordVisible = false;
        private void BtnShowPassword_Click(object sender, RoutedEventArgs e)
        {
            _passwordVisible = !_passwordVisible;
            if (_passwordVisible)
            {
                ApiPasswordVisible.Text       = ApiPassword.Password;
                ApiPasswordVisible.Visibility = Visibility.Visible;
                ApiPassword.Visibility        = Visibility.Collapsed;
                BtnShowPassword.Content       = "🙈";
            }
            else
            {
                ApiPassword.Password          = ApiPasswordVisible.Text;
                ApiPassword.Visibility        = Visibility.Visible;
                ApiPasswordVisible.Visibility = Visibility.Collapsed;
                BtnShowPassword.Content       = "👁";
            }
        }

        // Synchroniser les 2 champs (caché ↔ visible)
        private bool _syncingPwd = false;
        private void ApiPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPwd) return;
            _syncingPwd = true;
            ApiPasswordVisible.Text = ApiPassword.Password;
            _syncingPwd = false;
        }
        private void ApiPasswordVisible_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_syncingPwd) return;
            _syncingPwd = true;
            ApiPassword.Password = ApiPasswordVisible.Text;
            _syncingPwd = false;
        }

        /// <summary>Charge la liste des offres depuis l'API.</summary>
        private List<ShopmiumOffer> _allLoadedOffers = new();

        // ─── Onglet "Mes soumissions" ──────────────────────────────────────
        private bool _submissionsLoaded = false;

        private async void BtnRefreshSubmissions_Click(object sender, RoutedEventArgs e)
        {
            await LoadSubmissionsAsync();
        }

        /// <summary>
        /// Handler du bouton "📷 Envoyer une preuve" sur une soumission admin_inquired.
        /// Ouvre un picker de fichier, nettoie l'EXIF, puis upload via l'API Shopmium :
        ///   1. POST /me/upload_urls → reçoit URL S3 + reference
        ///   2. PUT URL S3 avec les bytes JPEG
        ///   3. PUT /me/submissions/{id} avec proofs:[{purpose, reference}]
        /// </summary>
        private async void BtnAddProofToSubmission_Click(object sender, RoutedEventArgs e)
        {
            // Récupérer la soumission depuis le Tag du bouton
            if (sender is not System.Windows.Controls.Button btn) return;
            if (btn.Tag is not SubmissionApiModel sub) return;
            if (sub.MainCoupon == null)
            {
                MessageBox.Show("Soumission invalide (coupon manquant).", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 1. Confirmation utilisateur
            var offerName = sub.MainCoupon.OfferTitle;
            var statusReason = sub.MainCoupon.StatusReason ?? "(aucun détail)";
            var confirm = MessageBox.Show(
                $"Envoyer une nouvelle preuve photo pour :\n\n" +
                $"📌 {offerName}\n\n" +
                $"Demande de Shopmium :\n{statusReason}\n\n" +
                $"Continuer ?",
                "Envoyer une preuve",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            // 2. Sélection du fichier image
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Choisir la photo à envoyer à Shopmium",
                Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|Tous les fichiers (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            var imagePath = dlg.FileName;

            // 3. Lire et nettoyer EXIF
            byte[] cleanBytes;
            try
            {
                var raw = File.ReadAllBytes(imagePath);
                var cleanResult = ExifCleanerService.Clean(raw);
                if (cleanResult.CleanedBytes == null || cleanResult.CleanedBytes.Length == 0)
                {
                    MessageBox.Show("Échec du nettoyage EXIF de l'image.", "Erreur",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                cleanBytes = cleanResult.CleanedBytes;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lecture/nettoyage de l'image échoué :\n{ex.Message}",
                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 4. Convertir en JPEG si nécessaire (Shopmium attend image/jpeg)
            byte[] jpegBytes = cleanBytes;
            try
            {
                using var ms = new MemoryStream(cleanBytes);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    ms,
                    System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                {
                    var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(decoder.Frames[0]));
                    using var outMs = new MemoryStream();
                    enc.Save(outMs);
                    jpegBytes = outMs.ToArray();
                }
            }
            catch
            {
                // Si la conversion échoue, on essaie d'envoyer tel quel
            }

            // 5. Upload via le service Shopmium
            BtnRefreshSubmissions.IsEnabled = false;
            SetStatus("working", $"Envoi de la preuve pour {offerName}…");

            try
            {
                // Le purpose officiel pour une demande "PHOTO DEMANDÉE" est "product"
                // (photo du produit acheté, de face et de dos avec code-barre).
                var (ok, error, reference) = await _apiService.Upload.AddProofToSubmissionAsync(
                    sub.Id,
                    jpegBytes,
                    "product");

                if (ok)
                {
                    SetStatus("ok", $"✓ Preuve envoyée avec succès (réf : {reference})");
                    MessageBox.Show(
                        $"✓ La preuve a été envoyée à Shopmium.\n\n" +
                        $"Référence S3 : {reference}\n\n" +
                        $"Shopmium va re-traiter votre demande.",
                        "Succès", MessageBoxButton.OK, MessageBoxImage.Information);

                    // Recharger la liste pour voir le nouveau statut
                    await LoadSubmissionsAsync();
                }
                else
                {
                    SetStatus("err", $"⚠ Échec d'envoi de la preuve");
                    MessageBox.Show(
                        $"⚠ Échec de l'envoi de la preuve :\n\n{error}",
                        "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                SetStatus("err", $"⚠ Exception : {ex.Message}");
                MessageBox.Show($"Exception inattendue :\n{ex.Message}",
                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRefreshSubmissions.IsEnabled = true;
            }
        }

        /// <summary>Charge la liste des soumissions et remplit l'onglet "Mes soumissions".</summary>
        private async Task LoadSubmissionsAsync()
        {
            if (!_apiService.IsConnected)
            {
                if (SubEmptyText != null)
                {
                    SubEmptyText.Text = "⚠ Vous devez être connecté à Shopmium (onglet Offres Shopmium).";
                    SubEmptyText.Visibility = Visibility.Visible;
                }
                if (SubmissionsList != null) SubmissionsList.ItemsSource = null;
                return;
            }

            try
            {
                if (BtnRefreshSubmissions != null) BtnRefreshSubmissions.IsEnabled = false;
                if (SubEmptyText != null)
                {
                    SubEmptyText.Text = "Chargement en cours…";
                    SubEmptyText.Visibility = Visibility.Visible;
                }

                var resp = await _apiService.GetSubmissionsAsync();

                if (resp == null)
                {
                    if (SubEmptyText != null)
                    {
                        SubEmptyText.Text = "⚠ Impossible de charger les soumissions.";
                        SubEmptyText.Visibility = Visibility.Visible;
                    }
                    if (SubmissionsList != null) SubmissionsList.ItemsSource = null;
                    return;
                }

                // Dashboard
                if (SubTotalRefunded != null)
                {
                    var formatted = resp.Dashboard?.TotalRebatedAmountFormatted
                                    ?? (resp.Dashboard?.TotalRebatedAmount.ToString("0.00") + " €");
                    SubTotalRefunded.Text = $"Total remboursé : {formatted}";
                }
                if (SubTotalCount != null)
                {
                    var nb = resp.SubmissionsCount;
                    var coupons = resp.Dashboard?.CouponsCount ?? 0;
                    SubTotalCount.Text = $"{nb} soumission(s)" + (coupons > 0 ? $"  •  {coupons} remboursement(s) effectué(s)" : "");
                }

                // Liste
                if (resp.Submissions.Count == 0)
                {
                    if (SubEmptyText != null)
                    {
                        SubEmptyText.Text = "Aucune soumission pour le moment.";
                        SubEmptyText.Visibility = Visibility.Visible;
                    }
                    if (SubmissionsList != null) SubmissionsList.ItemsSource = null;
                }
                else
                {
                    if (SubEmptyText != null) SubEmptyText.Visibility = Visibility.Collapsed;
                    // Trier par date de soumission décroissante (la + récente en haut)
                    var sorted = resp.Submissions
                        .OrderByDescending(s => s.SubmittedAt ?? s.CreatedAt ?? "")
                        .ToList();
                    if (SubmissionsList != null) SubmissionsList.ItemsSource = sorted;

                    // Précharger les images en arrière-plan
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var urls = sorted
                                .Select(s => s.MainCoupon?.OfferImageUrl)
                                .Where(u => !string.IsNullOrEmpty(u))
                                .Distinct()
                                .Cast<string>()
                                .ToList();
                            await UrlToImageConverter.PreloadAsync(urls);
                            Dispatcher.Invoke(() =>
                            {
                                if (SubmissionsList != null) SubmissionsList.Items.Refresh();
                            });
                        }
                        catch { }
                    });
                }

                _submissionsLoaded = true;
            }
            catch (Exception ex)
            {
                if (SubEmptyText != null)
                {
                    SubEmptyText.Text = $"⚠ Erreur : {ex.Message}";
                    SubEmptyText.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                if (BtnRefreshSubmissions != null) BtnRefreshSubmissions.IsEnabled = true;
            }
        }

        private async void BtnApiLoadOffers_Click(object sender, RoutedEventArgs e)
        {
            if (!_apiService.IsConnected) return;

            BtnApiLoadOffers.IsEnabled = false;
            ApiOfferList.Items.Clear();
            _allLoadedOffers.Clear();
            SetStatus("working", "Chargement des offres Shopmium…");

            var (offers, error) = await _apiService.GetOffersAsync();

            if (!string.IsNullOrEmpty(error))
            {
                SetStatus("err", $"Erreur offres : {error}");
                BtnApiLoadOffers.IsEnabled = true;
                return;
            }

            // Stocker la liste complète + trier + remplir la ListBox
            _allLoadedOffers = offers.OrderBy(o => o.Name).ToList();
            foreach (var o in _allLoadedOffers) ApiOfferList.Items.Add(o);

            if (OfferCountLabel != null)
                OfferCountLabel.Text = $"{_allLoadedOffers.Count} offre(s) affichée(s)";

            SetStatus("ok", $"{_allLoadedOffers.Count} offre(s) chargée(s) — préchargement des images…");

            // ─── Précharger toutes les images en arrière-plan ────────────────
            _ = Task.Run(async () =>
            {
                try
                {
                    var urls = _allLoadedOffers
                        .Select(o => o.EffectiveImageUrl)
                        .Where(u => !string.IsNullOrWhiteSpace(u))
                        .Distinct()
                        .ToList();

                    await UrlToImageConverter.PreloadAsync(urls);

                    Dispatcher.Invoke(() =>
                    {
                        ApiOfferList.Items.Refresh();
                        SetStatus("ok", $"{offers.Count} offre(s) chargée(s)");
                    });
                }
                catch { }
            });

            BtnApiLoadOffers.IsEnabled = true;
        }

        /// <summary>
        /// Sélection d'une offre dans la liste API → déclenche le pipeline COMPLET v112
        /// (TVA multi-source, recherche image, panneau preuves, bannière TVA, etc.).
        ///
        /// Au lieu de juste afficher 3 lignes de résumé comme avant, on convertit
        /// le ShopmiumOffer en ProductData via OfferAnalyzer puis on appelle
        /// EXACTEMENT le même code que l'ancien BtnParseProduct_Click.
        /// </summary>
        private async void ApiOfferList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ApiOfferList.SelectedItem is not ShopmiumOffer offer)
            {
                ApiOfferDetail.Visibility = Visibility.Collapsed;
                _selectedApiOffer = null;
                UpdateApiSubmitButton();
                return;
            }

            _selectedApiOffer = offer;

            try
            {
                SetStatus("working", $"Analyse de l'offre : {offer.Name}…");

                // ─── Récupérer les détails complets si possible (depuis API) ──────
                var detailed = await _apiService.GetOfferDetailAsync(offer.Id);
                if (detailed != null) _selectedApiOffer = detailed;

                // ─── URL réelle de l'offre (pour les prompts IA, etc.) ─────────
                // L'API renvoie url_web = "https://offers.shopmium.com/fr/n/<slug>"
                _selectedProductUrl = !string.IsNullOrEmpty(_selectedApiOffer.UrlWeb)
                    ? _selectedApiOffer.UrlWeb
                    : "https://offers.shopmium.com/fr";

                // ─── Convertir l'offre API → ProductData via OfferAnalyzer ────────
                _extractedData = OfferAnalyzer.Analyze(_selectedApiOffer);
                _refChosen = false; // Réinitialiser à chaque nouveau produit

                // ─── Affichage initial dans le panneau extrait (comme onglet Auto v112) ─
                ExtractedPanel.Visibility = Visibility.Visible;
                if (RefChosenBar != null) RefChosenBar.Visibility = Visibility.Collapsed;
                ExtProduit.Text   = _extractedData.ProductName;
                ExtArticles.Text  = $"{_extractedData.MaxArticles} article(s)";
                ExtPrix.Text      = $"{_extractedData.MaxPrice:F2} €";
                ExtTotal.Text     = $"{_extractedData.TotalTTC:F2} €";
                var rayonInfo = string.IsNullOrEmpty(_extractedData.RayonText)
                    ? "" : $" ({_extractedData.RayonText})";
                ExtTva.Text = $"{TvaCalculator.Format(_extractedData.TvaRate)} — {_extractedData.TvaAmount:F2} €{rayonInfo}";
                ExtDate.Text      = _extractedData.StartDate;
                if (BtnGenerateAuto != null)
                    BtnGenerateAuto.IsEnabled = true;

                // Affichage du type de ticket détecté
                UpdateTicketTypeDisplay(_extractedData);

                // Affichage du panneau de preuve si requis
                UpdateProofPanel(_extractedData);

                // ─── Affichage conditionnel du panneau EXIF ───────────────────
                // Le panneau de nettoyage EXIF n'apparaît que si l'offre exige une
                // photo du produit (qui sera généralement générée via Gemini).
                if (ExifPanel != null)
                {
                    ExifPanel.Visibility = _selectedApiOffer.RequiresProductPhoto
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                    // Réinitialiser l'état du panneau pour chaque nouvelle offre
                    if (_selectedApiOffer.RequiresProductPhoto)
                    {
                        _exifSourceBytes = null;
                        _exifCleanedBytes = null;
                        _exifSourceName = null;
                        if (ExifImportedLabel != null) ExifImportedLabel.Visibility = Visibility.Collapsed;
                        if (ExifVerifyPanel != null)   ExifVerifyPanel.Visibility = Visibility.Collapsed;
                        if (BtnExifClean != null)  BtnExifClean.IsEnabled = false;
                        if (BtnExifVerify != null) BtnExifVerify.IsEnabled = false;
                        if (BtnExifSave != null)   BtnExifSave.IsEnabled = false;
                    }
                }

                // Bannière orange si TVA incertaine
                UpdateTvaWarning(_extractedData);

                // ─── Détails de l'offre API ──────────────────────────────────────
                ApiOfferName.Text = $"{offer.Name}" +
                    (_extractedData.MaxPrice > 0 ? $" — {_extractedData.MaxPrice:F2}€" : "");
                var barcodes = _selectedApiOffer.Products
                    .Where(p => !string.IsNullOrEmpty(p.Ean))
                    .Select(p => p.Ean!).Distinct().ToList();
                if (barcodes.Count == 0)
                    ApiOfferBarcode.Text = "📦 Aucun code-barres disponible";
                else if (barcodes.Count == 1)
                    ApiOfferBarcode.Text = $"📦 EAN : {barcodes[0]}";
                else
                    ApiOfferBarcode.Text = $"📦 EAN ({barcodes.Count} disponibles) — sélectionné : {_extractedData.BarcodeEan ?? barcodes[0]}";

                var retailers = _selectedApiOffer.Retailers?.Select(r => r.Name).ToList();
                ApiOfferRetailers.Text = retailers?.Any() == true
                    ? $"🏪 Enseignes : {string.Join(", ", retailers)}"
                    : "🏪 Toutes enseignes";
                ApiOfferDetail.Visibility = Visibility.Visible;

                // ─── Pré-remplir TbEanCode automatiquement (depuis l'API) ──────
                if (TbEanCode != null)
                {
                    TbEanCode.Text = _extractedData.BarcodeEan ?? "";
                }
                // Masquer l'aperçu du code-barres précédent (s'il y en avait un)
                if (EanBarcodePanel != null)
                {
                    EanBarcodePanel.Visibility = Visibility.Collapsed;
                    EanBarcodeImage.Source = null;
                }
                if (BarcodeHelpText != null) BarcodeHelpText.Visibility = Visibility.Collapsed;

                SetStatus("ok", "Données extraites — analyses en cours...");

                // ─── Lancer en parallèle : TVA + image (comme v112 onglet Auto) ───
                // L'API nous donne déjà l'URL de l'image, mais on lance quand même
                // la recherche multi-source pour le matching et le fallback.
                var apiHtml = BuildPseudoHtmlFromOffer(_selectedApiOffer);
                var tasks = new List<Task>
                {
                    RefreshTvaRateAsync(_extractedData),
                    FetchProductImageFromApiAsync(_extractedData, _selectedApiOffer, apiHtml),
                };
                await Task.WhenAll(tasks);

                UpdateApiSubmitButton();
            }
            catch (Exception ex)
            {
                ShowError($"Erreur analyse offre : {ex.Message}");
                SetStatus("err", "Erreur d'analyse");
            }
        }

        /// <summary>
        /// Construit un pseudo-HTML à partir d'un ShopmiumOffer pour pouvoir
        /// réutiliser FetchProductImageAsync (qui attend du HTML en entrée).
        /// </summary>
        private static string BuildPseudoHtmlFromOffer(ShopmiumOffer offer)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<html><body>");

            // Image principale
            if (!string.IsNullOrEmpty(offer.EffectiveImageUrl))
                sb.Append($"<img src=\"{offer.EffectiveImageUrl}\" alt=\"{offer.Name}\" />");

            // Logo customer brand
            foreach (var b in offer.CustomerBrands ?? new())
            {
                if (!string.IsNullOrEmpty(b.OfferDetailLogoImageUrl))
                    sb.Append($"<img src=\"{b.OfferDetailLogoImageUrl}\" alt=\"{b.Name} logo\" />");
                if (!string.IsNullOrEmpty(b.OfferListLogoImageUrl))
                    sb.Append($"<img src=\"{b.OfferListLogoImageUrl}\" alt=\"{b.Name}\" />");
            }

            // Conditions / description
            var content = offer.Presentation?.Detail?.Conditions?.Content
                       ?? offer.EffectiveDescription
                       ?? "";
            sb.Append($"<div>{System.Net.WebUtility.HtmlEncode(content)}</div>");

            sb.Append("</body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// Variante de FetchProductImageAsync v112 adaptée pour l'API :
        /// privilégie l'URL fournie par l'API (product_image_url), avec fallback
        /// sur ProductImageService comme en v112 si l'image API n'est pas dispo.
        /// </summary>
        private async Task FetchProductImageFromApiAsync(ProductData data, ShopmiumOffer offer, string pseudoHtml)
        {
            try
            {
                // ─── 1. Image déjà préchargée par UrlToImageConverter ? ───────
                // Le converter a un cache global ; on essaie de récupérer les bytes
                // depuis ce cache pour éviter un nouveau téléchargement.
                if (!string.IsNullOrEmpty(offer.EffectiveImageUrl))
                {
                    var cachedBytes = await UrlToImageConverter.GetCachedBytesAsync(offer.EffectiveImageUrl);
                    if (cachedBytes != null && cachedBytes.Length > 0)
                    {
                        data.BestImageData    = cachedBytes;
                        data.ProductImageData = cachedBytes;
                        data.ProductImageUrl  = offer.EffectiveImageUrl;
                        data.BestImageUrl     = offer.EffectiveImageUrl;
                        data.BestImageMatch   = true;
                        data.BestImageNote    = "Image officielle Shopmium (API)";
                    }
                }

                // 2. Fallback : ProductImageService comme en v112
                if (data.ProductImageData == null && !string.IsNullOrEmpty(pseudoHtml))
                {
                    var progress = new Progress<string>(msg =>
                        Dispatcher.Invoke(() => SetStatus("working", msg)));
                    var result = await ProductImageService.FetchBestImageAsync(
                        pseudoHtml, data.ProductName, progress);
                    if (result.Success)
                    {
                        data.ProductImageData = result.ImageData;
                        data.ProductImageUrl  = result.ImageUrl;
                        data.BestImageData    = result.ImageData;
                    }
                }

                // 3. Régénérer le panneau preuves (peut afficher l'image maintenant)
                Dispatcher.Invoke(() =>
                {
                    if (_extractedData?.ProofRequirements.Count > 0)
                        UpdateProofPanel(_extractedData);
                    else if (_extractedData?.NeedsEanSearch == true)
                    {
                        if (EanPanel != null) EanPanel.Visibility = Visibility.Visible;
                    }
                });
            }
            catch (Exception ex)
            {
                SetStatus("ok", "Image produit : erreur (" + ex.Message + ")");
            }
        }

        /// <summary>Sélection de l'image à soumettre.</summary>
        private void BtnApiPickImage_Click(object sender, RoutedEventArgs e)
        {
            // Utiliser d'abord le PNG généré s'il existe
            if (!string.IsNullOrEmpty(_lastPngPath) && File.Exists(_lastPngPath))
            {
                _apiSelectedImagePath = _lastPngPath;
                ApiImagePath.Text     = Path.GetFileName(_lastPngPath);
                ApiImagePath.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xA0));
                UpdateApiSubmitButton();
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Sélectionner l'image du ticket",
                Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Tous les fichiers|*.*",
            };

            if (dlg.ShowDialog() == true)
            {
                _apiSelectedImagePath = dlg.FileName;
                ApiImagePath.Text     = Path.GetFileName(dlg.FileName);
                ApiImagePath.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xA0));
                UpdateApiSubmitButton();
            }
        }

        /// <summary>Soumet le ticket à Shopmium.</summary>
        private async void BtnApiSubmit_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApiOffer == null || string.IsNullOrEmpty(_apiSelectedImagePath))
                return;

            BtnApiSubmit.IsEnabled   = false;
            ApiSubmitStatus.Text     = "";

            try
            {
                var progress = new Progress<string>(msg =>
                {
                    ApiSubmitStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x00, 0xAA, 0xFF));
                    ApiSubmitStatus.Text = msg;
                    SetStatus("working", msg);
                });

                // ── 1. Upload de l'image ───────────────────────────────────
                var (s3Url, uploadError) = await _apiService.UploadImageAsync(
                    _apiSelectedImagePath, progress);

                if (s3Url == null)
                {
                    ShowApiError($"Erreur upload : {uploadError}");
                    return;
                }

                // ── 2. Soumission ──────────────────────────────────────────
                var chain = string.IsNullOrWhiteSpace(ApiChain.Text)
                    ? null : ApiChain.Text.Trim().ToUpper();

                var (success, message, subId) = await _apiService.SubmitTicketAsync(
                    _selectedApiOffer, s3Url, chain, progress);

                if (success)
                {
                    ApiSubmitStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xA0));
                    ApiSubmitStatus.Text = $"✅ {message}";
                    SetStatus("ok", message);
                }
                else
                {
                    ShowApiError(message);
                }
            }
            catch (Exception ex)
            {
                ShowApiError(ex.Message);
            }
            finally
            {
                BtnApiSubmit.IsEnabled = true;
            }
        }

        private void ShowApiError(string message)
        {
            ApiSubmitStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44));
            ApiSubmitStatus.Text = $"✗ {message}";
            SetStatus("err", message);
        }

        private void UpdateApiSubmitButton()
        {
            if (BtnApiSubmit != null)
                BtnApiSubmit.IsEnabled = _apiService.IsConnected
                    && _selectedApiOffer != null
                    && !string.IsNullOrEmpty(_apiSelectedImagePath);

            // Bouton "📤 Envoyer à Shopmium" : activé dès qu'une offre est sélectionnée
            // et qu'on est connecté à l'API. L'image sera demandée dans la fenêtre dédiée.
            if (BtnSubmitToShopmium != null)
                BtnSubmitToShopmium.IsEnabled = _apiService.IsConnected
                    && _selectedApiOffer != null;

            // Auto-remplir l'image si un PNG a été généré
            if (!string.IsNullOrEmpty(_lastPngPath) && File.Exists(_lastPngPath)
                && string.IsNullOrEmpty(_apiSelectedImagePath))
            {
                _apiSelectedImagePath   = _lastPngPath;
                if (ApiImagePath != null)
                {
                    ApiImagePath.Text       = Path.GetFileName(_lastPngPath);
                    ApiImagePath.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xA0));
                }
            }
        }

        /// <summary>
        /// Handler du bouton "📤 ENVOYER À SHOPMIUM" — ouvre la fenêtre dédiée
        /// qui auto-détecte les preuves requises et orchestre l'upload.
        /// </summary>
        private void BtnSubmitToShopmium_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedApiOffer == null)
            {
                MessageBox.Show("Aucune offre sélectionnée.", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_apiService.IsConnected)
            {
                MessageBox.Show("Vous n'êtes pas connecté à l'API Shopmium.", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Récupérer le ticket type actuellement sélectionné pour synchroniser la chain
            var currentTicketType = _extractedData?.TicketType
                ?? ShopmiumPdfAutomator.Models.TicketType.Standard;

            var dlg = new SubmitToShopmiumWindow(_apiService, _selectedApiOffer, currentTicketType)
            {
                Owner = this
            };
            var result = dlg.ShowDialog();

            if (result == true)
            {
                // Soumission réussie : on rafraîchit la liste des "Mes soumissions"
                // pour que l'utilisateur voie sa nouvelle entrée.
                _ = LoadSubmissionsAsync();
            }
        }

    }
}