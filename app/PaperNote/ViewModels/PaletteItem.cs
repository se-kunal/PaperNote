namespace PaperNote.ViewModels;

// One row in the command palette: an action to run or a note to jump to.
public sealed class PaletteItem
{
    public required string Glyph { get; init; }
    public required string Label { get; init; }
    public string Subtitle { get; init; } = "";
    public required Action Run { get; init; }
}
