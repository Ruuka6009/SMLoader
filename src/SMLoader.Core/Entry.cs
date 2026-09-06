using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SMLoader.Api.Lua;

namespace SMLoader.Core;

/// <summary>
/// Managed entry point. The native shim resolves and calls <see cref="Boot"/>
/// through hostfxr once CoreCLR is running.
/// </summary>
public static class Entry
{
    public const string Version = "0.1.0";

    /// <summary>Mirror of <c>smloader::BootContext</c> in shim.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BootContext
    {
        public uint Size;                 // sizeof(BootContext)
        public nint RootDir;              // const wchar_t*
        public nint SetLuaStateCallback;  // void (*)(void (*)(void* L))
        public nint SetLuaReadyCallback;  // void (*)(int  (*)(void* L))
        public nint SetScriptLoadCallback; // void (*)(load, free)
        public nint SetSetFenvCallback;    // void (*)(void (*)(void* L))
        public nint SetFileOpenCallback;   // void (*)(int (*)(const wchar_t*, wchar_t*, int))
    }

    /// <summary>Raised on the game's thread for every lua_State it creates.</summary>
    internal static event Action<LuaState>? LuaStateCreated;

    private static ModLoader? _modLoader;
    private static List<string> _splash = new();
    private static int _splashEchoed;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe int Boot(nint contextPtr)
    {
        try
        {
            if (contextPtr == 0)
                return 1;

            BootContext context = *(BootContext*)contextPtr;
            string root = Marshal.PtrToStringUni(context.RootDir) ?? AppContext.BaseDirectory;

            Logging.Initialize(root);
            AssetPatcher.Initialize(root);
            SettingsPanel.Install();
            LuaState.ErrorSink = ex => Logging.Error("unhandled exception inside a Lua callback", ex);

            _splash = Banner.Build("Mod Loader is starting", new[]
            {
                new KeyValuePair<string, string>("version", Version),
                new KeyValuePair<string, string>("runtime", ".NET " + Environment.Version),
                new KeyValuePair<string, string>("process", $"pid {Environment.ProcessId}"),
                new KeyValuePair<string, string>("root", root),
            });
            foreach (string line in _splash)
                Logging.Write(line);

            _modLoader = new ModLoader(root);
            _modLoader.LoadAll();

            // After mods have declared their settings, so the panel sees them.
            SettingsRegistry.PublishToLua();

            List<string> ready = Banner.Build("Ready", new[]
            {
                new KeyValuePair<string, string>("mods", DescribeMods()),
                new KeyValuePair<string, string>("waiting", "for the game's Lua VM"),
            });
            foreach (string line in ready)
                Logging.Write(line);
            _splash.AddRange(ready);

            // Installed last so that any lua_State the shim queued while the CLR
            // was starting is replayed after the mods have subscribed.
            var install = (delegate* unmanaged[Cdecl]<nint, void>)context.SetLuaStateCallback;
            install((nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnLuaStateCreated);

            var installReady = (delegate* unmanaged[Cdecl]<nint, void>)context.SetLuaReadyCallback;
            installReady((nint)(delegate* unmanaged[Cdecl]<nint, int>)&OnLuaMaybeReady);

            var installScripts = (delegate* unmanaged[Cdecl]<nint, nint, void>)context.SetScriptLoadCallback;
            installScripts(
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nuint, nint*, nuint*, int>)&OnScriptLoading,
                (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnScriptFree);

            var installSetFenv = (delegate* unmanaged[Cdecl]<nint, void>)context.SetSetFenvCallback;
            installSetFenv((nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnSetFenv);

            var installFileOpen = (delegate* unmanaged[Cdecl]<nint, void>)context.SetFileOpenCallback;
            installFileOpen((nint)(delegate* unmanaged[Cdecl]<nint, nint, int, int>)&OnFileOpen);

            return 0;
        }
        catch (Exception ex)
        {
            Logging.Error("boot failed", ex);
            return 1;
        }
    }

    private static string DescribeMods()
    {
        IReadOnlyList<Api.IMod> mods = _modLoader?.Loaded ?? Array.Empty<Api.IMod>();
        if (mods.Count == 0)
            return "none found";

        return $"{mods.Count} - " + string.Join(", ", mods.Select(m => $"{m.Name} {m.Version}"));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnLuaStateCreated(nint L)
    {
        // Runs on the game's script thread. Never let anything escape back
        // into native code.
        try
        {
            Logging.Write($"lua_State 0x{L:x} available");
            var lua = new LuaState(L);

            LuaStateCreated?.Invoke(lua);
        }
        catch (Exception ex)
        {
            Logging.Error("a mod threw while handling a new lua_State", ex);
        }
    }

    /// <summary>
    /// Polled from the lua_pcall detour. A state fresh out of luaL_newstate has
    /// neither sm nor print, so the splash cannot go into the game's log there.
    /// We wait until the game has populated the VM, emit once, and return 1 so
    /// the detour retires itself.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnLuaMaybeReady(nint L)
    {
        try
        {
            if (_splashEchoed != 0)
                return 1;

            if (!HasGlobalTable(L, "sm"))
                return 0;

            if (Interlocked.Exchange(ref _splashEchoed, 1) != 0)
                return 1;

            EchoSplashToGameLog(new LuaState(L));
            return 1;
        }
        catch (Exception ex)
        {
            Logging.Error("splash emission failed", ex);
            return 1; // never leave the detour spinning on a broken path
        }
    }

    /// <summary>
    /// Runs on the game thread for every chunk the engine is about to compile.
    /// Returning 1 makes it compile our replacement instead of the file's own
    /// bytes - which is how mods change game behaviour with nothing on disk
    /// modified.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnScriptLoading(nint namePtr, nint sourcePtr, nuint length,
                                              nint* outSource, nuint* outLength)
    {
        try
        {
            string name = Marshal.PtrToStringUTF8(namePtr) ?? string.Empty;
            if (!ScriptPatcher.WantsScript(name))
                return 0;

            // Precompiled LuaJIT bytecode starts with ESC 'L' 'J'; never treat
            // that as text.
            if (length == 0 || ((byte*)sourcePtr)[0] == 0x1B)
                return 0;

            string source = Encoding.UTF8.GetString((byte*)sourcePtr, checked((int)length));
            string? patched = ScriptPatcher.Apply(name, source);
            if (patched is null)
                return 0;

            byte[] bytes = Encoding.UTF8.GetBytes(patched);
            nint buffer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);

            *outSource = buffer;
            *outLength = (nuint)bytes.Length;
            return 1;
        }
        catch (Exception ex)
        {
            Logging.Error("script transform failed; compiling the original", ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnScriptFree(nint buffer)
    {
        try
        {
            Marshal.FreeHGlobal(buffer);
        }
        catch
        {
            // Freeing must never throw back into native code.
        }
    }

    /// <summary>
    /// Runs with a script's environment table on top of the Lua stack, on the
    /// game thread. Seeds the smloader table into it - the only way to reach a
    /// sandboxed script, since it never sees LUA_GLOBALSINDEX.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnSetFenv(nint L)
    {
        try
        {
            LuaApi.Seed(L);
        }
        catch (Exception ex)
        {
            // Must not disturb the stack or throw into the engine.
            Logging.Error("seeding a script environment failed", ex);
        }
    }

    /// <summary>
    /// Runs for every file the game opens for reading, on whichever thread is
    /// opening it. Must be fast and must never throw: returning 0 simply leaves
    /// the engine reading its own file.
    /// </summary>
    [ThreadStatic]
    private static bool _resolvingAsset;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int OnFileOpen(nint pathPtr, nint outPtr, int outChars)
    {
        // Reading the original file to transform it opens a file itself, which
        // comes straight back through this hook. Without this guard that
        // recursion took the process down as soon as the hook covered the
        // modules .NET uses for file IO.
        if (_resolvingAsset)
            return 0;

        try
        {
            _resolvingAsset = true;
            string? path = Marshal.PtrToStringUni(pathPtr);
            if (string.IsNullOrEmpty(path))
                return 0;

            string? replacement = AssetPatcher.Resolve(path);
            if (replacement is null || replacement.Length + 1 > outChars)
                return 0;

            fixed (char* source = replacement)
                Buffer.MemoryCopy(source, (void*)outPtr, outChars * 2, (replacement.Length + 1) * 2);

            ((char*)outPtr)[replacement.Length] = ' ';
            return 1;
        }
        catch
        {
            return 0;
        }
        finally
        {
            _resolvingAsset = false;
        }
    }

    /// <summary>Stack-neutral test for a populated global table.</summary>
    private static bool HasGlobalTable(nint L, string name)
    {
        LuaNative.lua_getglobal(L, name);
        bool present = LuaNative.lua_istable(L, -1);
        LuaNative.lua_pop(L, 1);
        return present;
    }

    /// <summary>
    /// Repeats the splash through the game's own logger so it lands in
    /// Logs/game-*.log next to everything else, not just our console.
    /// </summary>
    private static void EchoSplashToGameLog(LuaState lua)
    {
        var chunk = new StringBuilder();
        chunk.AppendLine("local sink = (sm and sm.log and sm.log.info) or print");
        chunk.AppendLine("if not sink then return 'no sink' end");

        foreach (string line in _splash)
            chunk.AppendLine($"sink([[{line}]])");

        // DoString runs under lua_pcall, so a missing sm.log cannot fault the VM.
        string? error = lua.DoString(chunk.ToString());
        Logging.Write(error is null
            ? "splash echoed to the game log"
            : $"splash echo failed: {error}");
    }
}
