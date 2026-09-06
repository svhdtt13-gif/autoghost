namespace AutoGhost.App;

public partial class ClientDialog : System.Windows.Window
{
    public ClientDialog()
    {
        InitializeComponent();
        ClientIdBox.Focus();
    }

    public string ClientId => ClientIdBox.Text.Trim();

    public string SpecialItems => SpecialItemsBox.Text;

    private void OnSave(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ClientId.Length == 0)
        {
            System.Windows.MessageBox.Show(
                "Enter a non-empty Client ID.",
                "Invalid Client ID",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
