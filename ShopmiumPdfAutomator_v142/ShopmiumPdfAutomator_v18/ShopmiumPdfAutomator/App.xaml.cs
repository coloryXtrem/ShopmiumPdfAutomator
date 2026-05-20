using ShopmiumPdfAutomator.Services;
using System.Windows;

namespace ShopmiumPdfAutomator
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ── IMPORTANT : empêche WPF de fermer l'app automatiquement
            // quand ActivationWindow se ferme (avant que MainWindow soit ouvert)
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var status = LicenseService.Check();

            switch (status)
            {
                case LicenseService.LicenseStatus.Valid:
                    // Licence OK → lancer l'app normalement
                    ShutdownMode = ShutdownMode.OnLastWindowClose;
                    new MainWindow().Show();
                    break;

                case LicenseService.LicenseStatus.NotActivated:
                    var win = new ActivationWindow();
                    if (win.ShowDialog() == true)
                    {
                        ShutdownMode = ShutdownMode.OnLastWindowClose;
                        new MainWindow().Show();
                    }
                    else
                        Shutdown();
                    break;

                case LicenseService.LicenseStatus.Expired:
                    LicenseService.Deactivate();
                    var winExp = new ActivationWindow(expired: true);
                    if (winExp.ShowDialog() == true)
                    {
                        ShutdownMode = ShutdownMode.OnLastWindowClose;
                        new MainWindow().Show();
                    }
                    else
                        Shutdown();
                    break;

                case LicenseService.LicenseStatus.Invalid:
                    MessageBox.Show(
                        "Fichier de licence corrompu.\n\nSupprimez le fichier et réactivez l'application.",
                        "Erreur de licence", MessageBoxButton.OK, MessageBoxImage.Error);
                    LicenseService.Deactivate();
                    var win2 = new ActivationWindow();
                    if (win2.ShowDialog() == true)
                    {
                        ShutdownMode = ShutdownMode.OnLastWindowClose;
                        new MainWindow().Show();
                    }
                    else
                        Shutdown();
                    break;
            }
        }
    }
}
