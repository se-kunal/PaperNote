using System.Windows;

namespace PaperNote.Views;

public partial class ApiKeyPrompt : Window
{
    public string ApiKey { get; private set; } = "";
    public bool UseOffline { get; private set; }

    public ApiKeyPrompt()
    {
        InitializeComponent();
        KeyBox.Loaded += (_, _) => KeyBox.Focus();
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        SaveBtn.IsEnabled = KeyBox.Password.Trim().Length > 10;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ApiKey = KeyBox.Password.Trim();
        if (ApiKey.Length <= 10) return;
        DialogResult = true;
    }

    private void OnUseOffline(object sender, RoutedEventArgs e)
    {
        UseOffline = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
