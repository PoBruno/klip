using System.Text.Json;
using System.Text.Json.Serialization;
using Klip.Core.Common;

namespace Klip.Core.Settings;

/// <summary>Persists AppSettings as JSON, applied right away.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly Lock _sync = new();

    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? Changed;

    public SettingsService(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        Load();
    }

    public void Load()
    {
        lock (_sync)
        {
            if (File.Exists(_path))
            {
                try
                {
                    Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
                }
                catch (JsonException)
                {
                    // arquivo corrompido: guarda uma copia pra diagnostico e comeca de novo
                    File.Copy(_path, _path + ".corrupt", overwrite: true);
                    Current = new AppSettings();
                }
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        Changed?.Invoke(Current);
    }

    public void Update(Action<AppSettings> mutate)
    {
        lock (_sync)
        {
            mutate(Current);
        }
        Save();
    }
}
