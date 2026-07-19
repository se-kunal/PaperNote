using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using PaperNote.Models;
using PaperNote.Services;
using PaperNote.ViewModels;

namespace PaperNote.Views;

public partial class EditorView : UserControl
{
    private MainViewModel? _viewModel;
    private readonly AiService _ai = new();
    private bool _editorReady;
    private Note? _pendingNote;

    public EditorView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.NoteOpened -= OnNoteOpened;
            _viewModel.FocusEditorRequested -= FocusEditor;
            _viewModel.ExportRequested -= OnExportRequested;
            _viewModel.ThemeChanged -= ApplyTheme;
            _viewModel.TemplatePickerRequested -= OpenTemplatePicker;
        }
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.NoteOpened += OnNoteOpened;
            _viewModel.FocusEditorRequested += FocusEditor;
            _viewModel.ExportRequested += OnExportRequested;
            _viewModel.ThemeChanged += ApplyTheme;
            _viewModel.TemplatePickerRequested += OpenTemplatePicker;
        }
    }

    private void OpenTemplatePicker() =>
        _ = Web.CoreWebView2?.ExecuteScriptAsync("window.PaperNote.openTemplatePicker()");

    private void ApplyTheme(bool dark)
    {
        // Host background matches the theme so there is no white flash before the page paints.
        Web.DefaultBackgroundColor = dark
            ? System.Drawing.Color.FromArgb(0x26, 0x20, 0x19)
            : System.Drawing.Color.FromArgb(0xFD, 0xFB, 0xF7);
        _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.PaperNote.setTheme('{(dark ? "dark" : "light")}')");
    }

    // Export the open note to PDF (WebView2 print) or Markdown (turndown in the editor).
    private async void OnExportRequested(string format)
    {
        var note = _viewModel?.GetOpenNote();
        if (note is null || Web.CoreWebView2 is null) return;

        var isPdf = format == "pdf";
        var dialog = new SaveFileDialog
        {
            Title = isPdf ? "Export to PDF" : "Export to Markdown",
            FileName = SafeFileName(note.Title) + (isPdf ? ".pdf" : ".md"),
            Filter = isPdf ? "PDF document (*.pdf)|*.pdf" : "Markdown (*.md)|*.md"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (isPdf)
            {
                var settings = Web.CoreWebView2.Environment.CreatePrintSettings();
                settings.ShouldPrintBackgrounds = true;
                await Web.CoreWebView2.PrintToPdfAsync(dialog.FileName, settings);
            }
            else
            {
                var json = await Web.CoreWebView2.ExecuteScriptAsync("window.PaperNote.toMarkdown()");
                var markdown = JsonSerializer.Deserialize<string>(json) ?? "";
                await File.WriteAllTextAsync(dialog.FileName, markdown);
            }
        }
        catch (Exception ex)
        {
            Log($"Export ({format}) failed: {ex.Message}");
        }
    }

    private static string SafeFileName(string title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Note" : title;
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name.Trim();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_editorReady) return;

        // Keep the WebView2 user-data folder under %AppData%/PaperNote, not next to the exe.
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PaperNote", "WebView2");
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
        core.ProcessFailed += (_, ev) => Log($"ProcessFailed: {ev.ProcessFailedKind}");
        core.Navigate("https://papernote.editor/editor.html");
    }

    private static void Log(string msg)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PaperNote", "editor-log.txt");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var type = doc.RootElement.GetProperty("type").GetString();

        if (type == "error")
        {
            Log($"  JS error: {doc.RootElement.GetProperty("msg").GetString()}");
            return;
        }

        if (type == "ready")
        {
            _editorReady = true;
            _pendingNote = null;
            if (_viewModel is not null) ApplyTheme(_viewModel.IsDark);
            OpenInEditor(_viewModel?.GetOpenNote());
            return;
        }

        if (type == "save")
        {
            var title = doc.RootElement.GetProperty("title").GetString() ?? "";
            var markdown = doc.RootElement.GetProperty("md").GetString() ?? "";
            var text = doc.RootElement.GetProperty("text").GetString() ?? "";
            _viewModel?.SaveCurrentNote(title, markdown, text);
            return;
        }

        if (type == "ai-ensure-key")
        {
            _ = EnsureOpenAiKeyAsync();
            return;
        }

        if (type == "ai-chunk")
        {
            var index = doc.RootElement.GetProperty("index").GetInt32();
            var text = doc.RootElement.GetProperty("text").GetString() ?? "";
            _ = CleanChunkAsync(index, text);
            return;
        }

        if (type == "ai-final")
        {
            var text = doc.RootElement.GetProperty("text").GetString() ?? "";
            _ = SummarizeMeetingAsync(text);
            return;
        }

        if (type == "ai-enhance")
        {
            var text = doc.RootElement.GetProperty("text").GetString() ?? "";
            _ = EnhanceTextAsync(text);
            return;
        }

        if (type == "import-files")
        {
            var files = doc.RootElement.GetProperty("files").EnumerateArray()
                .Select(f => (
                    Name: f.GetProperty("name").GetString() ?? "Untitled",
                    Content: f.GetProperty("content").GetString() ?? ""))
                .ToList();
            _viewModel?.ImportDropped(files);
        }
    }

    // Make sure we have an OpenAI key before the editor starts the agentic loop.
    // Reply mode: "key" (ready), "offline" (local cleanup), or "cancel".
    private async Task EnsureOpenAiKeyAsync()
    {
        if (Web.CoreWebView2 is null) return;

        var mode = "key";
        if (string.IsNullOrWhiteSpace(OpenAiKeyStore.Read()))
        {
            var (choice, key) = PromptForOpenAiKey();
            if (choice == "key" && !string.IsNullOrWhiteSpace(key))
                OpenAiKeyStore.Save(key);
            else
                mode = choice;
        }

        var payload = JsonSerializer.Serialize(new { mode });
        await Web.CoreWebView2.ExecuteScriptAsync($"window.PaperNote.aiKeyStatus({payload})");
    }

    private async Task CleanChunkAsync(int index, string text)
    {
        if (Web.CoreWebView2 is null) return;

        object payload;
        try
        {
            var apiKey = OpenAiKeyStore.Read()
                ?? throw new InvalidOperationException("OpenAI API key not found.");
            var markdown = await _ai.CleanChunkAsync(text, apiKey);
            payload = new { index, ok = true, markdown };
        }
        catch (Exception ex)
        {
            payload = new { index, ok = false, error = ex.Message };
        }

        var json = JsonSerializer.Serialize(payload);
        await Web.CoreWebView2.ExecuteScriptAsync($"window.PaperNote.aiChunkDone({json})");
    }

    private async Task SummarizeMeetingAsync(string cleanedNotes)
    {
        if (Web.CoreWebView2 is null) return;

        object payload;
        try
        {
            var apiKey = OpenAiKeyStore.Read()
                ?? throw new InvalidOperationException("OpenAI API key not found.");
            var markdown = await _ai.SummarizeAsync(cleanedNotes, apiKey);
            payload = new { ok = true, markdown };
        }
        catch (Exception ex)
        {
            payload = new { ok = false, error = ex.Message };
        }

        var json = JsonSerializer.Serialize(payload);
        await Web.CoreWebView2.ExecuteScriptAsync($"window.PaperNote.aiFinalDone({json})");
    }

    private async Task EnhanceTextAsync(string text)
    {
        if (Web.CoreWebView2 is null) return;

        object payload;
        try
        {
            var apiKey = OpenAiKeyStore.Read()
                ?? throw new InvalidOperationException("OpenAI API key not found.");
            var rewritten = await _ai.EnhanceAsync(text, apiKey);
            payload = new { ok = true, markdown = rewritten };
        }
        catch (Exception ex)
        {
            payload = new { ok = false, error = ex.Message };
        }

        var json = JsonSerializer.Serialize(payload);
        await Web.CoreWebView2.ExecuteScriptAsync($"window.PaperNote.aiEnhanceDone({json})");
    }

    private (string Choice, string? Key) PromptForOpenAiKey()
    {
        var choice = "cancel";
        string? key = null;
        Dispatcher.Invoke(() =>
        {
            var prompt = new ApiKeyPrompt { Owner = Window.GetWindow(this) };
            if (prompt.ShowDialog() != true) return;
            if (prompt.UseOffline) { choice = "offline"; return; }
            choice = "key";
            key = prompt.ApiKey;
        });
        return (choice, key);
    }

    private void OnNoteOpened(Note? note)
    {
        if (!_editorReady) { _pendingNote = note; return; }
        OpenInEditor(note);
    }

    private void FocusEditor()
    {
        Web.Focus();
        _ = Web.CoreWebView2?.ExecuteScriptAsync("window.PaperNote.focus()");
    }

    private void OpenInEditor(Note? note)
    {
        if (note is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            _ = Web.CoreWebView2?.ExecuteScriptAsync("window.PaperNote.clear()");
            return;
        }

        // Locked note: offer to unlock. If unlocked, re-fetch decrypted content; if declined, it
        // stays read-only with a lock banner (and never loads content or autosaves over the cipher).
        if (_viewModel?.IsNoteLockedPending(note) == true
            && !_viewModel.TakeSuppressUnlockPrompt() && PromptUnlock())
            note = _viewModel.GetOpenNote() ?? note;

        EmptyState.Visibility = Visibility.Collapsed;
        // Send markdown (source of truth) + legacy HTML so the editor can migrate old notes once.
        // Shared-in notes load read-only with a banner.
        var lockedPending = _viewModel?.IsNoteLockedPending(note) ?? false;
        var readOnly = (_viewModel?.IsNoteReadOnly(note) ?? false) || lockedPending;
        var label = lockedPending ? _viewModel?.LockedLabel ?? ""
                  : readOnly ? _viewModel?.SharedInLabel(note) ?? "" : "";
        var md = lockedPending ? "" : note.ContentMarkdown;
        var html = lockedPending ? "" : note.ContentHtml;
        var payload = JsonSerializer.Serialize(new { md, html, updatedAt = note.UpdatedAt, readOnly, label });
        _ = Web.CoreWebView2?.ExecuteScriptAsync($"window.PaperNote.load({payload})");
    }

    // Ask for the lock password, re-prompting on a wrong entry until it's correct or cancelled.
    private bool PromptUnlock()
    {
        if (_viewModel is null) return false;
        while (true)
        {
            var dialog = new PasswordPrompt(setup: false) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return false;
            if (_viewModel.TryUnlock(dialog.Password)) return true;
            MessageBox.Show("Incorrect password.", "PaperNote", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
