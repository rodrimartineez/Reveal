using System.Text.Json;

namespace Reveal.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // A corrupt settings file must never prevent the app from starting.
            try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch { /* ignore */ }
        }

        return new AppSettings();
    }

    /// <summary>Applies <paramref name="mutate"/> to the current settings, persists and notifies listeners.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            var next = Current.Clone();
            mutate(next);
            Current = next;
            snapshot = next;
            Save(snapshot);
        }

        Changed?.Invoke(snapshot);
    }

    private void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
