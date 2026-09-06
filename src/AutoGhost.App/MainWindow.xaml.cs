using AutoGhost.App.ViewModels;
using AutoGhost.App.Services;

namespace AutoGhost.App;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow()
    {
        WpfStartupDiagnostics.LogStage("MainWindow.ctor.enter");
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        Loaded += (_, _) => WpfStartupDiagnostics.LogWindowState("MainWindow.Loaded", this);
        ContentRendered += (_, _) => WpfStartupDiagnostics.LogWindowState("MainWindow.ContentRendered", this);
        Closed += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Dispose();
            }

            WpfStartupDiagnostics.LogWindowState("MainWindow.Closed", this);
        };
        WpfStartupDiagnostics.LogStage("MainWindow.ctor.exit");
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        WpfStartupDiagnostics.LogWindowState("MainWindow.OnLoaded.handler", this);
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.RefreshRuntimeAsync();
        }
    }
}
