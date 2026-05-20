using ShopmiumPdfAutomator.Services;
using System.Windows;

namespace ShopmiumPdfAutomator
{
    public partial class ActivationWindow : Window
    {
        public new bool Activated { get; private set; }

        public ActivationWindow(bool expired = false)
        {
            InitializeComponent();
            KeyBox.Focus();
            if (expired)
            {
                Title = "Licence expirée — Renouvellement";
                if (StatusText != null)
                {
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    StatusText.Text       = "Votre licence a expiré. Entrez votre nouvelle clé pour continuer.";
                }
            }
        }

        private void ActivateBtn_Click(object sender, RoutedEventArgs e)
        {
            var key = KeyBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                StatusText.Text = "Veuillez saisir votre clé de licence.";
                return;
            }

            ActivateBtn.IsEnabled = false;
            StatusText.Foreground = System.Windows.Media.Brushes.Gray;
            StatusText.Text = "Vérification...";

            var (success, message) = LicenseService.Activate(key);

            if (success)
            {
                Activated = true;

                if (message.Contains("déjà été utilisée sur cet appareil"))
                {
                    MessageBox.Show(message,
                        "Accès restauré", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"✓ {message}\n\nMerci pour votre achat !",
                        "Activation réussie", MessageBoxButton.OK, MessageBoxImage.None);
                }

                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                StatusText.Text = $"✗ {message}";
                ActivateBtn.IsEnabled = true;
            }
        }
    }
}
