using System.Text.Json;
using System.Text.Json.Serialization;
using SMLoader.Api;

namespace SMLoader.Core;

/// <summary>JSON-backed <see cref="IModConfig"/>, one file per mod.</summary>
internal sealed class ModConfig : IModConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _values;
    private readonly Lock _gate = new();

    public ModConfig(string configDirectory, string modName)
    {
        Directory.CreateDirectory(configDirectory);
        _path = Path.Combine(configDirectory, modName + ".json");
        _values = Load(_path);
    }

    private static Dictionary<string, JsonElement> Load(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, JsonElement>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
                   ?? new Dictionary<string, JsonElement>();
        }
        catch (Exception ex)
        {
            // A corrupt config must not stop the mod from loading.
            Logging.Error($"could not read {path}; starting from defaults", ex);
            return new Dictionary<string, JsonElement>();
        }
    }

    public T Get<T>(string key, T fallback)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out JsonElement element))
                return fallback;

            try
            {
                return element.Deserialize<T>(Options) ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
            _values[key] = JsonSerializer.SerializeToElement(value, Options);
    }

    public void Save()
    {
        lock (_gate)
        {
            string temp = _path + ".tmp";
            try
            {
                // Write-then-rename: File.WriteAllText truncates first, so a
                // force-quit mid-write - routine with games - would leave the
                // player with a zero-byte config and no settings.
                File.WriteAllText(temp, JsonSerializer.Serialize(_values, Options));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Logging.Error($"could not write {_path}", ex);
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }
}
