using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SMLoader.Api;

namespace SMLoader.Core;

/// <summary>
/// Rewrites game data files - GUI layouts and the like - on their way to being
/// opened. The original is never modified: the rewritten copy is written into
/// the loader's own cache and the engine is pointed at that instead.
/// </summary>
/// <remarks>
/// This is the non-Lua counterpart to <see cref="ScriptPatcher"/>. It is driven
/// by a CreateFileW hook, so it sees every read the game performs; results are
/// cached per path, including misses, because that hook is on a very hot path.
/// </remarks>
internal static class AssetPatcher
{
    private readonly record struct Registration(string ModName, string PathContains, Func<string, string> Transform);

    /// <summary>
    /// Above this many cached paths the misses are dropped. Once any mod
    /// registers a transform, every path the process ever opens earns an
    /// entry - worlds streaming in and out over a long session are tens of
    /// thousands of strings, held for the life of the process.
    /// </summary>
    private const int MaxCachedPaths = 4096;

    private static readonly List<Registration> Registrations = new();

    // Lazy, so two threads opening the same asset at once cannot both run the
    // transform and both write the same cache file. GetOrAdd makes no promise
    // that its factory runs once; the Lazy does.
    private static readonly ConcurrentDictionary<string, Lazy<string?>> Resolved =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();
    private static string _cacheDirectory = "Cache";

    public static void Initialize(string rootDirectory)
    {
        _cacheDirectory = Path.Combine(rootDirectory, "Cache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public static void Register(string modName, string pathContains, Func<string, string> transform)
    {
        lock (Gate)
        {
            Registrations.Add(new Registration(modName, pathContains, transform));
            Resolved.Clear(); // a new rule may match paths already judged a miss
        }

        Logging.Write($"[{modName}] will patch assets matching '{pathContains}'");
    }

    /// <summary>
    /// Returns the path the engine should open instead, or null to leave the
    /// open alone.
    /// </summary>
    public static string? Resolve(string path)
    {
        if (Registrations.Count == 0)
            return null;

        if (Resolved.Count >= MaxCachedPaths)
            Trim();

        return Resolved.GetOrAdd(path, static p =>
            new Lazy<string?>(() => Build(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// Drops the misses and keeps the hits. A miss costs a handful of string
    /// comparisons to recompute; a hit costs a file read, every mod's transform
    /// and a cache write, and there are only ever as many hits as the mods
    /// actually patch.
    /// </summary>
    private static void Trim()
    {
        lock (Gate)
        {
            if (Resolved.Count < MaxCachedPaths)
                return;

            int before = Resolved.Count;
            foreach (KeyValuePair<string, Lazy<string?>> entry in Resolved)
            {
                // Not yet evaluated means another thread is inside Build for it;
                // dropping the entry is safe, that thread holds its own reference.
                if (!entry.Value.IsValueCreated || entry.Value.Value is null)
                    Resolved.TryRemove(entry.Key, out _);
            }

            // Pathological only: more genuine hits than the cap.
            if (Resolved.Count >= MaxCachedPaths)
                Resolved.Clear();

            Logging.Write($"asset cache trimmed from {before} to {Resolved.Count} entries");
        }
    }

    private static string? Build(string path)
    {
        Registration[] matching;
        lock (Gate)
        {
            matching = Registrations
                .Where(r => path.Contains(r.PathContains, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (matching.Length == 0)
            return null;

        try
        {
            if (!File.Exists(path))
                return null;

            string original = File.ReadAllText(path);
            string current = original;

            foreach (Registration registration in matching)
            {
                try
                {
                    current = registration.Transform(current);
                }
                catch (Exception ex)
                {
                    // A broken transform must leave the game with its own file.
                    Logging.Error($"[{registration.ModName}] asset transform of {path} failed", ex);
                }
            }

            if (current == original)
                return null;

            string cached = CachePathFor(path);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.WriteAllText(cached, current, new UTF8Encoding(false));

            Logging.Write($"patched asset {Path.GetFileName(path)} " +
                          $"({original.Length} -> {current.Length} chars) -> {cached}");
            return cached;
        }
        catch (Exception ex)
        {
            Logging.Error($"could not patch asset {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// Cache path that keeps the ORIGINAL file name, in a per-path subfolder.
    /// MyGUI identifies layouts by file name, so handing it a renamed copy made
    /// it register a second set of widgets on top of the first rather than
    /// replacing them - which is what drew two captions in the same tab.
    /// </summary>
    private static string CachePathFor(string path)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Path.Combine(_cacheDirectory, Convert.ToHexString(hash)[..8], Path.GetFileName(path));
    }
}
