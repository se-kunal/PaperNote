using System.IO;
using System.Windows;
using System.Windows.Media;

namespace PaperNote.Themes;

// Light/dark theme. The themeable neutral brushes are swapped in Application.Resources at runtime
// and referenced via DynamicResource, so replacing an entry repaints the whole app live. (Mutating
// a brush in-place fails — WPF freezes Freezables once they're added to in-use app resources.)
// The choice persists to %AppData%/PaperNote/theme.txt.
public static class ThemeManager
{
    private static readonly string ThemeFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PaperNote", "theme.txt");

    // key -> (light hex, dark hex). Accent green is identical in both, so it lives in XAML.
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("PaperBrush",        "#FDFBF7",   "#262019"),
        ("SurfaceBrush",      "#FDFBF7",   "#332B21"),
        ("ListBgBrush",       "#F8F4EC",   "#221C16"),
        ("SidebarBgBrush",    "#F3ECDF",   "#1E1913"),
        ("InkBrush",          "#221B12",   "#EFE6DA"),
        ("InkSecondaryBrush", "#564939",   "#C6B8A7"),
        ("InkTertiaryBrush",  "#94836E",   "#9C8D7D"),
        ("LineBrush",         "#163B2B18", "#26FFF0DC"),
        ("HoverBrush",        "#0A3B2B18", "#16FFE8CC"),
        ("SelectedBrush",     "#1FD98E48", "#38D98E48"),
    ];

    public static bool IsDark { get; private set; }

    // Apply the saved theme. Must run before any window loads so DynamicResource refs resolve.
    public static void ApplySaved() => Apply(ReadSaved() == "dark");

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var resources = Application.Current.Resources;

        foreach (var (key, light, hex) in Palette)
            resources[key] = new SolidColorBrush(Parse(dark ? hex : light));

        Save(dark);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static string ReadSaved()
    {
        try { return File.Exists(ThemeFile) ? File.ReadAllText(ThemeFile).Trim() : "light"; }
        catch { return "light"; }
    }

    private static void Save(bool dark)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!);
            File.WriteAllText(ThemeFile, dark ? "dark" : "light");
        }
        catch { }
    }
}
