using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using PaperNote.ViewModels;

namespace PaperNote.Views;

// "Open with PaperNote" opens files here first: a throwaway view of the doc in the real editor.
// Nothing is written to the library unless the user clicks Save. Close = discard, no note created.
public partial class PreviewWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly string _initialTitle;
    private readonly string _initialMarkdown;

    // Latest content the editor reported. Null until the user edits; Save falls back to the initial doc.
    private string? _title;
    private string? _markdown;
    private string? _text;

    public PreviewWindow(MainViewModel viewModel, string filePath)
    {
        InitializeComponent();
        _viewModel = viewModel;

        var (title, markdown) = viewModel.ReadForPreview(filePath);
        _initialTitle = title;
        _initialMarkdown = markdown;
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Preview" : title;

        InitEditor();
    }

    private async void InitEditor()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaperNote", "WebView2");
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await Web.EnsureCoreWebView2Async(env);

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        var editorDir = Path.Combine(AppContext.BaseDirectory, "editor");
        core.SetVirtualHostNameToFolderMapping(
            "papernote.editor", editorDir, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;
        core.Navigate("https://papernote.editor/editor.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var type = doc.RootElement.GetProperty("type").GetString();

        if (type == "ready")
        {
            _viewModel.ThemeChanged += ApplyTheme;
            ApplyTheme(_viewModel.IsDark);
            var payload = JsonSerializer.Serialize(new
            {
                md = _initialMarkdown,
                html = "",
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                readOnly = false,
                label = ""
            });
            _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.PaperNote.load({payload})");
            return;
        }

        // Editor reports edits as they happen — cache them, but do NOT persist. Save decides.
        if (type == "save")
        {
            _title = doc.RootElement.GetProperty("title").GetString() ?? "";
            _markdown = doc.RootElement.GetProperty("md").GetString() ?? "";
            _text = doc.RootElement.GetProperty("text").GetString() ?? "";
            TitleText.Text = string.IsNullOrWhiteSpace(_title) ? "Preview" : _title;
        }
    }

    private void ApplyTheme(bool dark) =>
        _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.PaperNote.setTheme('{(dark ? "dark" : "light")}')");

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _viewModel.SavePreview(
            _title ?? _initialTitle,
            _markdown ?? _initialMarkdown,
            _text ?? _initialMarkdown);
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            OnSave(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.ThemeChanged -= ApplyTheme;
        Web?.Dispose();
        base.OnClosed(e);
    }
}
