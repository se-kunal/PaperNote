using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaperNote.Data;
using PaperNote.Models;
using PaperNote.Services;
using PaperNote.Sharing;

namespace PaperNote.ViewModels;

// Shell coordinator: folders, note list, selection, search, CRUD, trash, import.
public partial class MainViewModel : ObservableObject
{
    private readonly NoteRepository _repository;
    private readonly NoteLock _lock;
    private bool _isSearching;
    private bool _switchingView;   // guards the folder<->tag cross-clear from reloading
    private string? _viewTag;      // when set, the note list shows this tag instead of a folder

    public ObservableCollection<FolderItemViewModel> Folders { get; } = [];
    public ObservableCollection<TagItemViewModel> Tags { get; } = [];
    public ObservableCollection<NoteItemViewModel> Notes { get; } = [];

    [ObservableProperty] private FolderItemViewModel? _selectedFolder;
    [ObservableProperty] private TagItemViewModel? _selectedTag;
    [ObservableProperty] private NoteItemViewModel? _selectedNote;
    [ObservableProperty] private string _searchText = "";

    public bool HasTags => Tags.Count > 0;

    // --- command palette (Ctrl+K) ---
    public ObservableCollection<PaletteItem> PaletteResults { get; } = [];
    [ObservableProperty] private bool _isPaletteOpen;
    [ObservableProperty] private string _paletteQuery = "";
    [ObservableProperty] private PaletteItem? _selectedPaletteItem;

    // Raised when the palette's "Import files…" action runs (file dialog lives in the view).
    public event Action? ImportRequested;

    // Raised by the palette's export actions ("pdf" | "md"); the editor view handles it.
    public event Action<string>? ExportRequested;

    public string CurrentFolderName =>
        SelectedTag is not null ? SelectedTag.Display : SelectedFolder?.Name ?? "All Notes";
    public bool HasNotes => Notes.Count > 0;
    public bool IsTrashSelected => SelectedFolder?.Kind == FolderKind.Trash;

    // Raised when the active note changes so the editor can load its HTML.
    public event Action<Note?>? NoteOpened;

    // Raised by Ctrl+F so the note list can focus its search box.
    public event Action? FocusSearchRequested;

    // Raised after quick-capture creates a note so the editor can take focus.
    public event Action? FocusEditorRequested;

    // Raised by the palette's "New from template" action; the editor opens the picker.
    public event Action? TemplatePickerRequested;

    // Raised when dark mode toggles so the editor's WebView2 can re-theme to match.
    public event Action<bool>? ThemeChanged;

    [ObservableProperty] private bool _isDark = Themes.ThemeManager.IsDark;

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDark = !IsDark;
        Themes.ThemeManager.Apply(IsDark);
        ThemeChanged?.Invoke(IsDark);
    }

    // --- LAN sharing (BEACON): presence/discovery ---
    private readonly ShareConfig _shareConfig = ShareConfig.Load();
    public PresenceService Presence { get; }
    public ObservableCollection<PeerInfo> Peers => Presence.Peers;

    [ObservableProperty] private bool _sharingEnabled;

    public bool IsSearching => SharingEnabled && Peers.Count == 0;
    public bool HasPeers => Peers.Count > 0;
    public string DisplayName => _shareConfig.DisplayName;

    // Raised when the user turns sharing on for the first time and we need a display name.
    public event Action? NameNeeded;

    partial void OnSharingEnabledChanged(bool value) => OnPropertyChanged(nameof(IsSearching));

    [RelayCommand]
    private void ToggleSharing()
    {
        if (SharingEnabled) { DisableSharing(); return; }
        if (string.IsNullOrWhiteSpace(_shareConfig.DisplayName)) { NameNeeded?.Invoke(); return; }
        EnableSharing();
    }

    // Called by the view after the first-run name prompt is confirmed.
    public void SetNameAndEnable(string name)
    {
        _shareConfig.DisplayName = name.Trim();
        EnableSharing();
    }

    private void EnableSharing()
    {
        _shareConfig.SharingEnabled = true;
        _shareConfig.Save();
        SharingEnabled = true;
        Transport.Start();
        Presence.TransportPort = Transport.Port;
        Presence.Start();
    }

    private void DisableSharing()
    {
        _shareConfig.SharingEnabled = false;
        _shareConfig.Save();
        SharingEnabled = false;
        Presence.Stop();
        Transport.Stop();
        OnPropertyChanged(nameof(HasPeers));
    }

    // --- LAN sharing (BEACON): transfer ---
    public TransportService Transport { get; }

    // Raised on the UI thread when a peer offers a note; the view shows an accept dialog and
    // calls back with the decision (synchronously, since it's modal).
    public event Action<ShareOffer, Action<bool>>? ShareOfferReceived;

    private Task<bool> HandleOfferAsync(ShareOffer offer)
    {
        var accepted = false;
        ShareOfferReceived?.Invoke(offer, a => accepted = a);
        if (accepted) ImportSharedNote(offer);
        return Task.FromResult(accepted);
    }

    // Drag a note onto a peer to send it.
    public async Task ShareNoteAsync(NoteItemViewModel note, PeerInfo peer)
    {
        if (!SharingEnabled || peer.Port == 0) return;

        var full = _repository.GetById(note.Id);
        if (full is null) return;

        var offer = new ShareOffer
        {
            ShareId = Guid.NewGuid().ToString("N"),
            FromPeerId = _shareConfig.PeerId,
            FromName = _shareConfig.DisplayName,
            Title = string.IsNullOrWhiteSpace(full.Title) ? "Untitled" : full.Title,
            Markdown = full.ContentMarkdown
        };

        peer.Status = "sending…";
        var ok = await Transport.SendOfferAsync(peer.Ip, peer.Port, offer);
        if (ok) _repository.AddShare(offer.ShareId, note.Id, peer.PeerId, peer.Name);
        peer.Status = ok ? "shared ✓" : "rejected ✗";
        _ = ClearStatusLater(peer);
    }

    private static async Task ClearStatusLater(PeerInfo peer)
    {
        await Task.Delay(2500);
        peer.Status = "";
    }

    private void ImportSharedNote(ShareOffer offer)
    {
        var folderId = GetOrCreateSharedFolderId();
        var note = _repository.Import(folderId, offer.Title, offer.Markdown, $"{offer.Title}\n{offer.Markdown}");
        _repository.AddSharedIn(offer.ShareId, note.Id, offer.FromPeerId, offer.FromName);
        LoadFolders();
        if (SelectedFolder?.Id == folderId) LoadNotes();
        RefreshCounts();
    }

    private int GetOrCreateSharedFolderId()
    {
        var existing = _repository.GetFolders().FirstOrDefault(f => f.Name == "Shared");
        return (existing ?? _repository.CreateFolder("Shared")).Id;
    }

    // --- live one-way sync ---

    // Sender: push edits to each online subscriber.
    private void PushUpdates(int noteId, string title, string markdown)
    {
        foreach (var sub in _repository.GetSubscribers(noteId))
        {
            var peer = Peers.FirstOrDefault(p => p.PeerId == sub.PeerId);
            if (peer is { Port: > 0 }) _ = PushOne(sub.ShareId, peer, title, markdown);
        }
    }

    private async Task PushOne(string shareId, PeerInfo peer, string title, string markdown)
    {
        if (await Transport.SendUpdateAsync(peer.Ip, peer.Port, shareId, title, markdown))
            _repository.TouchShareSync(shareId);
    }

    // Receiver: a sender pushed new content.
    private void OnIncomingUpdate(string shareId, string title, string markdown)
    {
        var row = _repository.GetSharedInByShareId(shareId);
        if (row is null || row.Revoked) return;
        ApplyIncoming(row.NoteId, title, markdown);
    }

    // Sender: answer a receiver's pull with the note's current content.
    private (bool, string, string) OnLatestRequested(string shareId)
    {
        var share = _repository.GetShare(shareId);
        var note = share is null ? null : _repository.GetById(share.NoteId);
        if (note is null) return (false, "", "");
        return (true, string.IsNullOrWhiteSpace(note.Title) ? "Untitled" : note.Title, note.ContentMarkdown);
    }

    // Receiver: pull the latest of every note a (re)appeared peer shares with us.
    private async void PullFromPeer(PeerInfo peer)
    {
        if (peer.Port == 0) return;
        foreach (var row in _repository.GetIncomingFrom(peer.PeerId))
        {
            var (found, title, markdown) = await Transport.RequestLatestAsync(peer.Ip, peer.Port, row.ShareId);
            if (found) ApplyIncoming(row.NoteId, title, markdown);
        }
    }

    private void ApplyIncoming(int noteId, string title, string markdown)
    {
        _repository.ApplyIncomingUpdate(noteId, title, markdown);
        Notes.FirstOrDefault(n => n.Id == noteId)
            ?.Refresh(title, markdown, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (SelectedNote?.Id == noteId) NoteOpened?.Invoke(_repository.GetById(noteId));   // refresh open editor
    }

    public bool IsNoteReadOnly(Note note) => _repository.IsSharedIn(note.Id);

    // --- note lock (#7): one app password, AES-GCM at rest ---

    public bool IsPasswordSet => _lock.IsPasswordSet;
    public bool IsUnlocked => _lock.IsUnlocked;
    public void SetupPassword(string password) => _lock.SetupPassword(password);
    public bool TryUnlock(string password) => _lock.Unlock(password);

    // A locked note whose session isn't unlocked yet — editor shows a locked banner, never content.
    public bool IsNoteLockedPending(Note note) => note.Locked && !_lock.IsUnlocked;
    public string LockedLabel => "🔒 Locked — unlock to view";

    // Locking re-locks the whole session so the note is hidden immediately — viewing it (or its
    // sticky) again requires the password. Encrypt first (needs the key), then clear the key.
    public void LockNote(NoteItemViewModel note)
    {
        _repository.LockNote(note.Id);
        _lock.LockSession();
        _suppressUnlockPrompt = true;   // show the lock banner right after locking, don't nag for the password
        ApplyLockState(note, locked: true);
    }

    public void RemoveLock(NoteItemViewModel note)
    {
        _repository.RemoveLock(note.Id);
        ApplyLockState(note, locked: false);
    }

    private void ApplyLockState(NoteItemViewModel note, bool locked)
    {
        note.Locked = locked;
        note.Model.Locked = locked;

        var stored = _repository.GetById(note.Id);   // empty content when locked + session re-locked
        if (stored is not null) note.Refresh(stored.Title, stored.ContentText, stored.UpdatedAt);
        if (SelectedNote?.Id == note.Id) NoteOpened?.Invoke(stored);
    }

    // One-shot: the editor skips its unlock prompt for the refresh right after locking.
    private bool _suppressUnlockPrompt;
    public bool TakeSuppressUnlockPrompt()
    {
        var v = _suppressUnlockPrompt;
        _suppressUnlockPrompt = false;
        return v;
    }

    public string SharedInLabel(Note note)
    {
        var row = _repository.GetSharedInByNote(note.Id);
        return row is null ? "" : $"Read-only · shared by {row.SenderName} · synced {RelativeTime(row.LastSyncAt)}";
    }

    private static string RelativeTime(long unixSeconds)
    {
        if (unixSeconds <= 0) return "just now";
        var mins = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds) / 60;
        if (mins <= 0) return "just now";
        if (mins < 60) return $"{mins}m ago";
        var hrs = mins / 60;
        return hrs < 24 ? $"{hrs}h ago" : $"{hrs / 24}d ago";
    }

    // Global quick-capture: new note in All Notes, then focus the editor.
    public void QuickCapture()
    {
        SelectedFolder = Folders.First();
        NewNote();
        FocusEditorRequested?.Invoke();
    }

    // Lightweight quick-capture: append the text to one running "Quick Notes" system note,
    // newest entry on top with a timestamp. Never opens the editor or spawns a new note.
    public void QuickCaptureText(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        var note = _repository.GetOrCreateCaptureNote();
        var stamp = DateTimeOffset.Now.ToString("MMM d, yyyy · h:mm tt");
        var entry = $"*{stamp}*\n\n{text}";
        var existing = (note.ContentMarkdown ?? "").Trim();
        var body = existing.Length == 0 ? entry : $"{entry}\n\n---\n\n{existing}";

        _repository.UpdateContent(note.Id, note.Title, body, body);
        RefreshCounts();
    }

    public MainViewModel(NoteRepository repository, NoteLock noteLock)
    {
        _repository = repository;
        _lock = noteLock;
        _repository.DeleteEmptyNotes();   // sweep blank notes left over from previous sessions
        LoadFolders();
        LoadTags();
        SelectedFolder = Folders.FirstOrDefault();

        Presence = new PresenceService(_shareConfig);
        Presence.Peers.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(IsSearching));
            OnPropertyChanged(nameof(HasPeers));
            if (e.NewItems is not null)
                foreach (PeerInfo p in e.NewItems) PullFromPeer(p);   // a peer (re)appeared — pull any notes they share with us
        };
        Transport = new TransportService
        {
            OfferReceived = HandleOfferAsync,
            UpdateReceived = OnIncomingUpdate,
            LatestRequested = OnLatestRequested
        };

        SharingEnabled = _shareConfig.SharingEnabled && !string.IsNullOrWhiteSpace(_shareConfig.DisplayName);
        if (SharingEnabled)
        {
            Transport.Start();
            Presence.TransportPort = Transport.Port;
            Presence.Start();
        }
    }

    partial void OnSelectedFolderChanged(FolderItemViewModel? value)
    {
        if (_switchingView) return;       // programmatic clear from a tag click; ignore
        _viewTag = null;
        SelectedTag = null;               // clicking a folder leaves the tag view
        _isSearching = false;
        SearchText = "";
        LoadNotes();
        OnPropertyChanged(nameof(CurrentFolderName));
        OnPropertyChanged(nameof(IsTrashSelected));
    }

    partial void OnSelectedTagChanged(TagItemViewModel? value)
    {
        if (value is null) return;
        _viewTag = value.Name;
        _isSearching = false;

        _switchingView = true;
        SelectedFolder = null;            // clears the folder highlight without reloading
        _switchingView = false;

        SearchText = "";
        LoadNotes();
        OnPropertyChanged(nameof(CurrentFolderName));
        OnPropertyChanged(nameof(IsTrashSelected));
    }

    private NoteItemViewModel? _prevNote;

    partial void OnSelectedNoteChanged(NoteItemViewModel? value)
    {
        // Leaving an untouched blank note discards it, so empty notes never pile up.
        if (!_suppressPrune && _prevNote is not null && !ReferenceEquals(_prevNote, value)
            && Notes.Contains(_prevNote) && IsBlank(_prevNote))
        {
            _repository.DeleteForever(_prevNote.Id);
            Notes.Remove(_prevNote);
            RefreshCounts();
        }
        _prevNote = value;

        NoteOpened?.Invoke(value is null ? null : _repository.GetById(value.Id));
        LoadCurrentNoteTags();
    }

    private static bool IsBlank(NoteItemViewModel note) =>
        !note.Model.TitleManual
        && !note.Model.Locked                                  // a locked note has empty index text by design — never auto-delete it
        && string.IsNullOrWhiteSpace(note.Model.Title)
        && string.IsNullOrWhiteSpace(note.Model.ContentText);

    // Pills shown for the open note. Empty when no note is selected.
    public ObservableCollection<string> CurrentNoteTags { get; } = [];
    [ObservableProperty] private string _newTagText = "";

    public bool CanTagCurrentNote => SelectedNote is not null && !IsTrashSelected && CurrentNoteTags.Count > 0;

    private void LoadCurrentNoteTags()
    {
        CurrentNoteTags.Clear();
        if (SelectedNote is not null)
            foreach (var tag in _repository.GetTagsForNote(SelectedNote.Id))
                CurrentNoteTags.Add(tag);
        OnPropertyChanged(nameof(CanTagCurrentNote));
    }

    [RelayCommand]
    private void AddTag()
    {
        if (SelectedNote is null) return;
        var tag = NormalizeTag(NewTagText);
        NewTagText = "";
        if (tag is null || CurrentNoteTags.Contains(tag)) return;

        _repository.AddTag(SelectedNote.Id, tag);
        CurrentNoteTags.Add(tag);
        OnPropertyChanged(nameof(CanTagCurrentNote));
        LoadTags();
    }

    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (SelectedNote is null || tag is null) return;
        _repository.RemoveTag(SelectedNote.Id, tag);
        CurrentNoteTags.Remove(tag);
        OnPropertyChanged(nameof(CanTagCurrentNote));
        LoadTags();
    }

    // Tags are lowercase, spaces -> dashes, no leading '#', max 30 chars.
    private static string? NormalizeTag(string raw)
    {
        var t = raw.Trim().TrimStart('#').Trim().ToLowerInvariant();
        t = string.Join("-", t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (t.Length > 30) t = t[..30];
        return t.Length == 0 ? null : t;
    }

    partial void OnSearchTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _isSearching = false;
            LoadNotes();
            return;
        }
        _isSearching = true;
        ReplaceNotes(_repository.Search(value));
    }

    private void LoadFolders()
    {
        var selectedId = SelectedFolder?.Id;
        var selectedKind = SelectedFolder?.Kind;

        Folders.Clear();
        Folders.Add(new FolderItemViewModel(null, "All Notes", _repository.GetTotalNoteCount(), FolderKind.AllNotes));
        foreach (var f in _repository.GetFolders())
            Folders.Add(FolderItemViewModel.FromModel(f));
        Folders.Add(new FolderItemViewModel(-1, "Trash", _repository.GetTrashCount(), FolderKind.Trash));

        // restore selection after a rebuild
        if (selectedKind is not null)
            SelectedFolder = Folders.FirstOrDefault(f => f.Kind == selectedKind && f.Id == selectedId)
                ?? Folders.First();
    }

    private void LoadNotes()
    {
        List<Note> notes;
        if (_viewTag is not null) notes = _repository.GetNotesByTag(_viewTag);
        else if (IsTrashSelected) notes = _repository.GetTrash();
        else notes = _repository.GetNotes(SelectedFolder?.Id);
        ReplaceNotes(notes);
    }

    private void LoadTags()
    {
        var fresh = _repository.GetAllTags();
        bool unchanged = fresh.Count == Tags.Count &&
            fresh.Zip(Tags).All(p => p.First.Tag == p.Second.Name && p.First.Count == p.Second.Count);
        if (unchanged) return;   // avoid rebuilding (and re-animating) the list on every save

        var selectedName = SelectedTag?.Name;
        Tags.Clear();
        foreach (var t in fresh) Tags.Add(new TagItemViewModel(t.Tag, t.Count));
        OnPropertyChanged(nameof(HasTags));
        if (selectedName is not null) SelectedTag = Tags.FirstOrDefault(t => t.Name == selectedName);
    }

    private bool _suppressPrune;

    private void ReplaceNotes(List<Note> notes)
    {
        _suppressPrune = true;   // a list reload is not the user abandoning a blank note
        Notes.Clear();
        foreach (var n in notes)
            Notes.Add(new NoteItemViewModel(n));
        OnPropertyChanged(nameof(HasNotes));
        SelectedNote = Notes.FirstOrDefault();
        _suppressPrune = false;
    }

    [RelayCommand]
    private void NewNote()
    {
        // Capturing into Trash makes no sense; fall back to All Notes.
        var targetFolder = IsTrashSelected ? null : SelectedFolder?.Id;
        if (IsTrashSelected) SelectedFolder = Folders.First();

        var note = _repository.Create(targetFolder);
        var vm = new NoteItemViewModel(note);
        Notes.Insert(0, vm);
        OnPropertyChanged(nameof(HasNotes));
        SelectedNote = vm;
        RefreshCounts();
    }

    // Create a fresh note, then ask the editor to open the template picker over it.
    private void NewNoteFromTemplate()
    {
        NewNote();
        TemplatePickerRequested?.Invoke();
    }

    [RelayCommand]
    private void DeleteNote(NoteItemViewModel? note)
    {
        if (note is null) return;

        if (IsTrashSelected) _repository.DeleteForever(note.Id);
        else _repository.MoveToTrash(note.Id);

        RemoveFromList(note);
        RefreshCounts();
    }

    [RelayCommand]
    private void RestoreNote(NoteItemViewModel? note)
    {
        if (note is null) return;
        _repository.Restore(note.Id);
        RemoveFromList(note);
        RefreshCounts();
    }

    [RelayCommand]
    private void EmptyTrash()
    {
        _repository.EmptyTrash();
        if (IsTrashSelected) { Notes.Clear(); OnPropertyChanged(nameof(HasNotes)); SelectedNote = null; }
        RefreshCounts();
    }

    private void RemoveFromList(NoteItemViewModel note)
    {
        var index = Notes.IndexOf(note);
        Notes.Remove(note);
        OnPropertyChanged(nameof(HasNotes));
        if (SelectedNote == note)
            SelectedNote = Notes.Count > 0 ? Notes[Math.Min(index, Notes.Count - 1)] : null;
    }

    // Drag a note onto a sidebar folder. Dropping on Trash deletes; All Notes unfiles.
    public void MoveNote(NoteItemViewModel note, FolderItemViewModel target)
    {
        if (target.Kind == FolderKind.Trash)
            _repository.MoveToTrash(note.Id);
        else
            _repository.MoveToFolder(note.Id, target.Id);

        LoadNotes();
        RefreshCounts();
    }

    [RelayCommand]
    private void TogglePin(NoteItemViewModel? note)
    {
        if (note is null) return;
        note.Pinned = !note.Pinned;
        _repository.SetPinned(note.Id, note.Pinned);
        if (!_isSearching) LoadNotes();
    }

    // --- rename: note ---
    [RelayCommand]
    private void BeginRenameNote(NoteItemViewModel? note)
    {
        if (note is null) return;
        note.EditTitle = note.Title;
        note.IsEditing = true;
    }

    public void CommitRenameNote(NoteItemViewModel note)
    {
        note.IsEditing = false;
        var name = note.EditTitle.Trim();
        if (string.IsNullOrEmpty(name) || name == note.Title) return;
        _repository.RenameNote(note.Id, name);
        note.Title = name;
        // re-open so the editor reflects nothing structurally; title is metadata only.
    }

    // --- rename: folder ---
    [RelayCommand]
    private void BeginRenameFolder(FolderItemViewModel? folder)
    {
        if (folder is null || !folder.IsRenamable) return;
        folder.EditName = folder.Name;
        folder.IsEditing = true;
    }

    public void CommitRenameFolder(FolderItemViewModel folder)
    {
        folder.IsEditing = false;
        if (!folder.IsRenamable) return;
        var name = folder.EditName.Trim();
        if (string.IsNullOrEmpty(name) || name == folder.Name || folder.Id is null) return;
        _repository.RenameFolder(folder.Id.Value, name);
        folder.Name = name;
        OnPropertyChanged(nameof(CurrentFolderName));
    }

    [RelayCommand]
    private void DeleteFolder(FolderItemViewModel? folder)
    {
        if (folder is null || !folder.IsRenamable || folder.Id is null) return;
        _repository.DeleteFolder(folder.Id.Value);
        LoadFolders();
        SelectedFolder = Folders.First();
    }

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke();

    // --- command palette ---
    [RelayCommand]
    private void OpenPalette()
    {
        PaletteQuery = "";
        BuildPaletteResults();
        IsPaletteOpen = true;
    }

    public void ClosePalette() => IsPaletteOpen = false;

    partial void OnPaletteQueryChanged(string value) => BuildPaletteResults();

    private void BuildPaletteResults()
    {
        PaletteResults.Clear();
        var q = PaletteQuery.Trim();

        foreach (var action in PaletteActions())
            if (q.Length == 0 || action.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
                PaletteResults.Add(action);

        var notes = q.Length == 0 ? _repository.GetNotes(null) : _repository.Search(q);
        foreach (var note in notes.Take(8))
        {
            var id = note.Id;
            PaletteResults.Add(new PaletteItem
            {
                Glyph = "",
                Label = string.IsNullOrWhiteSpace(note.Title) ? "New Note" : note.Title,
                Subtitle = "Jump to note",
                Run = () => OpenNoteById(id)
            });
        }

        SelectedPaletteItem = PaletteResults.FirstOrDefault();
    }

    private IEnumerable<PaletteItem> PaletteActions() =>
    [
        new() { Glyph = "", Label = "New note",      Subtitle = "Create a note",    Run = NewNote },
        new() { Glyph = "", Label = "New from template", Subtitle = "Start a note from a template", Run = NewNoteFromTemplate },
        new() { Glyph = "", Label = "New folder",    Subtitle = "Create a folder",  Run = NewFolder },
        new() { Glyph = "", Label = "Import files…", Subtitle = "Import .txt / .md", Run = () => ImportRequested?.Invoke() },
        new() { Glyph = "", Label = "Export to PDF",      Subtitle = "Save this note as PDF", Run = () => ExportRequested?.Invoke("pdf") },
        new() { Glyph = "", Label = "Export to Markdown", Subtitle = "Save this note as .md", Run = () => ExportRequested?.Invoke("md") },
        new() { Glyph = "", Label = "Empty trash",   Subtitle = "Delete trash forever", Run = EmptyTrash },
    ];

    // Jump to a note from the palette: show it in All Notes and select it.
    private void OpenNoteById(int id)
    {
        _isSearching = false;
        SelectedFolder = Folders.First();
        LoadNotes();
        SelectedNote = Notes.FirstOrDefault(n => n.Id == id);
    }

    [RelayCommand]
    private void NewFolder()
    {
        var folder = _repository.CreateFolder("New Folder");
        LoadFolders();
        var vm = Folders.FirstOrDefault(f => f.Id == folder.Id);
        if (vm is not null)
        {
            SelectedFolder = vm;
            BeginRenameFolder(vm);   // let the user name it immediately
        }
    }

    // Import files as notes into the current folder (or All Notes). Content is
    // shaped by type: csv/tsv become markdown tables, json/xml become fenced
    // code blocks, everything else imports as text.
    public int ImportFiles(IEnumerable<string> paths)
    {
        var folderId = IsTrashSelected ? null : SelectedFolder?.Id;
        var count = 0;
        foreach (var path in paths)
        {
            try
            {
                var content = ShapeImport(path, File.ReadAllText(path));
                ImportOne(folderId, Path.GetFileNameWithoutExtension(path), content);
                count++;
            }
            catch
            {
                // skip unreadable files; importing the rest matters more
            }
        }
        if (count > 0) FinishImport();
        return count;
    }

    // Read a file for a throwaway preview: title from the file name, content shaped like an import
    // but nothing is written. The preview window decides later whether to persist it.
    public (string Title, string Markdown) ReadForPreview(string path)
    {
        var title = Path.GetFileNameWithoutExtension(path);
        var content = ShapeImport(path, File.ReadAllText(path));
        return (title, $"# {title}\n\n{content}");
    }

    // Persist a previewed doc as a real note (user chose "Save"). Content comes from the editor.
    public void SavePreview(string title, string markdown, string text)
    {
        var folderId = IsTrashSelected ? null : SelectedFolder?.Id;
        _repository.Import(folderId, string.IsNullOrWhiteSpace(title) ? "Untitled" : title, markdown, text);
        FinishImport();
    }

    private static string ShapeImport(string path, string raw) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csv"  => DelimitedToMarkdownTable(raw, ','),
            ".tsv"  => DelimitedToMarkdownTable(raw, '\t'),
            ".json" => $"```json\n{raw.Trim()}\n```",
            ".xml"  => $"```xml\n{raw.Trim()}\n```",
            _       => raw
        };

    // ponytail: naive split, no quoted-field handling; swap in a CSV parser if users hit quotes
    private static string DelimitedToMarkdownTable(string raw, char sep)
    {
        var lines = raw.Replace("\r", "").Split('\n')
            .Where(l => l.Trim().Length > 0)
            .Select(l => l.Split(sep).Select(c => c.Trim().Replace("|", "\\|")).ToArray())
            .ToList();

        if (lines.Count == 0)
            return raw;

        var cols = lines.Max(r => r.Length);
        string Row(string[] cells) =>
            "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(i => i < cells.Length ? cells[i] : "")) + " |";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Row(lines[0]));
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", cols)));
        foreach (var row in lines.Skip(1))
            sb.AppendLine(Row(row));
        return sb.ToString();
    }

    // Import files dragged onto the editor. The browser hands us content (no filesystem path),
    // so this mirrors ImportFiles but from the in-memory text the editor already read.
    public void ImportDropped(IReadOnlyList<(string Name, string Content)> files)
    {
        if (files.Count == 0) return;
        var folderId = IsTrashSelected ? null : SelectedFolder?.Id;
        foreach (var (name, content) in files)
            ImportOne(folderId, Path.GetFileNameWithoutExtension(name), content);
        FinishImport();
    }

    // .txt/.md content is already markdown; title comes from the file name.
    private void ImportOne(int? folderId, string title, string content) =>
        _repository.Import(folderId, title, $"# {title}\n\n{content}", $"{title}\n{content}");

    private void FinishImport()
    {
        if (IsTrashSelected) SelectedFolder = Folders.First();
        LoadNotes();
        RefreshCounts();
    }

    // The note the editor should currently show (full row incl. HTML), or null.
    public Note? GetOpenNote() => SelectedNote is null ? null : _repository.GetById(SelectedNote.Id);

    // --- sticky note window (#6) ---

    public Note? GetNote(int id) => _repository.GetById(id);

    // Save from a sticky window (any note, not just the selected one). Refreshes the list row and
    // mirrors to LAN subscribers. ponytail: does NOT force-reload the main editor if the same note
    // is open there (would stomp in-progress typing) — the main editor picks it up on reselect.
    public void SaveNoteById(int id, string title, string markdown, string text)
    {
        _repository.UpdateContent(id, title, markdown, text);
        var stored = _repository.GetById(id);
        Notes.FirstOrDefault(n => n.Id == id)
            ?.Refresh(stored?.Title ?? title, stored?.ContentText ?? text, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        PushUpdates(id, stored?.Title ?? title, markdown);
    }

    // Called by the editor on debounced edits.
    public void SaveCurrentNote(string title, string markdown, string text)
    {
        if (SelectedNote is null) return;
        _repository.UpdateContent(SelectedNote.Id, title, markdown, text);
        var stored = _repository.GetById(SelectedNote.Id);
        SelectedNote.Refresh(stored?.Title ?? title, text, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (SelectedFolder?.Kind == FolderKind.AllNotes)
            Folders[0].Count = _repository.GetTotalNoteCount();

        PushUpdates(SelectedNote.Id, stored?.Title ?? title, markdown);   // mirror to any subscribers
    }

    private void RefreshCounts()
    {
        var counts = _repository.GetFolders().ToDictionary(f => f.Id, f => f.NoteCount);
        foreach (var f in Folders)
        {
            switch (f.Kind)
            {
                case FolderKind.AllNotes: f.Count = _repository.GetTotalNoteCount(); break;
                case FolderKind.Trash: f.Count = _repository.GetTrashCount(); break;
                default:
                    if (f.Id is not null && counts.TryGetValue(f.Id.Value, out var c)) f.Count = c;
                    break;
            }
        }
        LoadTags();
    }
}
