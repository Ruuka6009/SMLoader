using SMLoader.Api.Lua;

namespace SMLoader.Api;

/// <summary>Services SMLoader provides to mods.</summary>
public interface IModHost
{
    /// <summary>Directory containing SMLoader.Core.dll.</summary>
    string RootDirectory { get; }

    void Log(string message);

    void LogError(string message, Exception? exception = null);

    /// <summary>
    /// Raised for every <c>lua_State</c> the game creates, on the game's own
    /// thread. This is the safe place to register globals. States created before
    /// the CLR finished booting are replayed here, so no state is missed.
    /// </summary>
    event Action<LuaState> LuaStateCreated;

    /// <summary>
    /// Rewrites a game script in memory, every time the engine compiles it.
    /// <paramref name="pathContains"/> is matched case-insensitively against the
    /// chunk name, so "CreativePlayer.lua" is enough.
    /// </summary>
    /// <remarks>
    /// This is how a mod changes game behaviour without touching Data/Scripts:
    /// nothing on disk is modified, so Steam's file verification has nothing to
    /// undo and uninstalling the mod fully reverts the change.
    /// </remarks>
    /// <returns>
    /// A handle that removes the registration when disposed. A mod that never
    /// unloads can ignore it; it exists so a mod <em>can</em> be unloaded, which
    /// nothing appending to a static list forever could ever support.
    /// </returns>
    IDisposable PatchScript(string pathContains, Action<ScriptLoadContext> patch);

    /// <summary>
    /// Publishes a function as <c>smloader.&lt;name&gt;</c> inside every game
    /// script's environment.
    /// </summary>
    /// <remarks>
    /// The engine sandboxes each script with its own environment, so a plain
    /// global is invisible to game code. SMLoader hooks <c>lua_setfenv</c> and
    /// seeds the table into each environment as it is installed - this is the
    /// supported way for injected Lua to call back into a mod.
    /// </remarks>
    /// <returns>A handle that removes the function when disposed.</returns>
    IDisposable AddLuaFunction(string name, LuaFunction function);

    /// <summary>
    /// Rewrites a game data file (a GUI layout, for instance) as it is opened.
    /// The transform receives the file's text and returns the replacement.
    /// </summary>
    /// <remarks>
    /// Like <see cref="PatchScript"/>, nothing on disk is modified - the result
    /// is cached inside the loader and the engine is redirected to it.
    /// </remarks>
    /// <returns>A handle that removes the transform when disposed.</returns>
    IDisposable PatchAsset(string pathContains, Func<string, string> transform);

    /// <summary>This mod's persisted settings.</summary>
    IModConfig Config { get; }

    /// <summary>
    /// Declarative settings. A mod declares what it exposes and SMLoader
    /// persists it and shows it in the shared in-game settings panel, so mods
    /// do not each build their own configuration UI.
    /// </summary>
    IModSettings Settings { get; }

    /// <summary>
    /// The virtual-key code the player has bound to a game action, read from
    /// Scrap Mechanic's own keybinds.json - e.g. <c>C_Forward</c>, which is W
    /// on QWERTY but Z on AZERTY. Returns <paramref name="fallback"/> when the
    /// action is unbound, bound to a mouse button, or the file is unreadable.
    /// </summary>
    int GetKeyBinding(string action, int fallback);

    /// <summary>
    /// Direct access to the game's memory, for behaviour the Lua API does not
    /// expose. Powerful and build-specific - see <see cref="IMemory"/>.
    /// </summary>
    IMemory Memory { get; }
}
