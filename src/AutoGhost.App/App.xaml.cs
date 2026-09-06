using AutoGhost.App.Services;

namespace AutoGhost.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        WpfStartupDiagnostics.InstallExceptionHooks(this);
        WpfStartupDiagnostics.LogStage("OnStartup.enter", $"args={e.Args.Length}");
        base.OnStartup(e);

        try
        {
            WpfStartupDiagnostics.LogStage("MainWindow.ctor.begin");
            var mainWindow = new MainWindow();
            WpfStartupDiagnostics.LogStage("MainWindow.ctor.complete");
            MainWindow = mainWindow;
            mainWindow.Show();
            WpfStartupDiagnostics.LogWindowState("MainWindow.Show.return", mainWindow);
        }
        catch (Exception exception)
        {
            WpfStartupDiagnostics.LogException("OnStartup.exception", exception);
            throw;
        }
    }
}
