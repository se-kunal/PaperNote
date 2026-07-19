using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using PaperNote.Models;
using PaperNote.Services;

namespace PaperNote.Data;

public sealed class TagCount
{
    public string Tag { get; set; } = "";
    public int Count { get; set; }
}

// Note content lives in .md files (FileStore); SQLite is the index over them. Content I/O is
// routed through the file store, keyed by the note's int Id + its RelPath. The file is the
// source of truth for content; the ContentMarkdown column is a cache (and FTS uses ContentText).
public sealed class NoteRepository(Database database, FileStore files, NoteLock noteLock)
{
    private static readonly string[] DefaultFolders =
    [
        "Inbox",
        "Projects",
        "Meetings",
        "Shared",
        "Personal",
        "Archive"
    ];

    private const string NoteColumns =
        "Id, FolderId, Title, TitleManual, ContentHtml, ContentMarkdown, ContentText, RelPath, Pinned, Locked, Deleted, DeletedAt, CreatedAt, UpdatedAt";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string? FolderName(System.Data.IDbConnection db, int? folderId) =>
        folderId is null ? null
        : db.QueryFirstOrDefault<string>("SELECT Name FROM Folder WHERE Id = @Id", new { Id = folderId });

    private static string TitleOrDefault(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "Untitled" : title!;

    // First non-empty line (minus a leading '# '), for naming notes that have no title.
    private static string? FirstLine(string markdown)
    {
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('#').Trim();
            if (line.Length > 0) return line.Length > 120 ? line[..120] : line;
        }
        return null;
    }

    // --- Notes ---

    public List<Note> GetNotes(int? folderId)
    {
        using var db = database.OpenConnection();

        var allSql = $"""
            SELECT {NoteColumns} FROM Note
            WHERE  Deleted = 0
            ORDER  BY Pinned DESC, UpdatedAt DESC
            """;

        var byFolderSql = $"""
            SELECT {NoteColumns} FROM Note
            WHERE  Deleted = 0 AND FolderId = @FolderId
            ORDER  BY Pinned DESC, UpdatedAt DESC
            """;

        var notes = folderId is null
            ? db.Query<Note>(allSql)
            : db.Query<Note>(byFolderSql, new { FolderId = folderId });

        return notes.AsList();
    }

    public List<Note> GetTrash()
    {
        using var db = database.OpenConnection();
        var sql = $"""
            SELECT {NoteColumns} FROM Note
            WHERE  Deleted = 1
            ORDER  BY DeletedAt DESC
            """;
        return db.Query<Note>(sql).AsList();
    }

    public Note? GetById(int id)
    {
        using var db = database.OpenConnection();
        var note = db.QueryFirstOrDefault<Note>($"SELECT {NoteColumns} FROM Note WHERE Id = @Id", new { Id = id });

        // The file is the source of truth for content (picks up edits made outside the app).
        if (note is not null && note.RelPath.Length > 0)
        {
            var fromFile = files.Read(note.RelPath);
            if (fromFile is not null)
            {
                // Locked notes are ciphertext on disk. Decrypt only when the session is unlocked;
                // otherwise return empty content so the UI shows a locked state (never the cipher).
                note.ContentMarkdown = NoteLock.IsLockedBlob(fromFile)
                    ? (noteLock.IsUnlocked ? noteLock.Decrypt(fromFile) : "")
                    : fromFile;
            }
        }

        return note;
    }

    public Note Create(int? folderId)
    {
        using var db = database.OpenConnection();
        var now = Now();
        var rel = files.UniqueRelPath(FolderName(db, folderId), "Untitled");
        files.Write(rel, "");

        const string sql = """
            INSERT INTO Note (FolderId, Title, TitleManual, ContentMarkdown, ContentText, Pinned, Deleted, CreatedAt, UpdatedAt, RelPath)
            VALUES (@FolderId, '', 0, '', '', 0, 0, @Now, @Now, @Rel);
            SELECT last_insert_rowid();
            """;
        var id = db.ExecuteScalar<int>(sql, new { FolderId = folderId, Now = now, Rel = rel });
        return new Note { Id = id, FolderId = folderId, CreatedAt = now, UpdatedAt = now, RelPath = rel };
    }

    // Import an existing note with content in one shot. Title is treated as manual.
    public Note Import(int? folderId, string title, string contentMarkdown, string contentText)
    {
        using var db = database.OpenConnection();
        var now = Now();
        var rel = files.UniqueRelPath(FolderName(db, folderId), title);
        files.Write(rel, contentMarkdown);

        const string sql = """
            INSERT INTO Note (FolderId, Title, TitleManual, ContentMarkdown, ContentText, Pinned, Deleted, CreatedAt, UpdatedAt, RelPath)
            VALUES (@FolderId, @Title, 1, @ContentMarkdown, @ContentText, 0, 0, @Now, @Now, @Rel);
            SELECT last_insert_rowid();
            """;
        var id = db.ExecuteScalar<int>(sql,
            new { FolderId = folderId, Title = title, ContentMarkdown = contentMarkdown, ContentText = contentText, Now = now, Rel = rel });
        return new Note { Id = id, FolderId = folderId, Title = title, TitleManual = true, CreatedAt = now, UpdatedAt = now, RelPath = rel };
    }

    // Editor save. Title follows the first line UNLESS the user renamed it manually. The file is
    // renamed to track the title so the .md on disk stays human-named.
    public void UpdateContent(int id, string firstLineTitle, string contentMarkdown, string contentText)
    {
        using var db = database.OpenConnection();
        var row = db.QueryFirstOrDefault<Note>($"SELECT {NoteColumns} FROM Note WHERE Id = @Id", new { Id = id });
        if (row is null) return;

        // Never write over a locked note without the key — protects the ciphertext from a stray
        // (possibly empty) save when the session isn't unlocked.
        if (row.Locked && !noteLock.IsUnlocked) return;

        // Locked notes keep their title + filename fixed (content is hidden, so first-line
        // auto-titling would wipe the name and rename the file to "Untitled").
        var keepTitle = row.TitleManual || row.Locked;
        var titleForName = keepTitle ? TitleOrDefault(row.Title) : TitleOrDefault(firstLineTitle);
        var folderName = FolderName(db, row.FolderId);
        var newRel = files.UniqueRelPath(folderName, titleForName, except: NullIfEmpty(row.RelPath));

        if (row.RelPath.Length > 0 && !PathEq(newRel, row.RelPath)) files.Move(row.RelPath, newRel);

        // Locked notes: encrypt on disk and keep the index body empty so search can't leak it.
        files.Write(newRel, row.Locked ? noteLock.Encrypt(contentMarkdown) : contentMarkdown);
        var indexMarkdown = row.Locked ? "" : contentMarkdown;
        var indexText = row.Locked ? "" : contentText;

        const string sql = """
            UPDATE Note
            SET    Title           = CASE WHEN @KeepTitle = 1 THEN Title ELSE @Title END,
                   ContentMarkdown = @ContentMarkdown,
                   ContentText     = @ContentText,
                   RelPath         = @Rel,
                   UpdatedAt       = @Now
            WHERE  Id = @Id
            """;
        db.Execute(sql, new { Id = id, Title = firstLineTitle, KeepTitle = keepTitle ? 1 : 0, ContentMarkdown = indexMarkdown, ContentText = indexText, Rel = newRel, Now = Now() });
    }

    // Encrypt a note at rest and wipe its body from the index. Requires an unlocked session.
    public void LockNote(int id)
    {
        using var db = database.OpenConnection();
        var row = db.QueryFirstOrDefault<Note>("SELECT Id, RelPath, ContentMarkdown FROM Note WHERE Id = @Id", new { Id = id });
        if (row is null || row.RelPath.Length == 0) return;

        var current = files.Read(row.RelPath) ?? row.ContentMarkdown;
        var plain = NoteLock.IsLockedBlob(current) ? current : noteLock.Encrypt(current);
        files.Write(row.RelPath, plain);

        db.Execute("UPDATE Note SET Locked = 1, ContentMarkdown = '', ContentText = '', UpdatedAt = @Now WHERE Id = @Id",
            new { Id = id, Now = Now() });
    }

    // Decrypt a note back to plaintext on disk and restore its body to the index. Requires unlock.
    public void RemoveLock(int id)
    {
        using var db = database.OpenConnection();
        var row = db.QueryFirstOrDefault<Note>("SELECT Id, RelPath FROM Note WHERE Id = @Id", new { Id = id });
        if (row is null || row.RelPath.Length == 0) return;

        var stored = files.Read(row.RelPath) ?? "";
        var plain = NoteLock.IsLockedBlob(stored) ? noteLock.Decrypt(stored) : stored;
        files.Write(row.RelPath, plain);

        db.Execute("UPDATE Note SET Locked = 0, ContentMarkdown = @Md, ContentText = @Md, UpdatedAt = @Now WHERE Id = @Id",
            new { Id = id, Md = plain, Now = Now() });
    }

    public const string CaptureNoteTitle = "Quick Notes";

    // The single running note that quick-capture appends to. Found by its reserved title
    // (created with a manual title so appends never rename it).
    // ponytail: title-based lookup — rename it in the app and the next capture starts a fresh one.
    public Note GetOrCreateCaptureNote()
    {
        using var db = database.OpenConnection();
        var existing = db.QueryFirstOrDefault<Note>(
            $"SELECT {NoteColumns} FROM Note WHERE Title = @Title AND Deleted = 0 ORDER BY Id LIMIT 1",
            new { Title = CaptureNoteTitle });
        if (existing is not null) return existing;

        var inboxId = db.QueryFirstOrDefault<int?>("SELECT Id FROM Folder WHERE Name = 'Inbox' LIMIT 1");
        return Import(inboxId, CaptureNoteTitle, "", "");
    }

    public void RenameNote(int id, string title)
    {
        using var db = database.OpenConnection();
        var row = db.QueryFirstOrDefault<Note>("SELECT Id, FolderId, RelPath FROM Note WHERE Id = @Id", new { Id = id });
        if (row is null) return;

        var newRel = files.UniqueRelPath(FolderName(db, row.FolderId), TitleOrDefault(title), except: NullIfEmpty(row.RelPath));
        if (row.RelPath.Length > 0) files.Move(row.RelPath, newRel);

        db.Execute("UPDATE Note SET Title = @Title, TitleManual = 1, RelPath = @Rel, UpdatedAt = @Now WHERE Id = @Id",
            new { Id = id, Title = title, Rel = newRel, Now = Now() });
    }

    public void SetPinned(int id, bool pinned)
    {
        using var db = database.OpenConnection();
        db.Execute("UPDATE Note SET Pinned = @Pinned WHERE Id = @Id",
            new { Id = id, Pinned = pinned ? 1 : 0 });
    }

    public void MoveToFolder(int id, int? folderId)
    {
        using var db = database.OpenConnection();
        var row = db.QueryFirstOrDefault<Note>("SELECT Id, Title, RelPath FROM Note WHERE Id = @Id", new { Id = id });
        if (row is null) return;

        var newRel = files.UniqueRelPath(FolderName(db, folderId), TitleOrDefault(row.Title), except: NullIfEmpty(row.RelPath));
        if (row.RelPath.Length > 0) files.Move(row.RelPath, newRel);

        db.Execute("UPDATE Note SET FolderId = @FolderId, RelPath = @Rel WHERE Id = @Id",
            new { Id = id, FolderId = folderId, Rel = newRel });
    }

    // Soft delete -> Trash (file moves to .trash/). Recoverable.
    public void MoveToTrash(int id)
    {
        using var db = database.OpenConnection();
        var row = db.QueryFirstOrDefault<Note>("SELECT Id, RelPath FROM Note WHERE Id = @Id", new { Id = id });
        if (row is null) return;

        var trashRel = row.RelPath.Length > 0 ? files.MoveToTrash(row.RelPath) : "";
        db.Execute("UPDATE Note SET Deleted = 1, DeletedAt = @Now, RelPath = @Rel WHERE Id = @Id",
            new { Id = id, Now = Now(), Rel = trashRel });
    }

    public void Restore(int id)
    {
        using var db = database.OpenConnection();
        var row = db.QueryFirstOrDefault<Note>("SELECT Id, FolderId, Title, RelPath FROM Note WHERE Id = @Id", new { Id = id });
        if (row is null) return;

        var newRel = files.UniqueRelPath(FolderName(db, row.FolderId), TitleOrDefault(row.Title));
        if (row.RelPath.Length > 0) files.Move(row.RelPath, newRel);

        db.Execute("UPDATE Note SET Deleted = 0, DeletedAt = NULL, RelPath = @Rel WHERE Id = @Id",
            new { Id = id, Rel = newRel });
    }

    // Permanent delete (from Trash).
    public void DeleteForever(int id)
    {
        using var db = database.OpenConnection();
        var rel = db.QueryFirstOrDefault<string>("SELECT RelPath FROM Note WHERE Id = @Id", new { Id = id });
        if (!string.IsNullOrEmpty(rel)) files.Delete(rel);
        db.Execute("DELETE FROM Note WHERE Id = @Id", new { Id = id });
    }

    // Sweep untouched blank notes: no manual title, empty title, empty text. Clears the pile
    // left by "new note" clicks the user never wrote in. Skips the capture note (manual title).
    public void DeleteEmptyNotes()
    {
        using var db = database.OpenConnection();
        var ids = db.Query<int>("""
            SELECT Id FROM Note
            WHERE  Deleted = 0
              AND  Locked = 0
              AND  TitleManual = 0
              AND  TRIM(COALESCE(Title, '')) = ''
              AND  TRIM(COALESCE(ContentText, '')) = ''
            """).AsList();
        foreach (var id in ids)
            DeleteForever(id);
    }

    public void EmptyTrash()
    {
        using var db = database.OpenConnection();
        foreach (var rel in db.Query<string>("SELECT RelPath FROM Note WHERE Deleted = 1"))
            if (!string.IsNullOrEmpty(rel)) files.Delete(rel);

        db.Execute("DELETE FROM Note WHERE Deleted = 1");
        db.Execute("VACUUM");
    }

    // One-time: write a .md file for every note that doesn't have one yet (RelPath empty).
    public void MigrateToFiles()
    {
        using var db = database.OpenConnection();
        var rows = db.Query<Note>($"SELECT {NoteColumns} FROM Note WHERE RelPath = ''").AsList();

        foreach (var n in rows)
        {
            var title = !string.IsNullOrWhiteSpace(n.Title) ? n.Title : FirstLine(n.ContentMarkdown) ?? "Untitled";
            var baseRel = files.UniqueRelPath(FolderName(db, n.FolderId), title);
            files.Write(baseRel, n.ContentMarkdown);
            var finalRel = n.Deleted ? files.MoveToTrash(baseRel) : baseRel;
            db.Execute("UPDATE Note SET RelPath = @Rel WHERE Id = @Id", new { Rel = finalRel, Id = n.Id });
        }
    }

    public List<Note> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        using var db = database.OpenConnection();

        // Title hits weigh 10x over body hits; recency breaks ties.
        var sql = $"""
            SELECT {ColumnsWithPrefix("n")}
            FROM   NoteSearch s
            JOIN   Note n ON n.Id = s.rowid
            WHERE  NoteSearch MATCH @Match AND n.Deleted = 0
            ORDER  BY bm25(NoteSearch, 10.0, 1.0), n.UpdatedAt DESC
            LIMIT  50
            """;
        var hits = db.Query<Note>(sql, new { Match = ToPrefixMatch(query) }).AsList();

        // Notes tagged with a matching tag surface too (prefix match on the tag).
        var tagSql = $"""
            SELECT {ColumnsWithPrefix("n")}
            FROM   Note n
            JOIN   NoteTag t ON t.NoteId = n.Id
            WHERE  n.Deleted = 0 AND t.Tag LIKE @Prefix ESCAPE '\'
            ORDER  BY n.UpdatedAt DESC
            LIMIT  20
            """;
        var escaped = query.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        var tagHits = db.Query<Note>(tagSql, new { Prefix = escaped + "%" }).AsList();

        var seen = new HashSet<int>(hits.Select(n => n.Id));
        hits.AddRange(tagHits.Where(n => seen.Add(n.Id)));

        // Nothing matched: assume a typo and fall back to fuzzy scoring in memory.
        if (hits.Count == 0 && query.Trim().Length >= 3)
            hits = FuzzySearch(db, query.Trim());

        return hits;
    }

    // Typo-tolerant fallback: bigram (Dice) similarity of the query against each
    // note's title and its distinct words. In-memory scan is fine at notes-app
    // scale; only runs when FTS found nothing.
    private List<Note> FuzzySearch(SqliteConnection db, string query)
    {
        var rows = db.Query<(int Id, string Title, string Text)>(
            "SELECT Id, Title, substr(ContentText, 1, 600) FROM Note WHERE Deleted = 0").AsList();

        var q = query.ToLowerInvariant();
        var scored = new List<(int Id, double Score)>();

        foreach (var (id, title, text) in rows)
        {
            var score = Bigram(q, title.ToLowerInvariant());
            foreach (var word in Words(title).Concat(Words(text)))
                score = Math.Max(score, Bigram(q, word));

            if (score >= 0.5)
                scored.Add((id, score));
        }

        if (scored.Count == 0)
            return [];

        var ids = scored.OrderByDescending(s => s.Score).Take(15).Select(s => s.Id).ToArray();
        var notes = db.Query<Note>(
            $"SELECT {NoteColumns} FROM Note WHERE Id IN @Ids", new { Ids = ids }).AsList();
        return notes.OrderBy(n => Array.IndexOf(ids, n.Id)).ToList();
    }

    private static IEnumerable<string> Words(string s) =>
        s.ToLowerInvariant()
         .Split([' ', '\n', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries)
         .Where(w => w.Length >= 3)
         .Distinct();

    // Dice coefficient over character bigrams: 1 = identical, 0 = nothing shared.
    private static double Bigram(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;

        var setA = new HashSet<string>();
        for (var i = 0; i < a.Length - 1; i++) setA.Add(a.Substring(i, 2));

        int overlap = 0, countB = b.Length - 1;
        var used = new HashSet<string>();
        for (var i = 0; i < countB; i++)
        {
            var bg = b.Substring(i, 2);
            if (setA.Contains(bg) && used.Add(bg)) overlap++;
        }

        return 2.0 * overlap / (setA.Count + countB);
    }

    private static string ColumnsWithPrefix(string p) =>
        string.Join(", ", NoteColumns.Split(", ").Select(c => $"{p}.{c}"));

    // Turn raw user text into a safe FTS5 prefix query: each word quoted + '*'.
    private static string ToPrefixMatch(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var terms = words.Select(w => "\"" + w.Replace("\"", "") + "\"*");
        return string.Join(" ", terms);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    private static bool PathEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // --- Tags ---

    public List<TagCount> GetAllTags()
    {
        using var db = database.OpenConnection();
        const string sql = """
            SELECT   t.Tag, COUNT(DISTINCT t.NoteId) AS Count
            FROM     NoteTag t
            JOIN     Note n ON n.Id = t.NoteId
            WHERE    n.Deleted = 0
            GROUP BY t.Tag
            ORDER BY t.Tag COLLATE NOCASE
            """;
        return db.Query<TagCount>(sql).AsList();
    }

    public List<Note> GetNotesByTag(string tag)
    {
        using var db = database.OpenConnection();
        var sql = $"""
            SELECT {ColumnsWithPrefix("n")}
            FROM   Note n
            JOIN   NoteTag t ON t.NoteId = n.Id
            WHERE  t.Tag = @Tag AND n.Deleted = 0
            ORDER  BY n.Pinned DESC, n.UpdatedAt DESC
            """;
        return db.Query<Note>(sql, new { Tag = tag }).AsList();
    }

    public List<string> GetTagsForNote(int noteId)
    {
        using var db = database.OpenConnection();
        return db.Query<string>(
            "SELECT Tag FROM NoteTag WHERE NoteId = @noteId ORDER BY Tag COLLATE NOCASE",
            new { noteId }).AsList();
    }

    public void AddTag(int noteId, string tag)
    {
        using var db = database.OpenConnection();
        db.Execute("INSERT OR IGNORE INTO NoteTag (NoteId, Tag) VALUES (@noteId, @tag)", new { noteId, tag });
    }

    public void RemoveTag(int noteId, string tag)
    {
        using var db = database.OpenConnection();
        db.Execute("DELETE FROM NoteTag WHERE NoteId = @noteId AND Tag = @tag", new { noteId, tag });
    }

    // --- Folders ---

    public void EnsureDefaultFolders()
    {
        using var db = database.OpenConnection();
        var existing = db.Query<string>("SELECT Name FROM Folder").ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in DefaultFolders)
        {
            if (existing.Contains(name)) continue;
            CreateFolder(name, isSystem: true);
        }

        // Existing installs created the defaults before the IsSystem column existed — mark
        // them so they lock to rename/delete like a fresh install.
        db.Execute("UPDATE Folder SET IsSystem = 1 WHERE Name IN @Names", new { Names = DefaultFolders });
    }

    public List<Folder> GetFolders()
    {
        using var db = database.OpenConnection();
        const string sql = """
            SELECT f.Id, f.Name, f.CreatedAt, f.IsSystem,
                   (SELECT COUNT(*) FROM Note n WHERE n.FolderId = f.Id AND n.Deleted = 0) AS NoteCount
            FROM   Folder f
            ORDER  BY CASE f.Name
                      WHEN 'Inbox' THEN 0
                      WHEN 'Projects' THEN 1
                      WHEN 'Meetings' THEN 2
                      WHEN 'Shared' THEN 3
                      WHEN 'Personal' THEN 4
                      WHEN 'Archive' THEN 5
                      ELSE 100
                      END,
                      f.Name COLLATE NOCASE
            """;
        return db.Query<Folder>(sql).AsList();
    }

    public int GetTotalNoteCount()
    {
        using var db = database.OpenConnection();
        return db.ExecuteScalar<int>("SELECT COUNT(*) FROM Note WHERE Deleted = 0");
    }

    public int GetTrashCount()
    {
        using var db = database.OpenConnection();
        return db.ExecuteScalar<int>("SELECT COUNT(*) FROM Note WHERE Deleted = 1");
    }

    public Folder CreateFolder(string name, bool isSystem = false)
    {
        using var db = database.OpenConnection();
        var now = Now();
        files.EnsureFolderDir(name);
        const string sql = """
            INSERT INTO Folder (Name, CreatedAt, IsSystem) VALUES (@Name, @Now, @IsSystem);
            SELECT last_insert_rowid();
            """;
        var id = db.ExecuteScalar<int>(sql, new { Name = name, Now = now, IsSystem = isSystem ? 1 : 0 });
        return new Folder { Id = id, Name = name, CreatedAt = now, IsSystem = isSystem };
    }

    public void RenameFolder(int id, string name)
    {
        using var db = database.OpenConnection();
        var oldName = db.QueryFirstOrDefault<string>("SELECT Name FROM Folder WHERE Id = @Id", new { Id = id });
        db.Execute("UPDATE Folder SET Name = @Name WHERE Id = @Id", new { Id = id, Name = name });
        if (oldName is null) return;

        files.RenameFolderDir(oldName, name);

        // The directory moved with its files; repoint each note's RelPath to the new folder.
        foreach (var n in db.Query<Note>("SELECT Id, RelPath FROM Note WHERE FolderId = @Id", new { Id = id }))
        {
            if (n.RelPath.Length == 0) continue;
            var newRel = files.Combine(name, Path.GetFileName(n.RelPath));
            db.Execute("UPDATE Note SET RelPath = @Rel WHERE Id = @Nid", new { Rel = newRel, Nid = n.Id });
        }
    }

    public void DeleteFolder(int id)
    {
        using var db = database.OpenConnection();
        var notes = db.Query<Note>("SELECT Id, Title, RelPath FROM Note WHERE FolderId = @Id", new { Id = id }).AsList();

        // Notes keep existing; FK ON DELETE SET NULL orphans them to "All Notes".
        db.Execute("DELETE FROM Folder WHERE Id = @Id", new { Id = id });

        // Move the orphaned notes' files back to the root.
        foreach (var n in notes)
        {
            if (n.RelPath.Length == 0) continue;
            var newRel = files.UniqueRelPath(null, TitleOrDefault(n.Title));
            files.Move(n.RelPath, newRel);
            db.Execute("UPDATE Note SET RelPath = @Rel WHERE Id = @Nid", new { Rel = newRel, Nid = n.Id });
        }
    }

    // --- Sharing (BEACON) ---

    // Sender: record that a note is shared to a peer.
    public void AddShare(string shareId, int noteId, string peerId, string peerName)
    {
        using var db = database.OpenConnection();
        db.Execute("""
            INSERT OR REPLACE INTO Share (ShareId, NoteId, PeerId, PeerName, LastSyncAt)
            VALUES (@ShareId, @NoteId, @PeerId, @PeerName, @Now)
            """, new { ShareId = shareId, NoteId = noteId, PeerId = peerId, PeerName = peerName, Now = Now() });
    }

    public List<ShareSub> GetSubscribers(int noteId)
    {
        using var db = database.OpenConnection();
        return db.Query<ShareSub>(
            "SELECT ShareId, NoteId, PeerId, PeerName, LastSyncAt FROM Share WHERE NoteId = @NoteId",
            new { NoteId = noteId }).AsList();
    }

    public ShareSub? GetShare(string shareId)
    {
        using var db = database.OpenConnection();
        return db.QueryFirstOrDefault<ShareSub>(
            "SELECT ShareId, NoteId, PeerId, PeerName, LastSyncAt FROM Share WHERE ShareId = @ShareId",
            new { ShareId = shareId });
    }

    public void TouchShareSync(string shareId)
    {
        using var db = database.OpenConnection();
        db.Execute("UPDATE Share SET LastSyncAt = @Now WHERE ShareId = @ShareId", new { ShareId = shareId, Now = Now() });
    }

    // Receiver: record an incoming mirrored note.
    public void AddSharedIn(string shareId, int noteId, string senderPeerId, string senderName)
    {
        using var db = database.OpenConnection();
        db.Execute("""
            INSERT OR REPLACE INTO SharedIn (ShareId, NoteId, SenderPeerId, SenderName, LastSyncAt, Revoked)
            VALUES (@ShareId, @NoteId, @SenderPeerId, @SenderName, @Now, 0)
            """, new { ShareId = shareId, NoteId = noteId, SenderPeerId = senderPeerId, SenderName = senderName, Now = Now() });
    }

    public List<SharedInRow> GetIncomingFrom(string senderPeerId)
    {
        using var db = database.OpenConnection();
        return db.Query<SharedInRow>(
            "SELECT ShareId, NoteId, SenderPeerId, SenderName, LastSyncAt, Revoked FROM SharedIn WHERE SenderPeerId = @P AND Revoked = 0",
            new { P = senderPeerId }).AsList();
    }

    public SharedInRow? GetSharedInByNote(int noteId)
    {
        using var db = database.OpenConnection();
        return db.QueryFirstOrDefault<SharedInRow>(
            "SELECT ShareId, NoteId, SenderPeerId, SenderName, LastSyncAt, Revoked FROM SharedIn WHERE NoteId = @NoteId",
            new { NoteId = noteId });
    }

    public SharedInRow? GetSharedInByShareId(string shareId)
    {
        using var db = database.OpenConnection();
        return db.QueryFirstOrDefault<SharedInRow>(
            "SELECT ShareId, NoteId, SenderPeerId, SenderName, LastSyncAt, Revoked FROM SharedIn WHERE ShareId = @ShareId",
            new { ShareId = shareId });
    }

    public bool IsSharedIn(int noteId)
    {
        using var db = database.OpenConnection();
        return db.ExecuteScalar<int>("SELECT COUNT(*) FROM SharedIn WHERE NoteId = @NoteId AND Revoked = 0", new { NoteId = noteId }) > 0;
    }

    // Receiver: overwrite a mirrored note with the sender's latest content.
    public void ApplyIncomingUpdate(int noteId, string title, string markdown)
    {
        using var db = database.OpenConnection();
        var row = db.QueryFirstOrDefault<Note>($"SELECT {NoteColumns} FROM Note WHERE Id = @Id", new { Id = noteId });
        if (row is null) return;

        if (row.RelPath.Length > 0) files.Write(row.RelPath, markdown);   // keep filename stable; just update content

        var now = Now();
        db.Execute("""
            UPDATE Note SET Title = @Title, TitleManual = 1, ContentMarkdown = @Md, ContentText = @Text, UpdatedAt = @Now
            WHERE Id = @Id
            """, new { Id = noteId, Title = title, Md = markdown, Text = $"{title}\n{markdown}", Now = now });
        db.Execute("UPDATE SharedIn SET LastSyncAt = @Now WHERE NoteId = @Id", new { Id = noteId, Now = now });
    }
}
