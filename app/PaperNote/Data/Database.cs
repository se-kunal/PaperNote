using System.IO;
using Microsoft.Data.Sqlite;

namespace PaperNote.Data;

// Owns the SQLite connection and one-time schema setup (tables + FTS5 + triggers).
public sealed class Database
{
    private readonly string _connectionString;

    // dbPath is for tests; production uses the default %AppData%/PaperNote location.
    public Database(string? dbPath = null)
    {
        if (dbPath is null)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PaperNote");
            Directory.CreateDirectory(dir);
            dbPath = Path.Combine(dir, "papernote.db");
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var connection = OpenConnection();

        const string schema = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS Folder (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Name      TEXT    NOT NULL,
                CreatedAt INTEGER NOT NULL,
                IsSystem  INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Note (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderId    INTEGER NULL REFERENCES Folder(Id) ON DELETE SET NULL,
                Title       TEXT    NOT NULL DEFAULT '',
                TitleManual INTEGER NOT NULL DEFAULT 0,
                ContentHtml     TEXT NOT NULL DEFAULT '',
                ContentMarkdown TEXT NOT NULL DEFAULT '',
                ContentText     TEXT NOT NULL DEFAULT '',
                RelPath         TEXT NOT NULL DEFAULT '',
                Pinned      INTEGER NOT NULL DEFAULT 0,
                Locked      INTEGER NOT NULL DEFAULT 0,
                Deleted     INTEGER NOT NULL DEFAULT 0,
                DeletedAt   INTEGER NULL,
                CreatedAt   INTEGER NOT NULL,
                UpdatedAt   INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Note_FolderId ON Note(FolderId);
            CREATE INDEX IF NOT EXISTS IX_Note_UpdatedAt ON Note(UpdatedAt);

            -- LAN sharing (BEACON). Sender side: who a note is shared with.
            CREATE TABLE IF NOT EXISTS Share (
                ShareId    TEXT    PRIMARY KEY,
                NoteId     INTEGER NOT NULL,
                PeerId     TEXT    NOT NULL,
                PeerName   TEXT    NOT NULL DEFAULT '',
                LastSyncAt INTEGER NOT NULL DEFAULT 0
            );

            -- Receiver side: a note mirrored from a peer (read-only).
            CREATE TABLE IF NOT EXISTS SharedIn (
                ShareId      TEXT    PRIMARY KEY,
                NoteId       INTEGER NOT NULL,
                SenderPeerId TEXT    NOT NULL,
                SenderName   TEXT    NOT NULL DEFAULT '',
                LastSyncAt   INTEGER NOT NULL DEFAULT 0,
                Revoked      INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS NoteTag (
                NoteId INTEGER NOT NULL REFERENCES Note(Id) ON DELETE CASCADE,
                Tag    TEXT    NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_NoteTag ON NoteTag(NoteId, Tag);
            CREATE INDEX IF NOT EXISTS IX_NoteTag_Tag ON NoteTag(Tag);

            CREATE VIRTUAL TABLE IF NOT EXISTS NoteSearch USING fts5(
                Title, ContentText, content='Note', content_rowid='Id'
            );

            CREATE TRIGGER IF NOT EXISTS Note_ai AFTER INSERT ON Note BEGIN
                INSERT INTO NoteSearch(rowid, Title, ContentText)
                VALUES (new.Id, new.Title, new.ContentText);
            END;

            CREATE TRIGGER IF NOT EXISTS Note_ad AFTER DELETE ON Note BEGIN
                INSERT INTO NoteSearch(NoteSearch, rowid, Title, ContentText)
                VALUES ('delete', old.Id, old.Title, old.ContentText);
            END;

            CREATE TRIGGER IF NOT EXISTS Note_au AFTER UPDATE ON Note BEGIN
                INSERT INTO NoteSearch(NoteSearch, rowid, Title, ContentText)
                VALUES ('delete', old.Id, old.Title, old.ContentText);
                INSERT INTO NoteSearch(rowid, Title, ContentText)
                VALUES (new.Id, new.Title, new.ContentText);
            END;
            """;

        using var command = connection.CreateCommand();
        command.CommandText = schema;
        command.ExecuteNonQuery();

        EnsureColumns(connection);
    }

    // Add columns introduced after v1.0 to databases created by an older build.
    private static void EnsureColumns(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Note)";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1));
        }

        var additions = new (string Column, string Ddl)[]
        {
            ("TitleManual",    "ALTER TABLE Note ADD COLUMN TitleManual INTEGER NOT NULL DEFAULT 0"),
            ("Deleted",        "ALTER TABLE Note ADD COLUMN Deleted INTEGER NOT NULL DEFAULT 0"),
            ("DeletedAt",      "ALTER TABLE Note ADD COLUMN DeletedAt INTEGER NULL"),
            ("ContentMarkdown","ALTER TABLE Note ADD COLUMN ContentMarkdown TEXT NOT NULL DEFAULT ''"),
            ("RelPath",        "ALTER TABLE Note ADD COLUMN RelPath TEXT NOT NULL DEFAULT ''"),
            ("Locked",         "ALTER TABLE Note ADD COLUMN Locked INTEGER NOT NULL DEFAULT 0")
        };

        foreach (var (column, ddl) in additions)
        {
            if (existing.Contains(column)) continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = ddl;
            alter.ExecuteNonQuery();
        }

        // Folder columns added after v1.0.
        var folderColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Folder)";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                folderColumns.Add(reader.GetString(1));
        }

        if (!folderColumns.Contains("IsSystem"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE Folder ADD COLUMN IsSystem INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
    }
}
