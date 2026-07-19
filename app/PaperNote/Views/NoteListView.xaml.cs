using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperNote.ViewModels;

namespace PaperNote.Views;

public partial class NoteListView : UserControl
{
    private MainViewModel? _viewModel;
    private Point _dragStart;
    private NoteItemViewModel? _dragCandidate;

    public NoteListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // --- drag a note out to a folder ---
    private void OnNotePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = FindNote(e.OriginalSource as DependencyObject);
    }

    private static NoteItemViewModel? FindNote(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: NoteItemViewModel vm }) return vm;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void OnNotePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;

        var diff = _dragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var data = new DataObject("PaperNote.Note", _dragCandidate);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
        _dragCandidate = null;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FocusSearchRequested -= FocusSearch;
            _viewModel.ImportRequested -= OpenImportDialog;
        }
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.FocusSearchRequested += FocusSearch;
            _viewModel.ImportRequested += OpenImportDialog;
        }
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnImport(object sender, RoutedEventArgs e) => OpenImportDialog();

    private void OpenImportDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import notes",
            Filter = "Notes and text (*.txt;*.md;*.markdown;*.log;*.json;*.csv)|*.txt;*.md;*.markdown;*.log;*.json;*.csv|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
            Vm?.ImportFiles(dialog.FileNames);
    }

    private void OnRenameBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: NoteItemViewModel note }) return;

        if (e.Key == Key.Enter)
            Vm?.CommitRenameNote(note);
        else if (e.Key == Key.Escape)
            note.IsEditing = false;
    }

    private void OnRenameCommit(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: NoteItemViewModel note } && note.IsEditing)
            Vm?.CommitRenameNote(note);
    }

    // --- note lock (#7) ---

    private void OnLockNote(object sender, RoutedEventArgs e)
    {
        var note = NoteFromMenu(sender);
        if (note is null || Vm is null) return;

        if (!Vm.IsPasswordSet)
        {
            if (!Prompt(setup: true, out var pwd)) return;
            Vm.SetupPassword(pwd);
        }
        else if (!Vm.IsUnlocked && !Unlock())
        {
            return;
        }

        Vm.LockNote(note);
    }

    private void OnUnlockNote(object sender, RoutedEventArgs e)
    {
        var note = NoteFromMenu(sender);
        if (note is null || Vm is null) return;
        if (!Vm.IsUnlocked && !Unlock()) return;
        Vm.RemoveLock(note);
    }

    // Re-prompt until the password is correct or the user cancels.
    private bool Unlock()
    {
        while (Prompt(setup: false, out var pwd))
        {
            if (Vm!.TryUnlock(pwd)) return true;
            MessageBox.Show("Incorrect password.", "PaperNote", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return false;
    }

    private bool Prompt(bool setup, out string password)
    {
        var dialog = new PasswordPrompt(setup) { Owner = Window.GetWindow(this) };
        var ok = dialog.ShowDialog() == true;
        password = ok ? dialog.Password : "";
        return ok;
    }

    private void OnOpenSticky(object sender, RoutedEventArgs e)
    {
        var note = NoteFromMenu(sender);
        if (note is null || Vm is null) return;
        if (Vm.IsNoteLockedPending(note.Model) && !Unlock()) return;
        new StickyWindow(Vm, note.Id).Show();
    }

    private static NoteItemViewModel? NoteFromMenu(object sender) =>
        sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement fe } }
            ? fe.DataContext as NoteItemViewModel
            : null;
}
