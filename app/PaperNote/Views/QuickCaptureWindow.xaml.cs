using System.Windows;
using System.Windows.Input;

namespace PaperNote.Views;

// Minimal capture card, docked bottom-right like a notification. Just type — clicking away
// auto-saves. Esc or the ✕ discards. Text is handed to the host via Captured; it never
// opens the editor.
public partial class QuickCaptureWindow : Window
{
    public event Action<string>? Captured;
    private bool _closing;

    public QuickCaptureWindow()
    {
        InitializeComponent();
        Loaded += OnLoadedDock;
        Deactivated += (_, _) => Commit();   // clicking away saves, so a capture is never lost
    }

    private void OnLoadedDock(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;   // excludes the taskbar
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;
        Box.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Discard();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Commit();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnDiscard(object sender, RoutedEventArgs e) => Discard();

    // The handwritten hint sits behind the box and steps aside at the first character.
    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Hint.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Discard()
    {
        _closing = true;   // skip the save on the Deactivated that Close raises
        Close();
    }

    private void Commit()
    {
        if (_closing) return;
        _closing = true;

        var text = Box.Text.Trim();
        if (text.Length > 0) Captured?.Invoke(text);
        Close();
    }
}
