using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PaperNote.ViewModels;
using PaperNote.Views;

namespace PaperNote;

public partial class MainWindow : Window
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PaperNote", "window.json");

    private const int HOTKEY_ID = 0xB001;
    private const int HOTKEY_ID_EASY = 0xB002;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
    private const uint VK_N = 0x4E;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _focusMode;
    private GridLength _savedSidebarWidth;
    private GridLength _savedNoteListWidth;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
        RestoreWindow();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    // Register the global quick-capture hotkey (Ctrl+Shift+N) once we have a window handle.
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);
        RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_N);
        RegisterHotKey(helper.Handle, HOTKEY_ID_EASY, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_N);
        InitTray();
        Closed += (_, _) =>
        {
            UnregisterHotKey(helper.Handle, HOTKEY_ID);
            UnregisterHotKey(helper.Handle, HOTKEY_ID_EASY);
            _trayIcon?.Dispose();
        };
    }

    // Tray icon keeps quick-capture alive while the window is hidden.
    private void InitTray()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadBrandIcon(),
            Text = "PaperNote",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open PaperNote", null, (_, _) => ShowFromTray());
        menu.Items.Add("Quick note  (Ctrl+Alt+N)", null, (_, _) => ShowQuickCapture());
        menu.Items.Add("New note  (Ctrl+Shift+N)", null, (_, _) => { ShowFromTray(); Vm?.QuickCapture(); });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _trayIcon.ContextMenuStrip = menu;
    }

    // Load the tray/balloon icon straight from the packed brand asset. ExtractAssociatedIcon reads
    // the exe's shell-cached icon, which lags behind an asset swap and kept showing the old green one.
    private static System.Drawing.Icon LoadBrandIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/app.ico");
        using var stream = System.Windows.Application.GetResourceStream(uri).Stream;
        return new System.Drawing.Icon(stream);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (id == HOTKEY_ID_EASY)          // Ctrl+Alt+N: lightweight capture card
            {
                ShowQuickCapture();
                handled = true;
            }
            else if (id == HOTKEY_ID)          // Ctrl+Shift+N: open the full app + new note
            {
                if (IsVisible) BringToFront();
                else ShowFromTray();
                Vm?.QuickCapture();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private QuickCaptureWindow? _quickCapture;

    // Pop the small capture card without touching the main window. Text appends to the
    // running "Quick Notes" note; the main window can stay hidden in the tray.
    private void ShowQuickCapture()
    {
        if (_quickCapture is not null) { _quickCapture.Activate(); return; }

        _quickCapture = new QuickCaptureWindow();
        _quickCapture.Captured += text => Vm?.QuickCaptureText(text);
        _quickCapture.Closed += (_, _) => _quickCapture = null;
        _quickCapture.Show();
        _quickCapture.Activate();
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        if (!_trayHintShown)
        {
            _trayIcon?.ShowBalloonTip(2500, "PaperNote", "Still running here. Press Ctrl+Alt+N anytime to jot a note.",
                System.Windows.Forms.ToolTipIcon.None);
            _trayHintShown = true;
        }
    }

    // Closing the window minimizes to tray (keeps the hotkey alive); Exit truly quits.
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            SaveWindow();
            return;
        }
        e.Cancel = true;
        HideToTray();
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Focus the search box the moment the palette appears.
    private void OnPaletteVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PaletteOverlay.Visibility != Visibility.Visible) return;
        PaletteBox.Focus();
        PaletteBox.SelectAll();
    }

    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Vm?.ClosePalette();
                e.Handled = true;
                break;
            case Key.Down:
                MovePaletteSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MovePaletteSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                RunPaletteSelection();
                e.Handled = true;
                break;
        }
    }

    private void MovePaletteSelection(int delta)
    {
        var count = PaletteList.Items.Count;
        if (count == 0) return;
        var index = Math.Clamp(PaletteList.SelectedIndex + delta, 0, count - 1);
        PaletteList.SelectedIndex = index;
        PaletteList.ScrollIntoView(PaletteList.SelectedItem);
    }

    private void OnPaletteItemClick(object sender, MouseButtonEventArgs e) => RunPaletteSelection();

    private void RunPaletteSelection()
    {
        if (PaletteList.SelectedItem is not PaletteItem item) return;
        Vm?.ClosePalette();
        item.Run();
    }

    // Click outside the card closes the palette.
    private void OnPaletteBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PaletteOverlay))
            Vm?.ClosePalette();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnToggleFocus(object sender, RoutedEventArgs e) => ToggleFocusMode();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F11) return;
        ToggleFocusMode();
        e.Handled = true;
    }

    private void ToggleFocusMode()
    {
        _focusMode = !_focusMode;

        if (_focusMode)
        {
            _savedSidebarWidth = SidebarColumn.Width;
            _savedNoteListWidth = NoteListColumn.Width;

            SidebarColumn.Width = new GridLength(0);
            SidebarColumn.MinWidth = 0;
            SidebarSplitterColumn.Width = new GridLength(0);
            NoteListColumn.Width = new GridLength(0);
            NoteListColumn.MinWidth = 0;
            NoteListSplitterColumn.Width = new GridLength(0);

            SidebarPane.Visibility = Visibility.Collapsed;
            NoteListPane.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            NoteListSplitter.Visibility = Visibility.Collapsed;
            FocusButton.Content = "";
            FocusButton.ToolTip = "Show sidebars (F11)";
        }
        else
        {
            SidebarColumn.MinWidth = 160;
            NoteListColumn.MinWidth = 240;
            SidebarColumn.Width = _savedSidebarWidth.Value > 0 ? _savedSidebarWidth : new GridLength(220);
            SidebarSplitterColumn.Width = GridLength.Auto;
            NoteListColumn.Width = _savedNoteListWidth.Value > 0 ? _savedNoteListWidth : new GridLength(320);
            NoteListSplitterColumn.Width = GridLength.Auto;

            SidebarPane.Visibility = Visibility.Visible;
            NoteListPane.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
            NoteListSplitter.Visibility = Visibility.Visible;
            FocusButton.Content = "";
            FocusButton.ToolTip = "Focus note (F11)";
        }
    }

    // Borderless windows overflow the screen edges when maximized. Pad to compensate,
    // and swap the maximize glyph for the restore glyph (Segoe MDL2: E922 / E923).
    private void OnStateChanged(object? sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        RootBorder.Padding = max ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = max ? "" : "";
    }

    private sealed record WindowSettings(double Left, double Top, double Width, double Height, bool Maximized);

    private void RestoreWindow()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(SettingsPath));
            if (s is null) return;

            // Only restore if the saved rect still sits on a visible screen.
            if (s.Width >= MinWidth && s.Height >= MinHeight &&
                s.Left + s.Width > SystemParameters.VirtualScreenLeft + 40 &&
                s.Top + s.Height > SystemParameters.VirtualScreenTop + 40)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.Left; Top = s.Top; Width = s.Width; Height = s.Height;
            }
            if (s.Maximized) WindowState = WindowState.Maximized;
        }
        catch
        {
            // Corrupt settings shouldn't block startup; fall back to defaults.
        }
    }

    private void SaveWindow()
    {
        try
        {
            var b = RestoreBounds;
            var s = new WindowSettings(b.Left, b.Top, b.Width, b.Height,
                WindowState == WindowState.Maximized);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch
        {
            // Best-effort; never fail on close.
        }
    }
}
