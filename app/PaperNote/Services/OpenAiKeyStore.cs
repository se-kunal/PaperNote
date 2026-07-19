using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PaperNote.Services;

public static class OpenAiKeyStore
{
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PaperNote",
        "openai-key.bin");

    public static string? Read()
    {
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        try
        {
            if (!File.Exists(KeyPath)) return null;
            var encrypted = File.ReadAllBytes(KeyPath);
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(bytes).Trim();
            return key.Length == 0 ? null : key;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var bytes = Encoding.UTF8.GetBytes(apiKey.Trim());
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, encrypted);
    }
}
