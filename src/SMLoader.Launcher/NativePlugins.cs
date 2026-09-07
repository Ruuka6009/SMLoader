using SMLoader.Core;

namespace SMLoader.Launcher;

/// <summary>
/// Decides which native DLLs under <c>Mods/</c> the shim is allowed to map, and
/// publishes them to the game process through its environment.
/// </summary>
/// <remarks>
/// The decision belongs here rather than in the shim because everything it
/// needs - PE headers, version resources, <c>Mods/allowed.json</c> - is trivial
/// in managed code and painful in C++ running inside someone else's process.
/// The shim's half is one <c>LoadLibraryW</c> per path, as early as the process
/// allows.
///
/// Opt-in, because a native plugin is not a mod SMLoader controls: it hooks the
/// renderer, it stays loaded for the session, and a player who launches Scrap
/// Mechanic normally should get exactly the game they had before.
///
/// Independent of <c>--no-mods</c> on purpose. "ReShade on, managed mods off" is
/// the launch that says whether a problem is the plugin or the loader, and a
/// safe mode that cannot express it is a safe mode that cannot be used to
/// bisect anything. Both are off unless asked for, so the default is unchanged.
/// </remarks>
internal static class NativePlugins
{
    /// <summary>Semicolon-separated absolute paths, read by the shim's plugin thread.</summary>
    public const string ListVariable = "SMLOADER_NATIVE_PLUGINS";

    /// <summary>
    /// Set whenever nothing is to be loaded, so the shim refuses even if a list
    /// is somehow present. Two statements of the same decision, because the one
    /// that carries the paths is also the one an environment could have set.
    /// </summary>
    public const string RefusalVariable = "SMLOADER_NO_NATIVE";

    /// <summary>Where ReShade resolves its config, log, shaders and screenshots.</summary>
    private const string ReShadeBasePathVariable = "RESHADE_BASE_PATH_OVERRIDE";

    public static IReadOnlyList<NativePluginInfo> Publish(string modsDirectory, bool enabled, bool ignoreAllowList)
    {
        // Authoritative. Whatever happened to be in the shell's environment does
        // not get to decide what runs as native code inside the game.
        Environment.SetEnvironmentVariable(ListVariable, null);
        Environment.SetEnvironmentVariable(RefusalVariable, "1");

        if (!enabled)
        {
            // Still scanned, so "ReShade is sitting here switched off" is
            // something the launcher says rather than something the player has
            // to deduce from a game that looks unchanged.
            foreach (NativePluginInfo idle in NativePluginScan.Discover(modsDirectory, _ => { }))
                Console.WriteLine($"Native: {idle.Describe()} found, not enabled (pass --reshade)");

            return Array.Empty<NativePluginInfo>();
        }

        var notes = new List<string>();
        List<NativePluginInfo> discovered = NativePluginScan.Discover(modsDirectory, notes.Add);

        foreach (string note in notes)
            Console.Error.WriteLine($"Native: {note}");

        if (discovered.Count == 0)
        {
            Console.Error.WriteLine($"Native: --reshade was passed, but no native plugin was " +
                                    $"found under {modsDirectory}");
            return Array.Empty<NativePluginInfo>();
        }

        List<NativePluginInfo> approved = ApplyAllowList(modsDirectory, discovered, ignoreAllowList);
        if (approved.Count == 0)
            return approved;

        Environment.SetEnvironmentVariable(ListVariable, string.Join(';', approved.Select(p => p.Path)));
        Environment.SetEnvironmentVariable(RefusalVariable, null);
        PointReShadeAtItsOwnFolder(approved);

        foreach (NativePluginInfo plugin in approved)
            Console.WriteLine($"Native: {plugin.Describe()} - {plugin.Path}");

        return approved;
    }

    private static List<NativePluginInfo> ApplyAllowList(
        string modsDirectory, List<NativePluginInfo> discovered, bool ignoreAllowList)
    {
        if (ignoreAllowList)
        {
            Console.WriteLine("Native: --any-mod, so the allowlist is ignored for this launch");
            return discovered;
        }

        // The same Mods/allowed.json the managed mods are checked against. A
        // native plugin is loaded earlier and with fewer questions asked than
        // any mod, so it is the last thing that should get to skip the check.
        ModAllowList? allowList = ModAllowList.Load(modsDirectory, out string? failure);
        if (failure is not null)
            Console.Error.WriteLine($"Native: {failure}; no native plugin will be loaded");

        if (allowList is null)
            return discovered;

        var approved = new List<NativePluginInfo>();
        foreach (NativePluginInfo plugin in discovered)
        {
            if (allowList.IsAllowed(plugin.Path, out string reason))
                approved.Add(plugin);
            else
                Console.Error.WriteLine($"Native: {reason}; not loading it");
        }

        return approved;
    }

    /// <summary>
    /// ReShade resolves <c>ReShade.ini</c>, <c>ReShade.log</c>, its shader and
    /// texture search paths, its presets and its screenshots against a single
    /// base path. Naming it explicitly is what keeps every one of those inside
    /// <c>Mods/&lt;name&gt;/</c>: SMLoader's whole premise is that the Steam
    /// install is left alone, and a post-processing injector scattering its
    /// config and screenshots next to ScrapMechanic.exe would be the one thing
    /// that broke it.
    /// </summary>
    /// <remarks>
    /// Honoured by recent ReShade builds. On one that does not know the
    /// variable it is simply unused, and ReShade's own default - the folder its
    /// DLL is in, which is the same folder - already gives the same answer.
    /// Either way nothing is written into the game directory.
    /// </remarks>
    private static void PointReShadeAtItsOwnFolder(IEnumerable<NativePluginInfo> plugins)
    {
        NativePluginInfo? reshade = plugins.FirstOrDefault(p => p.IsReShade);
        if (reshade is null)
            return;

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ReShadeBasePathVariable)))
        {
            Console.WriteLine($"Native: {ReShadeBasePathVariable} is already set; leaving it alone");
            return;
        }

        Environment.SetEnvironmentVariable(
            ReShadeBasePathVariable, reshade.Directory + Path.DirectorySeparatorChar);

        Console.WriteLine($"Native: ReShade keeps its config, shaders, presets and screenshots " +
                          $"in {reshade.Directory}");
    }
}
