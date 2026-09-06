namespace SMLoader.Api;

/// <summary>
/// A mod's declared settings. Declaring a setting is all that is required -
/// SMLoader persists it to the mod's config file and surfaces it in the shared
/// settings panel, so mods do not each build their own configuration UI.
/// </summary>
public interface IModSettings
{
    /// <summary>
    /// Declares a setting, returning the value currently stored for it (or the
    /// default on first run). Safe to call again with the same key; the stored
    /// value is kept and only the presentation is updated.
    /// </summary>
    T Declare<T>(ModSetting setting);

    /// <summary>Current value of a previously declared setting.</summary>
    T Get<T>(string key, T fallback);

    /// <summary>Updates a setting and persists it.</summary>
    void Set<T>(string key, T value);

    /// <summary>
    /// Raised when a setting changes, whether from the panel or from code. The
    /// argument is the setting key.
    /// </summary>
    event Action<string> Changed;
}
