using System.Diagnostics;
using Reveal.Core.Settings;

namespace Reveal.App.Services;

/// <summary>
/// Tiny local diagnostics log (%LOCALAPPDATA%\Reveal\logs). It records events and errors,
/// never the text the user selects.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    private static string FilePath
    {
        get
        {
            if (_path is not null) return _path;
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            _path = Path.Combine(AppPaths.LogsDirectory, "reveal.log");
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > 512 * 1024) File.Move(_path, _path + ".old", overwrite: true);
            }
            catch
            {
                // ignore
            }

            return _path;
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        Debug.WriteLine($"[Reveal] {message}");
        try
        {
            lock (Gate)
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}
