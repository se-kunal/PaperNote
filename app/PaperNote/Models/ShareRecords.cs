namespace PaperNote.Models;

// Sender side: a note shared to one peer.
public sealed class ShareSub
{
    public string ShareId { get; set; } = "";
    public int NoteId { get; set; }
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public long LastSyncAt { get; set; }
}

// Receiver side: a note mirrored from a peer.
public sealed class SharedInRow
{
    public string ShareId { get; set; } = "";
    public int NoteId { get; set; }
    public string SenderPeerId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public long LastSyncAt { get; set; }
    public bool Revoked { get; set; }
}
