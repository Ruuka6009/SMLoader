namespace SMLoader.Api;

/// <summary>
/// Per-mod settings, persisted as JSON under <c>dist/Config/&lt;ModName&gt;.json</c>.
/// Values survive restarts; nothing is written into the game folder.
/// </summary>
public interface IModConfig
{
    /// <summary>Reads a value, returning <paramref name="fallback"/> when absent or unreadable.</summary>
    T Get<T>(string key, T fallback);

    /// <summary>Writes a value in memory. Call <see cref="Save"/> to persist it.</summary>
    void Set<T>(string key, T value);

    void Save();
}
