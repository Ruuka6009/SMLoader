namespace SMLoader.Api;

/// <summary>
/// Implement this in a mod assembly placed under <c>dist/Mods/&lt;YourMod&gt;/</c>.
/// SMLoader discovers every public parameterless-constructible implementation.
/// </summary>
public interface IMod
{
    string Name { get; }

    string Version => "0.1.0";

    /// <summary>
    /// Called once, on the loader thread, right after the mod is instantiated.
    /// Do not touch Lua here - subscribe to <see cref="IModHost.LuaStateCreated"/> instead.
    /// </summary>
    void OnLoad(IModHost host);
}
