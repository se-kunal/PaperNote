using System.IO;

namespace PaperNote.Data;

// Notes live as .md files on disk: %Documents%/PaperNote/<Folder>/<Title>.md (no folder = root).
// Deleted notes move to <root>/.trash/. The file is the source of truth for note content;
// SQLite (NoteRepository) is the index over these files. All paths here are relative to Root.
public sealed class FileStore
{
    public string Root { get; }
    private string TrashDir => Path.Combine(Root, ".trash");

    public FileStore(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PaperNote");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(TrashDir);
    }

    public string FullPath(string relPath) => Path.Combine(Root, relPath);

    // A safe, unique relative path for a note titled `title` in `folderName` (null = root).
    // `except` is the note's current relPath, allowed to "collide" with itself.
    public string UniqueRelPath(string? folderName, string title, string? except = null)
    {
        var dir = string.IsNullOrWhiteSpace(folderName) ? "" : SafeName(folderName!);
        var baseName = SafeName(title);
        if (baseName.Length == 0) baseName = "Untitled";

        for (var n = 1; ; n++)
        {
            var fileName = n == 1 ? $"{baseName}.md" : $"{baseName} ({n}).md";
            var rel = dir.Length == 0 ? fileName : Path.Combine(dir, fileName);
            if (string.Equals(rel, except, StringComparison.OrdinalIgnoreCase)) return rel;
            if (!File.Exists(FullPath(rel))) return rel;
        }
    }

    // Content of the .md file, or null if it doesn't exist (so callers can fall back to the cache).
    public string? Read(string relPath)
    {
        var full = FullPath(relPath);
        try { return File.Exists(full) ? File.ReadAllText(full) : null; }
        catch { return null; }
    }

    public void EnsureFolderDir(string name) =>
        Directory.CreateDirectory(Path.Combine(Root, SafeName(name)));

    // Relative path for a given file name inside folderName (null = root). Keeps the file name as-is.
    public string Combine(string? folderName, string fileName) =>
        string.IsNullOrWhiteSpace(folderName) ? fileName : Path.Combine(SafeName(folderName!), fileName);

    public void Write(string relPath, string markdown)
    {
        var full = FullPath(relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, markdown);
    }

    public void Move(string oldRel, string newRel)
    {
        if (string.Equals(oldRel, newRel, StringComparison.OrdinalIgnoreCase)) return;
        var src = FullPath(oldRel);
        if (!File.Exists(src)) return;
        var dst = FullPath(newRel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Move(src, dst, overwrite: true);
    }

    public void Delete(string relPath)
    {
        try { var f = FullPath(relPath); if (File.Exists(f)) File.Delete(f); } catch { }
    }

    // Move a note into .trash with a unique name; returns its new relative path.
    public string MoveToTrash(string relPath)
    {
        var rel = UniqueTrashRel(Path.GetFileName(relPath));
        Move(relPath, rel);
        return rel;
    }

    public void RenameFolderDir(string oldName, string newName)
    {
        var src = Path.Combine(Root, SafeName(oldName));
        var dst = Path.Combine(Root, SafeName(newName));
        try { if (Directory.Exists(src) && !Directory.Exists(dst)) Directory.Move(src, dst); } catch { }
    }

    // Every .md under Root except the .trash folder, with last-write time and content.
    public IEnumerable<(string RelPath, long Mtime, string Text)> Scan()
    {
        if (!Directory.Exists(Root)) yield break;

        foreach (var full in Directory.EnumerateFiles(Root, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Root, full);
            if (rel.StartsWith(".trash", StringComparison.OrdinalIgnoreCase)) continue;

            string text;
            long mtime;
            try
            {
                text = File.ReadAllText(full);
                mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeSeconds();
            }
            catch { continue; }

            yield return (rel, mtime, text);
        }
    }

    private string UniqueTrashRel(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        for (var n = 1; ; n++)
        {
            var f = n == 1 ? $"{name}.md" : $"{name} ({n}).md";
            var rel = Path.Combine(".trash", f);
            if (!File.Exists(FullPath(rel))) return rel;
        }
    }

    private static string SafeName(string raw)
    {
        var name = raw.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        name = name.Trim().TrimEnd('.');         // Windows dislikes trailing dots/spaces
        return name.Length > 120 ? name[..120].Trim() : name;
    }
}
