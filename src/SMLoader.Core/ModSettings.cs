using SMLoader.Api;

namespace SMLoader.Core;

/// <summary>
/// Per-mod settings, stored through the mod's own <see cref="IModConfig"/> and
/// registered with <see cref="SettingsRegistry"/> so the shared panel can show
/// them.
/// </summary>
internal sealed class ModSettings : IModSettings
{
    private readonly string _modName;
    private readonly IModConfig _config;
    private readonly Dictionary<string, ModSetting> _declared = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public ModSettings(string modName, IModConfig config)
    {
        _modName = modName;
        _config = config;
    }

    public event Action<string>? Changed;

    public T Declare<T>(ModSetting setting)
    {
        lock (_gate)
            _declared[setting.Key] = setting;

        // Materialise the default on first run so the config file is
        // discoverable and the panel always has something to show.
        T current = _config.Get(setting.Key, (T)Convert.ChangeType(setting.Default, typeof(T)));
        _config.Set(setting.Key, current);

        // Deferred, so declaring N settings at startup is one file write
        // rather than N.
        _config.SaveDeferred();

        SettingsRegistry.Register(new ModSettingEntry(
            _modName,
            setting,
            read: () => ReadBoxed(setting),
            write: value => WriteBoxed(setting, value)));

        Logging.Write($"[{_modName}] setting '{setting.Key}' = {current}");
        return current;
    }

    private object ReadBoxed(ModSetting setting) => setting.Kind switch
    {
        SettingKind.Toggle => _config.Get(setting.Key, Convert.ToBoolean(setting.Default)),
        SettingKind.Key => _config.Get(setting.Key, Convert.ToInt32(setting.Default)),
        SettingKind.Number => _config.Get(setting.Key, Convert.ToDouble(setting.Default)),
        _ => _config.Get(setting.Key, Convert.ToString(setting.Default) ?? string.Empty),
    };

    private void WriteBoxed(ModSetting setting, object value)
    {
        switch (setting.Kind)
        {
            case SettingKind.Toggle:
                _config.Set(setting.Key, Convert.ToBoolean(value));
                break;

            case SettingKind.Key:
                _config.Set(setting.Key, Convert.ToInt32(value));
                break;

            case SettingKind.Number:
                double number = Math.Clamp(Convert.ToDouble(value), setting.Minimum, setting.Maximum);
                _config.Set(setting.Key, number);
                break;

            default:
                _config.Set(setting.Key, Convert.ToString(value) ?? string.Empty);
                break;
        }

        // Deferred: this runs on the game thread, from a panel click the
        // player can repeat as fast as they can press it.
        _config.SaveDeferred();
        Logging.Write($"[{_modName}] setting '{setting.Key}' -> {value}");

        try
        {
            Changed?.Invoke(setting.Key);
        }
        catch (Exception ex)
        {
            // A mod's change handler must not break the settings panel.
            Logging.Error($"[{_modName}] Changed handler for '{setting.Key}' threw", ex);
        }
    }

    public T Get<T>(string key, T fallback) => _config.Get(key, fallback);

    public void Set<T>(string key, T value)
    {
        ModSetting? setting;
        lock (_gate)
            _declared.TryGetValue(key, out setting);

        if (setting is null)
        {
            _config.Set(key, value);
            _config.SaveDeferred();
            return;
        }

        WriteBoxed(setting, value!);
    }
}
