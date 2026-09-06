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

    private static readonly List<Registration> Registrations = new();
    private static readonly ConcurrentDictionary<string, string?> Resolved = new(StringComparer.OrdinalIgnoreCase);
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

        return Resolved.GetOrAdd(path, Build);
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
