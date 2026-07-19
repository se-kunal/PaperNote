namespace PaperNote.ViewModels;

// One row in the sidebar TAGS section.
public sealed class TagItemViewModel(string name, int count)
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public string Display => "#" + Name;
}
