using SMLoader.Api;

namespace SMLoader.Core;

/// <summary>
/// Holds the in-memory rewrites mods have registered and applies them as the
/// engine compiles each script.
/// </summary>
internal static class ScriptPatcher
{
    private readonly record struct Registration(string ModName, string PathContains, Action<ScriptLoadContext> Patch);

    private static readonly List<Registration> Registrations = new();
    private static readonly HashSet<string> SeenScripts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    public static void Register(string modName, string pathContains, Action<ScriptLoadContext> patch)
    {
        lock (Gate)
            Registrations.Add(new Registration(modName, pathContains, patch));

        Logging.Write($"[{modName}] will patch scripts matching '{pathContains}'");
    }

    /// <summary>True when any mod wants to see this chunk. Kept cheap: the
    /// source is only decoded once a name actually matches.</summary>
    public static bool WantsScript(string name)
    {
        lock (Gate)
        {
            if (SeenScripts.Add(name))
                Logging.Write($"script: {name}");

            foreach (Registration registration in Registrations)
            {
                if (name.Contains(registration.PathContains, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Returns the rewritten source, or null if nothing changed.</summary>
    public static string? Apply(string name, string source)
    {
        Registration[] matching;
        lock (Gate)
        {
            matching = Registrations
                .Where(r => name.Contains(r.PathContains, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (matching.Length == 0)
            return null;

        var context = new ScriptLoadContext(name, source);
        bool changed = false;

        foreach (Registration registration in matching)
        {
            try
            {
                string before = context.Source;
                registration.Patch(context);

                if (!ReferenceEquals(before, context.Source) && before != context.Source)
                {
                    changed = true;
                    Logging.Write($"[{registration.ModName}] patched {name} " +
                                  $"({before.Length} -> {context.Source.Length} chars)");
                }
            }
            catch (Exception ex)
            {
                // A broken transform must not stop the game from loading.
                Logging.Error($"[{registration.ModName}] transform of {name} failed", ex);
            }
        }

        return changed ? context.Source : null;
    }
}
