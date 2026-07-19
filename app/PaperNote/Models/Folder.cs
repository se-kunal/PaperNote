namespace PaperNote.Models;

public sealed class Folder
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public long CreatedAt { get; set; }
    public int NoteCount { get; set; }
    public bool IsSystem { get; set; }
}
