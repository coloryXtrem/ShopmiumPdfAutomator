using System.Windows;
using ShopmiumPdfAutomator.Services;

namespace ShopmiumPdfAutomator
{
    /// <summary>
    /// Popup affiché quand la mise à jour est téléchargée et prête.
    /// Le téléchargement a déjà eu lieu en arrière-plan.
    /// L'utilisateur n'a qu'à cliquer "Redémarrer l'app".
    /// </summary>
    public partial class UpdateWindow : Window
    {
        private readonly UpdateService.UpdateInfo _info;

        public UpdateWindow(UpdateService.UpdateInfo info)
        {
            InitializeComponent();
            _info = info;

            LblVersions.Text = $"v{UpdateService.CurrentVersion}  →  v{info.Version}  " +
                               $"(déjà téléchargé, prêt à installer)";

            LblNotes.Text = string.IsNullOrWhiteSpace(info.Notes)
                ? "Améliorations et corrections de bugs."
                : info.Notes.Replace("\\n", "\n");

            // Mise à jour obligatoire → cacher "Plus tard" et "✕"
            if (info.Required)
            {
                BtnSkip.Visibility  = Visibility.Collapsed;
                BtnClose.Visibility = Visibility.Collapsed;
            }
        }

        // ── "Redémarrer l'app" ────────────────────────────────────────────
        private void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            // Lance le script de remplacement + redémarre
            UpdateService.InstallAndRestart();
        }

        // ── "Plus tard" ───────────────────────────────────────────────────
        private void BtnSkip_Click(object sender, RoutedEventArgs e)
            => Close();

        // ── ✕ ────────────────────────────────────────────────────────────
        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();

        // ── Drag ─────────────────────────────────────────────────────────
        protected override void OnMouseLeftButtonDown(
            System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            DragMove();
        }
    }
}
