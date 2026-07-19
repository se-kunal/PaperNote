using System.Windows;
using System.Windows.Input;

namespace PaperNote.Views;

// Two modes: Setup (create the first app password, needs confirm + a loss warning) and
// Unlock (enter the existing password to view/change locked notes).
public partial class PasswordPrompt : Window
{
    private readonly bool _setup;

    public string Password { get; private set; } = "";

    public PasswordPrompt(bool setup)
    {
        InitializeComponent();
        _setup = setup;

        if (setup)
        {
            Heading.Text = "Set a lock password";
            Subhead.Text = "One password unlocks all locked notes on this device. "
                         + "If you forget it, locked notes can't be recovered — there is no reset.";
            OkBtn.Content = "Set password";
        }
        else
        {
            Heading.Text = "Unlock notes";
            Subhead.Text = "Enter your lock password.";
            ConfirmBox.Visibility = Visibility.Collapsed;
            OkBtn.Content = "Unlock";
        }

        PwdBox.Loaded += (_, _) => PwdBox.Focus();
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var hasPwd = PwdBox.Password.Length > 0;
        OkBtn.IsEnabled = _setup
            ? hasPwd && ConfirmBox.Password.Length > 0
            : hasPwd;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OkBtn.IsEnabled) OnOk(sender, e);
        if (e.Key == Key.Escape) OnCancel(sender, e);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_setup && PwdBox.Password != ConfirmBox.Password)
        {
            ErrorText.Text = "Passwords don't match.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Password = PwdBox.Password;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
