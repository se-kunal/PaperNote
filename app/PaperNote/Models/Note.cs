namespace PaperNote.Models;

public sealed class Note
{
    public int Id { get; set; }
    public int? FolderId { get; set; }
    public string Title { get; set; } = "";
    public bool TitleManual { get; set; }
    public string ContentHtml { get; set; } = "";       // legacy; kept only for one-time migration
    public string ContentMarkdown { get; set; } = "";   // source of truth for note content
    public string ContentText { get; set; } = "";       // plaintext, for FTS search
    public string RelPath { get; set; } = "";            // path of the .md file, relative to the notes root
    public bool Pinned { get; set; }
    public bool Locked { get; set; }                     // encrypted at rest; body excluded from search
    public bool Deleted { get; set; }
    public long? DeletedAt { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
