using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PaperNote.Sharing;

// A PaperNote peer seen on the LAN. Initials + avatar color are derived (initials-on-color avatar).
public sealed partial class PeerInfo : ObservableObject
{
    public string PeerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public long LastSeenTicks { get; set; }

    // Transient send status shown on the peer row: "" | "sending…" | "shared ✓" | "rejected ✗".
    [ObservableProperty] private string _status = "";

    public string Initials => ComputeInitials(Name);
    public Brush Avatar => new SolidColorBrush(PickColor(PeerId.Length > 0 ? PeerId : Name));

    // A calm, distinct palette; chosen deterministically per peer so a person's color is stable.
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x0E, 0x9F, 0x6E), // brand green
        Color.FromRgb(0x3B, 0x82, 0xF6), // blue
        Color.FromRgb(0x8B, 0x5C, 0xF6), // violet
        Color.FromRgb(0xF5, 0x9E, 0x0B), // amber
        Color.FromRgb(0xEF, 0x44, 0x44), // red
        Color.FromRgb(0x14, 0xB8, 0xA6), // teal
        Color.FromRgb(0xEC, 0x48, 0x99), // pink
        Color.FromRgb(0x64, 0x74, 0x8B), // slate
    ];

    private static Color PickColor(string key)
    {
        var hash = 0;
        foreach (var c in key) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return Palette[hash % Palette.Length];
    }

    private static string ComputeInitials(string name)
    {
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1)
            return parts[0].Length >= 2 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant();
        return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
    }
}
