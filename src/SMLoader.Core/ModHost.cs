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

    public void AddLuaFunction(string name, LuaFunction function)
        => LuaApi.Add(_modName, name, function);

    public void PatchAsset(string pathContains, Func<string, string> transform)
        => AssetPatcher.Register(_modName, pathContains, transform);

    public void PatchScript(string pathContains, Action<ScriptLoadContext> patch)
        => ScriptPatcher.Register(_modName, pathContains, patch);

    public event Action<LuaState>? LuaStateCreated
    {
        add    => Entry.LuaStateCreated += value;
        remove => Entry.LuaStateCreated -= value;
    }
}
