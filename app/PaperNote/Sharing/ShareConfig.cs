using System.IO;
using System.Text.Json;

namespace PaperNote.Sharing;

// Identity + sharing prefs, persisted to %AppData%/PaperNote/share.json.
// PeerId is a stable GUID (the real identity across sessions + IP changes).
public sealed class ShareConfig
{
    public string PeerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool SharingEnabled { get; set; }

    private static string Path_ => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaperNote", "share.json");

    public static ShareConfig Load()
    {
        ShareConfig config;
        try
        {
            config = File.Exists(Path_)
                ? JsonSerializer.Deserialize<ShareConfig>(File.ReadAllText(Path_)) ?? new ShareConfig()
                : new ShareConfig();
        }
        catch { config = new ShareConfig(); }

        if (string.IsNullOrEmpty(config.PeerId))
            config.PeerId = Guid.NewGuid().ToString("N");

        return config;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
