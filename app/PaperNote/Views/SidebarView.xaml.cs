using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PaperNote.Sharing;
using PaperNote.ViewModels;

namespace PaperNote.Views;

public partial class SidebarView : UserControl
{
    private MainViewModel? _vm;

    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.NameNeeded -= OnNameNeeded;
            _vm.ShareOfferReceived -= OnShareOffer;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.NameNeeded += OnNameNeeded;
            _vm.ShareOfferReceived += OnShareOffer;
        }
    }

    // A peer offered us a note: ask to accept. (Modal, so the result is ready before returning.)
    private void OnShareOffer(ShareOffer offer, Action<bool> reply)
    {
        var dialog = new ShareOfferDialog(offer.FromName, offer.Title) { Owner = Window.GetWindow(this) };
        reply(dialog.ShowDialog() == true);
    }

    // Drag a note onto a peer row to send it.
    private void OnPeerDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("PaperNote.Note") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPeerDrop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PeerInfo peer) return;
        if (e.Data.GetData("PaperNote.Note") is NoteItemViewModel note)
            _ = Vm?.ShareNoteAsync(note, peer);
    }

    // First time sharing is turned on: ask for a display name, then enable.
    private void OnNameNeeded()
    {
        var dialog = new NamePrompt(_vm?.DisplayName ?? "") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            _vm?.SetNameAndEnable(dialog.EnteredName);
    }

    // Open the folder where the note .md files live, in Explorer.
    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PaperNote");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    // --- drop a dragged note onto a folder ---
    private static FolderItemViewModel? TargetOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as FolderItemViewModel;

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent("PaperNote.Note");
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && TargetOf(sender) is { } folder) folder.IsDropTarget = true;
        e.Handled = true;
    }

    private void OnFolderDragLeave(object sender, DragEventArgs e)
    {
        if (TargetOf(sender) is { } folder) folder.IsDropTarget = false;
    }

    private void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (TargetOf(sender) is not { } folder) return;
        folder.IsDropTarget = false;
        if (e.Data.GetData("PaperNote.Note") is NoteItemViewModel note)
            Vm?.MoveNote(note, folder);
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
        if (sender is not TextBox box || box.DataContext is not FolderItemViewModel folder) return;

        if (e.Key == Key.Enter)
            Vm?.CommitRenameFolder(folder);
        else if (e.Key == Key.Escape)
            folder.IsEditing = false;
    }

    private void OnRenameCommit(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FolderItemViewModel folder } && folder.IsEditing)
            Vm?.CommitRenameFolder(folder);
    }
}
