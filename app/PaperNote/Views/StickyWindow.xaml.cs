using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using PaperNote.ViewModels;

namespace PaperNote.Views;

// A small always-on-top window hosting the same TipTap editor for one note. Autosaves through
// the shared repository (file = truth), so edits show in the main list/editor on reselect.
public partial class StickyWindow : Window
{

    private static int _cascade;

    private readonly MainViewModel _viewModel;
    private readonly int _noteId;
    private bool _ready;

    public StickyWindow(MainViewModel viewModel, int noteId)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _noteId = noteId;

        // Cascade successive stickies so they don't stack exactly.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Loaded += (_, _) => { var o = (_cascade++ % 6) * 26; Left += o; Top += o; };

        var note = _viewModel.GetNote(_noteId);
        TitleText.Text = string.IsNullOrWhiteSpace(note?.Title) ? "Note" : note!.Title;

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
            _ready = true;
            _viewModel.ThemeChanged += ApplyTheme;
            ApplyTheme(_viewModel.IsDark);
            LoadNote();
            return;
        }

        if (type == "save")
        {
            var title = doc.RootElement.GetProperty("title").GetString() ?? "";
            var markdown = doc.RootElement.GetProperty("md").GetString() ?? "";
            var text = doc.RootElement.GetProperty("text").GetString() ?? "";
            TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Note" : title;
            _viewModel.SaveNoteById(_noteId, title, markdown, text);
        }
    }

    private void LoadNote()
    {
        var note = _viewModel.GetNote(_noteId);
        if (note is null) return;

        // A locked note that isn't unlocked shows read-only with a banner (no content, no save).
        var lockedPending = _viewModel.IsNoteLockedPending(note);
        var readOnly = _viewModel.IsNoteReadOnly(note) || lockedPending;
        var label = lockedPending ? _viewModel.LockedLabel
                  : readOnly ? _viewModel.SharedInLabel(note) : "";
        var md = lockedPending ? "" : note.ContentMarkdown;
        var html = lockedPending ? "" : note.ContentHtml;

        var payload = JsonSerializer.Serialize(new { md, html, updatedAt = note.UpdatedAt, readOnly, label });
        _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.PaperNote.load({payload})");
    }

    private void ApplyTheme(bool dark)
    {
        if (_ready)
            _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.PaperNote.setTheme('{(dark ? "dark" : "light")}')");
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinGlyph.Text = char.ConvertFromUtf32(Topmost ? 0xE840 : 0xE718);   // Pinned / Unpin
        PinBtn.Opacity = Topmost ? 1.0 : 0.6;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.ThemeChanged -= ApplyTheme;
        Web?.Dispose();
        base.OnClosed(e);
    }
}
