using CommunityToolkit.Mvvm.ComponentModel;
using PaperNote.Models;

namespace PaperNote.ViewModels;

public enum FolderKind { AllNotes, Folder, Trash }

// One row in the sidebar.
public partial class FolderItemViewModel : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private bool _isDropTarget;

    public int? Id { get; }
    public FolderKind Kind { get; }
    public bool IsSystem { get; }

    // Only user-created folders can be renamed or deleted. System rows (All Notes, Trash)
    // and the seeded default folders are locked.
    public bool IsRenamable => Kind == FolderKind.Folder && !IsSystem;

    public FolderItemViewModel(int? id, string name, int count, FolderKind kind = FolderKind.Folder, bool isSystem = false)
    {
        Id = id;
        _name = name;
        _count = count;
        Kind = kind;
        IsSystem = isSystem;
    }

    public static FolderItemViewModel FromModel(Folder f) => new(f.Id, f.Name, f.NoteCount, FolderKind.Folder, f.IsSystem);
}
