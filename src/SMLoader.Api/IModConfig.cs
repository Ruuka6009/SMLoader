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

    /// <summary>Persists the file now, synchronously.</summary>
    void Save();

    /// <summary>
    /// Asks for the file to be persisted shortly, coalescing with any other
    /// request made in the same window. Use this from anything the player can
    /// repeat quickly - a stepper button, a slider - where a synchronous write
    /// per change is a frame spike on the game thread.
    /// </summary>
    /// <remarks>
    /// The default implementation simply saves, so an existing
    /// <see cref="IModConfig"/> needs no change.
    /// </remarks>
    void SaveDeferred() => Save();
}
