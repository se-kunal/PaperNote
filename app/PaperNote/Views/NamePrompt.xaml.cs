using System.Windows;

namespace PaperNote.Views;

public partial class NamePrompt : Window
{
    public string EnteredName { get; private set; } = "";

    public NamePrompt(string initialName = "")
    {
        InitializeComponent();
        NameBox.Text = initialName;
        NameBox.Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnNameChanged(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        SaveBtn.IsEnabled = name.Length > 0;
        Initials.Text = ComputeInitials(name);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        EnteredName = NameBox.Text.Trim();
        if (EnteredName.Length == 0) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string ComputeInitials(string name)
    {
        var parts = name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1)
            return parts[0].Length >= 2 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant();
        return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
    }
}
