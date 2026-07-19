using System.IO;
using System.IO.Pipes;
using System.Windows;
using PaperNote.Data;
using PaperNote.Themes;
using PaperNote.ViewModels;

namespace PaperNote;

public partial class App : Application
{
    // Single instance: the first process owns the mutex and listens on the pipe; every later
    // launch (double-clicked shortcut, "Open with PaperNote") forwards its args and exits.
    // Without this, each launch spawned a full app with its own tray icon and hotkeys.
    private const string MutexName = @"Local\PaperNote.SingleInstance";
    private const string PipeName = "PaperNote.SingleInstance.Pipe";

    private static Mutex? _instanceMutex;
    private MainViewModel? _viewModel;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ForwardToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        ThemeManager.ApplySaved();

        var database = new Database();
        database.Initialize();

        var files = new FileStore();
        var noteLock = new Services.NoteLock();
        var repository = new NoteRepository(database, files, noteLock);
        repository.EnsureDefaultFolders();
        repository.MigrateToFiles();   // one-time: write a .md file for any note that lacks one

        var firstRun = IsFirstRun(repository);
        if (firstRun)
            CreateWelcomeNote(repository);

        _viewModel = new MainViewModel(repository, noteLock);

        _window = new MainWindow { DataContext = _viewModel };
        _window.Show();

        if (firstRun)
            new Views.FirstRunOverlay().Show();

        StartPipeServer();

        // "Open with PaperNote": file paths arrive as command-line args. Each opens in a throwaway
        // preview window; nothing is saved to the library unless the user chooses to keep it.
        OpenPreviews(e.Args);
    }

    private void OpenPreviews(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            try { new Views.PreviewWindow(_viewModel!, path) { Owner = _window }.Show(); }
            catch { }
        }
    }

    // Later launches land here: hand our args to the first instance over the pipe and quit.
    // No args means the user just started the app again, so ask the first instance to show itself.
    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            var toOpen = args.Where(File.Exists).ToArray();
            writer.Write(toOpen.Length > 0 ? string.Join("\n", toOpen) : "SHOW");
            writer.Flush();
        }
        catch
        {
            // First instance is shutting down or hung; nothing useful left to do.
        }
    }

    private void StartPipeServer()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var message = await reader.ReadToEndAsync();
                    await Dispatcher.InvokeAsync(() => HandleForwarded(message));
                }
                catch
                {
                    // A broken client connection shouldn't kill the listener; keep serving.
                }
            }
        });
    }

    private void HandleForwarded(string message)
    {
        if (message == "SHOW" || string.IsNullOrWhiteSpace(message))
        {
            ShowMainWindow();
            return;
        }
        OpenPreviews(message.Split('\n'));
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.ShowInTaskbar = true;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    // First run = no flag file yet AND an empty database (upgraders skip the welcome).
    private static bool IsFirstRun(NoteRepository repository)
    {
        var flag = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PaperNote", "firstrun.done");

        if (File.Exists(flag))
            return false;

        try { File.WriteAllText(flag, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); }
        catch { }

        return repository.GetTotalNoteCount() == 0;
    }

    // The welcome note demos the product inside the product: table, checklist, hotkey.
    private static void CreateWelcomeNote(NoteRepository repository)
    {
        var user = Environment.UserName;
        var title = $"{user}'s first note";
        var markdown = $"""
            # Welcome, {user} 👋

            PaperNote is fast, offline, and yours. Every note is a plain **.md file** in
            `Documents\PaperNote` — no account, no cloud, no lock-in.

            ## Try these

            - [ ] Press **Ctrl+Alt+N** anywhere in Windows — instant quick note
            - [ ] Copy a few cells in Excel and paste here — they become a real table
            - [ ] Drop a `.md`, `.txt` or `.csv` file into this window — it becomes a note
            - [ ] Type `/` for slash commands

            ## A table, because you can

            | What      | Why                  |
            | --------- | -------------------- |
            | 20 MB     | no bloat             |
            | No account| no lock-in           |
            | Offline   | your data stays home |

            Delete this note when you're done — it's yours now.
            """;

        try { repository.Import(null, title, markdown, $"{title}\n{markdown}"); }
        catch { }
    }
}
