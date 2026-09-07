namespace SMLoader.Core;

/// <summary>How the shim's LoadLibrary of one native plugin went.</summary>
/// <param name="Info">Name, version and add-on count, read from the file.</param>
/// <param name="Failure">Null when it loaded; otherwise why it did not.</param>
internal sealed record NativePluginStatus(NativePluginInfo Info, string? Failure)
{
    public string Describe()
        => Failure is null ? Info.Describe() : $"{Info.Describe()} - NOT LOADED, {Failure}";
}

/// <summary>
/// The managed side's view of the native DLLs the shim mapped before the CLR
/// existed. It does not load anything: by the time this runs, the plugins are
/// either in the process or they failed, and all that is left is to say which.
/// </summary>
/// <remarks>
/// The report arrives through <c>BootContext.nativePlugins</c> as one
/// <c>&lt;status&gt;|&lt;path&gt;</c> line per plugin the launcher published -
/// see <c>native_plugins.cpp</c>. Statuses are <c>ok</c>, <c>err &lt;code&gt;</c>
/// and <c>other &lt;path&gt;</c>.
/// </remarks>
internal static class NativePlugins
{
    private static NativePluginStatus[] _plugins = Array.Empty<NativePluginStatus>();

    internal static IReadOnlyList<NativePluginStatus> All => _plugins;

    /// <summary>Parses the shim's report and logs one line per plugin.</summary>
    internal static void Initialize(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            _plugins = Array.Empty<NativePluginStatus>();
            return;
        }

        var parsed = new List<NativePluginStatus>();

        foreach (string line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                                   StringSplitOptions.TrimEntries))
        {
            // Split on the first bar only: a status can carry a path of its
            // own ("other C:\Windows\System32\dxgi.dll"), and Windows paths
            // never contain a bar.
            int bar = line.IndexOf('|');
            if (bar < 0)
            {
                Logging.Warn($"native plugin report line not understood: {line}");
                continue;
            }

            string status = line[..bar];
            string path = line[(bar + 1)..];
            if (path.Length == 0)
                continue;

            var entry = new NativePluginStatus(NativePluginScan.Inspect(path), Explain(status));
            parsed.Add(entry);

            if (entry.Failure is null)
                Logging.Write($"native plugin {entry.Info.Describe()} loaded from {path}");
            else
                Logging.Error($"native plugin {path} did not load: {entry.Failure}");
        }

        _plugins = parsed.ToArray();
    }

    /// <summary>
    /// True when this mod folder is a native plugin the shim already mapped, so
    /// <see cref="ModLoader"/> leaves it alone instead of trying to read a
    /// 5 MB DLL as a .NET assembly.
    /// </summary>
    internal static bool OwnsDirectory(string directory)
    {
        if (_plugins.Length == 0)
            return false;

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or IOException)
        {
            return false;
        }

        return _plugins.Any(p => string.Equals(
            Path.TrimEndingDirectorySeparator(p.Info.Directory), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The splash row, or null when there is nothing to say. Failures are shown
    /// rather than hidden: a splash claiming ReShade is running when it is not
    /// sends the player looking in entirely the wrong place.
    /// </summary>
    internal static string? Describe()
        => _plugins.Length == 0 ? null : string.Join(", ", _plugins.Select(p => p.Describe()));

    private static string? Explain(string status)
    {
        if (string.Equals(status, "ok", StringComparison.Ordinal))
            return null;

        if (status.StartsWith("err ", StringComparison.Ordinal))
            return $"LoadLibrary failed with Windows error {status[4..]}";

        // The Windows loader can hand back a module that is already mapped
        // under the same file name - System32\dxgi.dll, say - instead of the
        // file we asked for, in which case the plugin never ran.
        if (status.StartsWith("other ", StringComparison.Ordinal))
            return $"{status[6..]} was already mapped under that name; rename the DLL";

        return $"the shim reported '{status}'";
    }
}
