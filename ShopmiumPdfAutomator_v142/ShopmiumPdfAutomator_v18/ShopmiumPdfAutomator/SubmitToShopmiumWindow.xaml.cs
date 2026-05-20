using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShopmiumPdfAutomator.Models;
using ShopmiumPdfAutomator.Services;

namespace ShopmiumPdfAutomator
{
    public partial class SubmitToShopmiumWindow : Window
    {
        private const double LOC_LAT      = 48.866;
        private const double LOC_LNG      = 2.348;
        private const int    LOC_ACCURACY = 10;

        private static readonly Dictionary<TicketType, string> ChainMap = new()
        {
            { TicketType.Standard,       "CARREFOUR"       },
            { TicketType.CarrefourDrive, "CARREFOUR_DRIVE" },
            { TicketType.Leclerc,        "LECLERC"         },
        };

        private readonly ShopmiumApiService _api;
        private readonly ShopmiumOffer      _offer;
        private readonly TicketType         _ticketType;

        private readonly Dictionary<string, int>       _quantities  = new();
        private readonly Dictionary<string, TextBlock> _qtyLabels   = new();
        private readonly Dictionary<string, string>    _proofFiles  = new();
        private readonly Dictionary<string, TextBlock> _proofLabels = new();

        private string ChainValue => ChainMap.TryGetValue(_ticketType, out var v) ? v : "CARREFOUR";

        private int TotalQuantity
        {
            get { int n = 0; foreach (var q in _quantities.Values) n += q; return n; }
        }

        public SubmitToShopmiumWindow(ShopmiumApiService api, ShopmiumOffer offer, TicketType ticketType)
        {
            InitializeComponent();
            _api        = api;
            _offer      = offer;
            _ticketType = ticketType;
            BuildUi();
        }

        private void BuildUi()
        {
            OfferTitleText.Text = _offer.Name ?? "(offre sans nom)";
            OfferMetaText.Text  = $"ID offre : {_offer.Id}";
            ChainValueText.Text = ChainValue;

            var products = _offer.Products ?? new List<ShopmiumProduct>();
            int itemsMin = _offer.Submission?.ProductSelection?.ItemsMin ?? 1;
            int itemsMax = _offer.Submission?.ProductSelection?.ItemsMax ?? 1;

            ProductsHintText.Text = itemsMin == itemsMax
                ? $"Exactement {itemsMin} article{(itemsMin > 1 ? "s" : "")} requis."
                : $"Entre {itemsMin} et {itemsMax} articles.";

            if (products.Count == 0)
            {
                ProductsList.Items.Add(new TextBlock
                {
                    Text       = "⚠ Aucun produit trouvé dans cette offre.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x44)),
                    FontSize   = 10,
                    Margin     = new Thickness(0, 0, 0, 8)
                });
            }
            else
            {
                foreach (var prod in products)
                {
                    var key = prod.Barcode ?? prod.Id.ToString();
                    _quantities[key] = 0;
                    ProductsList.Items.Add(BuildProductRow(prod, key, itemsMax));
                }
            }

            RefreshTotal(itemsMin, itemsMax);

            var required = _offer.RequiredProofPurposes;
            if (required.Count == 0)
                required = new List<(string, string)> { ("receipt", "Ticket de caisse") };

            bool hasReceipt = false;
            foreach (var p in required)
                if (string.Equals(p.Purpose, "receipt", StringComparison.OrdinalIgnoreCase))
                    { hasReceipt = true; break; }
            if (!hasReceipt) required.Insert(0, ("receipt", "Ticket de caisse"));

            foreach (var (purpose, label) in required)
                ProofsList.Items.Add(BuildProofRow(purpose, label));
        }

        private Border BuildProductRow(ShopmiumProduct prod, string key, int itemsMax)
        {
            var border = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(0x08, 0x10, 0x18)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1A, 0x30, 0x50)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(5),
                Padding         = new Thickness(10, 8, 10, 8),
                Margin          = new Thickness(0, 0, 0, 6)
            };

            var outerGrid = new Grid();
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ── Miniature produit ──
            var img = new System.Windows.Controls.Image
            {
                Width             = 46,
                Height            = 46,
                Stretch           = System.Windows.Media.Stretch.Uniform,
                Margin            = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!string.IsNullOrEmpty(prod.ImageUrl))
                try { img.Source = new System.Windows.Media.Imaging.BitmapImage(
                          new Uri(prod.ImageUrl, UriKind.Absolute)); }
                catch { }
            Grid.SetColumn(img, 0);
            outerGrid.Children.Add(img);

            // ── Nom + EAN ──
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text         = TruncateName(prod.Name ?? prod.Barcode ?? "Produit"),
                Foreground   = new SolidColorBrush(Color.FromRgb(0xC0, 0xE0, 0xFF)),
                FontSize     = 11,
                FontWeight   = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            info.Children.Add(new TextBlock
            {
                Text       = $"EAN : {prod.Barcode ?? "-"}",
                Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x58, 0x68)),
                FontSize   = 9,
                Margin     = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(info, 1);
            outerGrid.Children.Add(info);

            var spinner = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(10, 0, 0, 0)
            };

            var btnMinus = new Button { Content = "−", Style = (Style)FindResource("SpinBtn"), Tag = key };
            var qtyLabel = new TextBlock
            {
                Text              = "0",
                Foreground        = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xFF)),
                FontSize          = 14,
                FontWeight        = FontWeights.Bold,
                MinWidth          = 28,
                TextAlignment     = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 0, 4, 0)
            };
            var btnPlus = new Button
            {
                Content = "+",
                Style   = (Style)FindResource("SpinBtn"),
                Tag     = new Tuple<string, int>(key, itemsMax)
            };

            _qtyLabels[key] = qtyLabel;
            btnMinus.Click += BtnMinus_Click;
            btnPlus.Click  += BtnPlus_Click;

            spinner.Children.Add(btnMinus);
            spinner.Children.Add(qtyLabel);
            spinner.Children.Add(btnPlus);
            Grid.SetColumn(spinner, 2);
            outerGrid.Children.Add(spinner);

            border.Child = outerGrid;
            return border;
        }

        private Border BuildProofRow(string purpose, string label)
        {
            var border = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(0x08, 0x10, 0x18)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1A, 0x30, 0x50)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(5),
                Padding         = new Thickness(12, 9, 12, 9),
                Margin          = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text       = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xE0, 0xFF)),
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold
            });
            var pathLbl = new TextBlock
            {
                Text         = "Aucun fichier sélectionné",
                Foreground   = new SolidColorBrush(Color.FromRgb(0x50, 0x68, 0x78)),
                FontSize     = 9,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            info.Children.Add(pathLbl);
            _proofLabels[purpose] = pathLbl;
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var btn = new Button
            {
                Content           = "📁  Choisir...",
                Style             = (Style)FindResource("DarkBtn"),
                Tag               = purpose,
                MinWidth          = 110,
                Margin            = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Click += BtnChooseProof_Click;
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            border.Child = grid;
            return border;
        }

        private void BtnMinus_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string key) return;
            if (_quantities.TryGetValue(key, out var v) && v > 0)
            {
                _quantities[key] = v - 1;
                if (_qtyLabels.TryGetValue(key, out var lbl)) lbl.Text = _quantities[key].ToString();
            }
            int min = _offer.Submission?.ProductSelection?.ItemsMin ?? 1;
            int max = _offer.Submission?.ProductSelection?.ItemsMax ?? 1;
            RefreshTotal(min, max);
        }

        private void BtnPlus_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string key; int maxQty;
            if      (btn.Tag is Tuple<string, int> t) { key = t.Item1; maxQty = t.Item2; }
            else if (btn.Tag is string s)              { key = s;       maxQty = 99;      }
            else return;

            int itemsMax = _offer.Submission?.ProductSelection?.ItemsMax ?? 99;
            if (TotalQuantity >= itemsMax) return;

            if (!_quantities.ContainsKey(key)) _quantities[key] = 0;
            if (_quantities[key] < maxQty)
            {
                _quantities[key]++;
                if (_qtyLabels.TryGetValue(key, out var lbl)) lbl.Text = _quantities[key].ToString();
            }
            int itemsMin = _offer.Submission?.ProductSelection?.ItemsMin ?? 1;
            RefreshTotal(itemsMin, itemsMax);
        }

        private void RefreshTotal(int itemsMin, int itemsMax)
        {
            int total = TotalQuantity;
            bool valid = total >= itemsMin && total <= itemsMax;
            string hint = valid          ? "  ✓" :
                          total < itemsMin ? $"  (minimum : {itemsMin})" :
                                             $"  (maximum : {itemsMax})";
            TotalText.Text       = $"Total déclaré : {total} article{(total > 1 ? "s" : "")}" + hint;
            TotalText.Foreground = new SolidColorBrush(valid
                ? Color.FromRgb(0x5A, 0xB8, 0x78)
                : Color.FromRgb(0xFF, 0x99, 0x33));
        }

        private void BtnChooseProof_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string purpose) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = $"Photo pour : {purpose}",
                Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|Tous (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            _proofFiles[purpose] = dlg.FileName;
            if (_proofLabels.TryGetValue(purpose, out var lbl))
            {
                lbl.Text       = Path.GetFileName(dlg.FileName);
                lbl.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xB8, 0x78));
                lbl.FontStyle  = FontStyles.Normal;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private async void BtnSubmit_Click(object sender, RoutedEventArgs e)
        {
            int itemsMin = _offer.Submission?.ProductSelection?.ItemsMin ?? 1;
            int itemsMax = _offer.Submission?.ProductSelection?.ItemsMax ?? 99;
            int total    = TotalQuantity;

            if (total < itemsMin) { SetError($"⚠ Minimum {itemsMin} article(s) requis (actuel : {total})."); return; }
            if (total > itemsMax) { SetError($"⚠ Maximum {itemsMax} articles (actuel : {total}).");          return; }
            if (!_proofFiles.ContainsKey("receipt")) { SetError("⚠ Le ticket de caisse est obligatoire."); return; }

            // Construire la liste produits
            var productsList = new List<ShopmiumUploadService.ProductWithQuantity>();
            foreach (var prod in (_offer.Products ?? new List<ShopmiumProduct>()))
            {
                var key = prod.Barcode ?? prod.Id.ToString();
                if (_quantities.TryGetValue(key, out var qty) && qty > 0)
                    productsList.Add(new ShopmiumUploadService.ProductWithQuantity(
                        prod.Id > 0 ? prod.Id : (long?)null,
                        prod.Barcode ?? "",
                        qty));
            }

            // Construire les preuves
            var receiptProofs    = new List<ShopmiumUploadService.ProofToUpload>();
            var additionalProofs = new List<ShopmiumUploadService.ProofToUpload>();
            foreach (var kv in _proofFiles)
            {
                byte[] bytes;
                try   { bytes = ReadAndConvertToJpeg(kv.Value); }
                catch (Exception ex) { SetError($"⚠ Lecture {Path.GetFileName(kv.Value)} : {ex.Message}"); return; }
                var proof = new ShopmiumUploadService.ProofToUpload(bytes, kv.Key);
                if (string.Equals(kv.Key, "receipt", StringComparison.OrdinalIgnoreCase))
                    receiptProofs.Add(proof);
                else
                    additionalProofs.Add(proof);
            }

            BtnSubmit.IsEnabled = false;
            SetStatus("Envoi en cours…");

            try
            {
                var (ok, error, newId) = await _api.Upload.CreateSubmissionAsync(
                    offerId:          _offer.Id,
                    chain:            ChainValue,
                    products:         productsList,
                    receiptProofs:    receiptProofs,
                    additionalProofs: additionalProofs,
                    location:         (LOC_LAT, LOC_LNG, LOC_ACCURACY));

                if (ok)
                {
                    SetStatus($"✓ Soumission créée — ID : {newId?.ToString() ?? "?"}", Color.FromRgb(0x5A, 0xB8, 0x78));
                    MessageBox.Show(
                        $"✓ La demande a été envoyée à Shopmium.\n\n" +
                        (newId.HasValue ? $"ID soumission : {newId}\n\n" : "") +
                        "Suivez le statut dans l'onglet « Mes soumissions ».",
                        "Succès", MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else { SetError($"⚠ {error}"); }
            }
            catch (Exception ex) { SetError($"⚠ Exception : {ex.Message}"); }
            finally { BtnSubmit.IsEnabled = true; }
        }

        private static byte[] ReadAndConvertToJpeg(string path)
        {
            var raw     = File.ReadAllBytes(path);
            var cleaned = ExifCleanerService.Clean(raw).CleanedBytes ?? raw;
            try
            {
                using var ms  = new MemoryStream(cleaned);
                var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(ms,
                    System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                if (dec.Frames.Count > 0)
                {
                    var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(dec.Frames[0]));
                    using var outMs = new MemoryStream();
                    enc.Save(outMs);
                    return outMs.ToArray();
                }
            }
            catch { }
            return cleaned;
        }

        private static string TruncateName(string s) => s.Length > 55 ? s[..52] + "…" : s;

        private void SetStatus(string msg, Color? color = null)
        {
            StatusText.Text       = msg;
            StatusText.Foreground = new SolidColorBrush(color ?? Color.FromRgb(0x7A, 0x88, 0x98));
        }

        private void SetError(string msg)
        {
            StatusText.Text       = msg;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
        }
    }
}
