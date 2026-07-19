using CommunityToolkit.Mvvm.ComponentModel;
using PaperNote.Models;

namespace PaperNote.ViewModels;

// One row in the note list. Wraps a Note for binding + preview formatting.
public partial class NoteItemViewModel(Note note) : ObservableObject
{
    public Note Model { get; } = note;

    public int Id => Model.Id;

    [ObservableProperty] private string _title = string.IsNullOrWhiteSpace(note.Title) ? "New Note" : note.Title;
    [ObservableProperty] private string _preview = BuildPreview(note.ContentText);
    [ObservableProperty] private bool _pinned = note.Pinned;
    [ObservableProperty] private bool _locked = note.Locked;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editTitle = "";

    public string DateLabel => FormatDate(Model.UpdatedAt);

    public void Refresh(string title, string contentText, long updatedAt)
    {
        Model.Title = title;
        Model.ContentText = contentText;
        Model.UpdatedAt = updatedAt;
        Title = string.IsNullOrWhiteSpace(title) ? "New Note" : title;
        Preview = BuildPreview(contentText);
        OnPropertyChanged(nameof(DateLabel));
    }

    // Preview = body after the first line (the first line is the title).
    private static string BuildPreview(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var newline = normalized.IndexOf('\n');
        var body = newline >= 0 ? normalized[(newline + 1)..] : "";
        var trimmed = string.Join(" ", body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }

    private static string FormatDate(long unixSeconds)
    {
        var dt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        var today = DateTime.Today;
        if (dt.Date == today) return dt.ToString("h:mm tt");
        if (dt.Date == today.AddDays(-1)) return "Yesterday";
        if (dt.Year == today.Year) return dt.ToString("MMM d");
        return dt.ToString("MMM d, yyyy");
    }
}
