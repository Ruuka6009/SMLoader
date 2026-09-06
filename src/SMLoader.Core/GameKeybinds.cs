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
    private static bool _loaded;
    private static readonly Lock Gate = new();

    public static int Get(string action, int fallback)
    {
        lock (Gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                Load();
            }

            return Bindings.TryGetValue(action, out int key) ? key : fallback;
        }
    }

    private static void Load()
    {
        string? path = FindKeybindsFile();
        if (path is null)
        {
            Logging.Write("no keybinds.json found; mods will fall back to default keys");
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("KeyBinds", out JsonElement binds))
                return;

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

            Logging.Write($"read {Bindings.Count} keybinds from {path}");
        }
        catch (Exception ex)
        {
            Logging.Error($"could not read {path}", ex);
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
