using System.Text.Json;
using System.Text.Json.Serialization;
using Pantallazo.Core;
using Pantallazo.Logging;

namespace Pantallazo.Config;

/// <summary>Lee y guarda la configuración en %APPDATA%\Pantallazo\settings.json (escritura atómica).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.SettingsFolder, "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options);
                if (loaded is not null) Current = loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer la configuración, se usan los valores predeterminados: {ex.Message}");
            TryBackupBroken();
            Current = new AppSettings();
        }
        Current.Normalize();
        return Current;
    }

    /// <summary>Aplica un cambio, guarda y avisa.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            Current.Normalize();
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Sin poder escribir, la app sigue con la configuración en memoria.
                Log.Warn($"No se pudo guardar la configuración: {ex.Message}");
            }
        }
    }

    private void TryBackupBroken()
    {
        try { File.Copy(_path, _path + ".roto", overwrite: true); } catch { }
    }
}
