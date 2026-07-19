using System.Windows;

namespace PaperNote.Views;

public partial class ShareOfferDialog : Window
{
    public ShareOfferDialog(string fromName, string title)
    {
        InitializeComponent();
        Headline.Inlines.Clear();
        Headline.Text = $"{fromName} wants to share “{title}” with you.";
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnReject(object sender, RoutedEventArgs e) => DialogResult = false;
}
