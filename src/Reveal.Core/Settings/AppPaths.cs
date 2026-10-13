namespace Reveal.Core.Settings;

/// <summary>Well-known locations. Everything user specific lives under %APPDATA% / %LOCALAPPDATA%.</summary>
public static class AppPaths
{
    public const string AppName = "Reveal";

    private static string? _overrideRoot;

    /// <summary>Tests (and portable runs) can redirect all storage to a different root.</summary>
    public static void OverrideRoot(string? root) => _overrideRoot = root;

    public static string Roaming => _overrideRoot is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName)
        : Path.Combine(_overrideRoot, "roaming");

    public static string Local => _overrideRoot is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName)
        : Path.Combine(_overrideRoot, "local");

    public static string SettingsFile => Path.Combine(Roaming, "settings.json");

    public static string ModelsDirectory => Path.Combine(Local, "models");

    public static string BackupsDirectory => Path.Combine(Local, "backups");

    public static string LogsDirectory => Path.Combine(Local, "logs");

    /// <summary>Directory that holds the files shipped with the application (lexicon, language model...).</summary>
    public static string AppDataDirectory => Path.Combine(AppContext.BaseDirectory, "Data");

    public static string LexiconFile => Path.Combine(AppDataDirectory, "lexicon.db");

    public static string LangIdFile => Path.Combine(AppDataDirectory, "langid.json");

    public static void EnsureCreated(string directory) => Directory.CreateDirectory(directory);
}
