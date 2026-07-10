namespace Klip.Core.Common;

/// <summary>Where the app keeps things on disk.</summary>
public static class AppPaths
{
    /// <summary>Default root: %LocalAppData%\Klip. Swappable in tests.</summary>
    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klip");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string RegistryJournalFile => Path.Combine(Root, "registry-journal.json");
    public static string DataDir => Path.Combine(Root, "data");
    public static string DatabaseFile => Path.Combine(DataDir, "klip.db");
    public static string MediaDir => Path.Combine(DataDir, "media");
    public static string ThumbsDir => Path.Combine(DataDir, "thumbs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(MediaDir);
        Directory.CreateDirectory(ThumbsDir);
    }

    /// <summary>yyyy-MM subfolder for media.</summary>
    public static string MediaSubdirFor(DateTimeOffset timestamp) =>
        Path.Combine(MediaDir, timestamp.ToString("yyyy-MM"));

    public static string ThumbSubdirFor(DateTimeOffset timestamp) =>
        Path.Combine(ThumbsDir, timestamp.ToString("yyyy-MM"));
}
