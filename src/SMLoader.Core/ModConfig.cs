using System.Text.Json;
using System.Text.Json.Serialization;
using SMLoader.Api;

namespace SMLoader.Core;

/// <summary>JSON-backed <see cref="IModConfig"/>, one file per mod.</summary>
internal sealed class ModConfig : IModConfig, IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// How long a <see cref="SaveDeferred"/> waits for company. Long enough to
    /// collapse a run of stepper clicks and the startup burst of declarations,
    /// short enough that a crash cannot lose much.
    /// </summary>
    private const int FlushDelayMs = 1000;

    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _values;

    /// <summary>
    /// Values already materialised out of JSON, by key and requested type.
    /// Without this, a Lua binding reading one setting per fixed tick pays a full
    /// System.Text.Json converter dispatch 40 times a second for a constant.
    /// </summary>
    private readonly Dictionary<string, (Type Type, object? Value)> _typed = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();
    private readonly Timer _flushTimer;
    private int _dirty;

    public ModConfig(string configDirectory, string modName)
    {
        Directory.CreateDirectory(configDirectory);
        _path = Path.Combine(configDirectory, modName + ".json");
        _values = Load(_path);

        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        // Best effort only: a game that is force-quit never gets here, which is
        // why the delay above is short and the write itself is atomic.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
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
            if (_typed.TryGetValue(key, out (Type Type, object? Value) cached) && cached.Type == typeof(T))
                return (T)cached.Value!;

            if (!_values.TryGetValue(key, out JsonElement element))
                return fallback;

            T result;
            try
            {
                result = element.Deserialize<T>(Options) ?? fallback;
            }
            catch (Exception ex)
            {
                // A setting that silently reverts to its default every launch is
                // very hard to diagnose. Caching the fallback both answers the
                // next call cheaply and keeps this from logging on every read.
                Logging.Error($"{_path}: '{key}' is not a valid {typeof(T).Name}; using the default", ex);
                result = fallback;
            }

            _typed[key] = (typeof(T), result);
            return result;
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            _values[key] = JsonSerializer.SerializeToElement(value, Options);
            _typed[key] = (typeof(T), value);
        }
    }

    public void Save()
    {
        // Whatever prompted this, the pending flush has nothing left to do.
        Interlocked.Exchange(ref _dirty, 0);

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

    public void SaveDeferred()
    {
        // Already scheduled: the pending flush will pick this change up too, so
        // a run of stepper clicks costs one write rather than one each.
        if (Interlocked.Exchange(ref _dirty, 1) == 1)
            return;

        try
        {
            _flushTimer.Change(FlushDelayMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            Save();
        }
    }

    private void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
            return;

        Save();
    }

    /// <summary>
    /// Flushes anything pending and stops the timer. Nothing calls this yet -
    /// SMLoader has no shutdown path (OPTIMISATION.md 5.9) - but the timer is a
    /// disposable this type owns, and the day mods can be unloaded this is where
    /// the pending write has to land.
    /// </summary>
    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
    }
}
