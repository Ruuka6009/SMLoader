using System.Text.Json;

namespace SMLoader.Core;

/// <summary>
/// Reads the player's own bindings from Scrap Mechanic's keybinds.json, so mods
/// can follow the keyboard layout the player actually uses instead of assuming
/// QWERTY. The file is read, never written.
/// </summary>
/// <remarks>
/// Layout is <c>{ "KeyBinds": { "C_Forward": [ { "K": 90 } ] } }</c>, where
/// <c>K</c> is a Windows virtual-key code and <c>MB</c> a mouse button.
/// </remarks>
internal static class GameKeybinds
{
    private static readonly Dictionary<string, int> Bindings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>Set once bindings were actually read, not merely attempted.</summary>
    private static bool _loaded;

    /// <summary>
    /// When the last attempt was. Setting a flag before calling Load meant a
    /// transient failure - the game rewriting the file at that instant - was
    /// cached for the whole session. Doubles as the re-read interval, so
    /// rebinding a key mid-session is picked up without a restart.
    /// </summary>
    private static long _lastAttempt = long.MinValue;

    private const long RetryMs = 10_000;

    public static int Get(string action, int fallback)
    {
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (now - _lastAttempt >= RetryMs)
            {
                _lastAttempt = now;

                // Only announce the first success. After that this is a re-read
                // every RetryMs and saying so each time would be noise.
                if (Load() && !_loaded)
                {
                    _loaded = true;
                    Logging.Write($"read {Bindings.Count} keybinds");
                }
            }

            return Bindings.TryGetValue(action, out int key) ? key : fallback;
        }
    }

    /// <summary>
    /// Reads the file into <see cref="Bindings"/>. Returns false on any failure,
    /// leaving whatever was already read in place - a transient failure must not
    /// replace good bindings with none.
    /// </summary>
    private static bool Load()
    {
        string? path = FindKeybindsFile();
        if (path is null)
        {
            if (!_loaded)
                Logging.Write("no keybinds.json found; mods will fall back to default keys");
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("KeyBinds", out JsonElement binds))
                return false;

            foreach (JsonProperty action in binds.EnumerateObject())
            {
                if (action.Value.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement entry in action.Value.EnumerateArray())
                {
                    // Mouse buttons carry "MB" instead and have no virtual-key
                    // code, so they are skipped rather than mapped wrongly.
                    if (entry.TryGetProperty("K", out JsonElement key) &&
                        key.TryGetInt32(out int code))
                    {
                        Bindings[action.Name] = code;
                        break;
                    }
                }
            }

            Logging.Debug($"read {Bindings.Count} keybinds from {path}");
            return true;
        }
        catch (Exception ex)
        {
            // Once only: this retries now, and a locked file would otherwise
            // fill the log with the same line every ten seconds.
            if (!_loaded)
                Logging.Error($"could not read {path}", ex);
            return false;
        }
    }

    /// <summary>
    /// Picks the most recently written profile: a machine can hold several
    /// User_&lt;steamid&gt; folders and only one is the account in use.
    /// </summary>
    private static string? FindKeybindsFile()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Axolot Games", "Scrap Mechanic", "User");

        if (!Directory.Exists(root))
            return null;

        return Directory.EnumerateDirectories(root)
            .Select(directory => Path.Combine(directory, "keybinds.json"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
