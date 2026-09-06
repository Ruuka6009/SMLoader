using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SMLoader.Api;

namespace SMLoader.Core;

/// <summary>
/// Holds the in-memory rewrites mods have registered and applies them as the
/// engine compiles each script.
/// </summary>
internal static class ScriptPatcher
{
    private readonly record struct Registration(string ModName, string PathContains, Action<ScriptLoadContext> Patch);

    /// <summary>
    /// Registrations are append-only, so they are published as a whole array and
    /// read without a lock. This is on the path of every chunk the engine
    /// compiles, most of which no mod cares about.
    /// </summary>
    private static Registration[] _registrations = Array.Empty<Registration>();

    /// <summary>
    /// Patched output keyed by chunk name and a hash of the incoming bytes. A
    /// world load compiles the same scripts for each of its ~10 states, and
    /// rebuilding meant a full UTF-8 decode, every mod's transform and a re-encode
    /// each time, on the world-load critical path.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Name, ulong Hash), byte[]?> Cache = new();

    /// <summary>Chunk names no mod matched, logged once each so mod authors can find them.</summary>
    private static readonly ConcurrentDictionary<string, byte> UnmatchedSeen = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxCacheEntries = 512;
    private const int MaxUnmatchedLogged = 256;

    private static readonly Lock Gate = new();

    public static IDisposable Register(string modName, string pathContains,
                                       Action<ScriptLoadContext> patch)
    {
        var registration = new Registration(modName, pathContains, patch);

        lock (Gate)
        {
            Registration[] updated = new Registration[_registrations.Length + 1];
            Array.Copy(_registrations, updated, _registrations.Length);
            updated[^1] = registration;

            Volatile.Write(ref _registrations, updated);

            // Anything already built predates this registration.
            Cache.Clear();
        }

        Logging.Write($"[{modName}] will patch scripts matching '{pathContains}'");
        return new Handle(registration);
    }

    private static void Unregister(Registration registration)
    {
        lock (Gate)
        {
            Registration[] current = _registrations;
            int index = Array.IndexOf(current, registration);
            if (index < 0)
                return;

            Registration[] updated = new Registration[current.Length - 1];
            Array.Copy(current, 0, updated, 0, index);
            Array.Copy(current, index + 1, updated, index, current.Length - index - 1);

            Volatile.Write(ref _registrations, updated);

            // Every cached result was built with this registration in it.
            Cache.Clear();
        }

        Logging.Write($"[{registration.ModName}] no longer patches scripts matching " +
                      $"'{registration.PathContains}'");
    }

    /// <summary>Idempotent: disposing twice must not remove someone else's entry.</summary>
    private sealed class Handle : IDisposable
    {
        private Registration? _registration;

        public Handle(Registration registration) => _registration = registration;

        public void Dispose()
        {
            Registration? registration = _registration;
            _registration = null;

            if (registration is not null)
                Unregister(registration.Value);
        }
    }

    /// <summary>True when any mod wants to see this chunk. Kept cheap: the
    /// source is only decoded once a name actually matches.</summary>
    public static bool WantsScript(string name)
    {
        Registration[] registrations = Volatile.Read(ref _registrations);

        // One field read when nobody has registered anything - the same early-out
        // AssetPatcher.Resolve uses, and for the same reason.
        if (registrations.Length == 0)
            return false;

        foreach (Registration registration in registrations)
        {
            if (name.Contains(registration.PathContains, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        NoteUnmatched(name);
        return false;
    }

    /// <summary>
    /// Returns the rewritten chunk as UTF-8, or null to leave the engine
    /// compiling its own bytes.
    /// </summary>
    public static byte[]? Apply(string name, ReadOnlySpan<byte> source)
    {
        Registration[] registrations = Volatile.Read(ref _registrations);
        if (registrations.Length == 0)
            return null;

        var key = (name, Hash(source));
        if (Cache.TryGetValue(key, out byte[]? cached))
        {
            Metrics.ScriptCompile(cacheHit: true);
            return cached;
        }

        Metrics.ScriptCompile(cacheHit: false);

        long started = Stopwatch.GetTimestamp();
        byte[]? built = Build(name, source, registrations);
        Metrics.ScriptTransformTime(Stopwatch.GetTimestamp() - started);

        if (Cache.Count >= MaxCacheEntries)
            Cache.Clear();

        Cache[key] = built;
        return built;
    }

    private static byte[]? Build(string name, ReadOnlySpan<byte> source, Registration[] registrations)
    {
        var context = new ScriptLoadContext(name, Encoding.UTF8.GetString(source));

        foreach (Registration registration in registrations)
        {
            if (!name.Contains(registration.PathContains, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                int revision = context.Revision;
                int before = context.Length;

                registration.Patch(context);

                // Comparing revisions rather than the text itself: reading Source
                // to compare would flatten the builder between every mod's turn,
                // which is the concatenation cost this was meant to remove.
                if (context.Revision != revision)
                {
                    Logging.Write($"[{registration.ModName}] patched {name} " +
                                  $"({before} -> {context.Length} chars)");
                }
            }
            catch (Exception ex)
            {
                // A broken transform must not stop the game from loading.
                Logging.Error($"[{registration.ModName}] transform of {name} failed", ex);
            }
        }

        if (context.Revision == 0)
            return null;

        byte[] patched = Encoding.UTF8.GetBytes(context.Source);

        // A mod that rewrote the chunk back to what it already was gets the same
        // "compile your own bytes" answer as one that did nothing.
        return patched.AsSpan().SequenceEqual(source) ? null : patched;
    }

    /// <summary>
    /// FNV-1a. Not a security hash - it identifies one build of one script, and
    /// the chunk name is part of the key. Chosen over System.IO.Hashing to avoid
    /// putting another assembly next to the loader for this.
    /// </summary>
    private static ulong Hash(ReadOnlySpan<byte> data)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offsetBasis;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= prime;
        }

        // Length too, so a truncated read cannot collide with the whole file.
        return hash ^ (ulong)data.Length;
    }

    private static void NoteUnmatched(string name)
    {
        // Bounded: this used to be an unbounded HashSet fed by every chunk the
        // engine ever compiled. Past the cap the names stop being recorded, which
        // costs nothing but the log line.
        if (UnmatchedSeen.Count >= MaxUnmatchedLogged)
            return;

        // Trace: dozens of these per world load, and they exist for a mod
        // author hunting a chunk name, not for a player sending in a log.
        if (UnmatchedSeen.TryAdd(name, 0))
            Logging.Trace($"script: {name}");
    }
}
