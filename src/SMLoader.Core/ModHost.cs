using SMLoader.Api;
using SMLoader.Api.Lua;

namespace SMLoader.Core;

internal sealed class ModHost : IModHost
{
    private readonly string _modName;

    public ModHost(string rootDirectory, string modName)
    {
        RootDirectory = rootDirectory;
        _modName = modName;

        var config = new ModConfig(Path.Combine(rootDirectory, "Config"), modName);
        Config = config;
        OwnedConfig = config;
        Settings = new ModSettings(modName, Config);
    }

    /// <summary>
    /// The concrete config, so shutdown can flush a deferred write that has not
    /// fired yet. IModConfig deliberately does not expose disposal.
    /// </summary>
    internal ModConfig OwnedConfig { get; }

    public IModConfig Config { get; }

    public IModSettings Settings { get; }

    public string RootDirectory { get; }

    public void Log(string message) => Logging.Write($"[{_modName}] {message}");

    public void LogError(string message, Exception? exception = null)
        => Logging.Error($"[{_modName}] {message}", exception);

    private static readonly ProcessMemory SharedMemory = new();

    public IMemory Memory => SharedMemory;

    public int GetKeyBinding(string action, int fallback)
        => GameKeybinds.Get(action, fallback);

    public IDisposable AddLuaFunction(string name, LuaFunction function)
        => Track(LuaApi.Add(_modName, name, function));

    public IDisposable PatchAsset(string pathContains, Func<string, string> transform)
        => Track(AssetPatcher.Register(_modName, pathContains, transform));

    public IDisposable PatchScript(string pathContains, Action<ScriptLoadContext> patch)
        => Track(ScriptPatcher.Register(_modName, pathContains, patch));

    /// <summary>
    /// Every handle handed to this mod, so unloading it does not depend on the
    /// mod having kept them. A mod that ignores the return value - which all of
    /// them do today - is still fully unregisterable.
    /// </summary>
    private readonly List<IDisposable> _registrations = new();

    private IDisposable Track(IDisposable registration)
    {
        lock (_registrations)
            _registrations.Add(registration);

        return registration;
    }

    /// <summary>
    /// Withdraws everything this mod registered and flushes its config. This is
    /// the half of hot reload that can be written and reasoned about without a
    /// running game; see OPTIMISATION.md 8.4 for the half that cannot.
    /// </summary>
    internal void Unregister()
    {
        IDisposable[] handles;
        lock (_registrations)
        {
            handles = _registrations.ToArray();
            _registrations.Clear();
        }

        foreach (IDisposable handle in handles)
        {
            // One bad handle must not strand the rest still registered.
            try { handle.Dispose(); }
            catch (Exception ex) { Logging.Error($"[{_modName}] unregister failed", ex); }
        }

        SettingsRegistry.RemoveAll(_modName);

        try { OwnedConfig.Dispose(); }
        catch (Exception ex) { Logging.Error($"[{_modName}] config flush failed", ex); }
    }

    public event Action<LuaState>? LuaStateCreated
    {
        add    => Entry.LuaStateCreated += value;
        remove => Entry.LuaStateCreated -= value;
    }
}
