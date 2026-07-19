using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperNote.Services;

// One app-wide password protects all locked notes. The password is never stored; we keep a
// salt + a verifier hash so an entered password can be validated, and hold the derived key in
// memory only for the running session. Locked notes are AES-GCM encrypted on disk; their body
// is wiped from the SQLite index so search can't leak it.
public sealed class NoteLock
{
    public const string Marker = "PN-LOCKED-v1:";

    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int NonceSize = 12;   // AES-GCM standard nonce
    private const int TagSize = 16;     // AES-GCM auth tag
    private const int Iterations = 200_000;

    private readonly string _lockFile;
    private byte[]? _sessionKey;

    public NoteLock(string? lockFilePath = null)
    {
        _lockFile = lockFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PaperNote", "lock.json");
    }

    public bool IsPasswordSet => File.Exists(_lockFile);
    public bool IsUnlocked => _sessionKey is not null;

    public static bool IsLockedBlob(string content) => content.StartsWith(Marker, StringComparison.Ordinal);

    // First-time setup: create the salt + verifier and unlock the session.
    public void SetupPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(password, salt);
        var record = new LockRecord
        {
            Salt = Convert.ToBase64String(salt),
            Verifier = Convert.ToBase64String(SHA256.HashData(key)),
            Iterations = Iterations
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_lockFile)!);
        File.WriteAllText(_lockFile, JsonSerializer.Serialize(record));
        _sessionKey = key;
    }

    // Validate a password against the stored verifier; on success, unlock the session.
    public bool Unlock(string password)
    {
        var record = ReadRecord();
        if (record is null) return false;

        var key = DeriveKey(password, Convert.FromBase64String(record.Salt), record.Iterations);
        var verifier = SHA256.HashData(key);
        if (!CryptographicOperations.FixedTimeEquals(verifier, Convert.FromBase64String(record.Verifier)))
            return false;

        _sessionKey = key;
        return true;
    }

    public void LockSession() => _sessionKey = null;

    public string Encrypt(string plaintext)
    {
        var key = _sessionKey ?? throw new InvalidOperationException("Locked notes are not unlocked.");

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize, cipher.Length);

        return Marker + Convert.ToBase64String(blob);
    }

    public string Decrypt(string content)
    {
        var key = _sessionKey ?? throw new InvalidOperationException("Locked notes are not unlocked.");

        var blob = Convert.FromBase64String(content[Marker.Length..]);
        var nonce = blob[..NonceSize];
        var tag = blob[NonceSize..(NonceSize + TagSize)];
        var cipher = blob[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations = Iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private LockRecord? ReadRecord()
    {
        try { return JsonSerializer.Deserialize<LockRecord>(File.ReadAllText(_lockFile)); }
        catch { return null; }
    }

    private sealed class LockRecord
    {
        public string Salt { get; set; } = "";
        public string Verifier { get; set; } = "";
        public int Iterations { get; set; } = NoteLock.Iterations;
    }
}
