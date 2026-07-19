namespace PaperNote.Sharing;

// A note one peer offers to another. Markdown travels with it (a note IS plain text).
public sealed class ShareOffer
{
    public string ShareId { get; set; } = "";
    public string FromPeerId { get; set; } = "";
    public string FromName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Markdown { get; set; } = "";
}
