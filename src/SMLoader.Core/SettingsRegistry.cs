using SMLoader.Api;
using SMLoader.Api.Lua;

namespace SMLoader.Core;

/// <summary>
/// Every setting declared by every loaded mod, in one list. This is what the
/// in-game settings panel enumerates, and it is exposed to Lua so the panel can
/// live in game script where the GUI API is.
/// </summary>
internal static class SettingsRegistry
{
    private static readonly List<ModSettingEntry> Entries = new();
    private static readonly Lock Gate = new();

    public static void Register(ModSettingEntry entry)
    {
        lock (Gate)
        {
            // Re-declaring replaces the presentation but keeps the stored value,
            // so a mod reloading does not duplicate rows.
            int existing = Entries.FindIndex(e =>
                e.ModName == entry.ModName &&
                string.Equals(e.Definition.Key, entry.Definition.Key, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
                Entries[existing] = entry;
            else
                Entries.Add(entry);
        }
    }

    public static IReadOnlyList<ModSettingEntry> All
    {
        get
        {
            lock (Gate)
                return Entries.ToArray();
        }
    }

    /// <summary>
    /// Publishes the registry to Lua as part of the shared <c>smloader</c>
    /// table, so an in-game panel can list and edit settings without each mod
    /// needing its own bridge.
    /// </summary>
    public static void PublishToLua()
    {
        LuaApi.Add("SMLoader", "settingCount", lua =>
        {
            lua.Push((long)All.Count);
            return 1;
        });

        // Returns: mod, key, label, kind, value, description
        LuaApi.Add("SMLoader", "settingAt", lua =>
        {
            int index = (int)lua.ToInteger(1) - 1; // Lua is 1-based
            IReadOnlyList<ModSettingEntry> all = All;

            if (index < 0 || index >= all.Count)
            {
                lua.PushNil();
                return 1;
            }

            ModSettingEntry entry = all[index];
            lua.Push(entry.ModName);
            lua.Push(entry.Definition.Key);
            lua.Push(entry.Definition.Label);
            lua.Push(entry.Definition.Kind.ToString().ToLowerInvariant());
            lua.Push(Describe(entry));
            lua.Push(entry.Definition.Description ?? string.Empty);
            return 6;
        });

        LuaApi.Add("SMLoader", "settingSet", lua =>
        {
            int index = (int)lua.ToInteger(1) - 1;
            IReadOnlyList<ModSettingEntry> all = All;

            if (index < 0 || index >= all.Count)
            {
                lua.Push(false);
                return 1;
            }

            ModSettingEntry entry = all[index];
            try
            {
                entry.Value = entry.Definition.Kind switch
                {
                    SettingKind.Toggle => lua.ToBoolean(2),
                    SettingKind.Key => (int)lua.ToInteger(2),
                    SettingKind.Number => lua.ToNumber(2),
                    _ => lua.ToStringValue(2) ?? string.Empty,
                };
                lua.Push(true);
            }
            catch (Exception ex)
            {
                Logging.Error($"could not set {entry.ModName}.{entry.Definition.Key}", ex);
                lua.Push(false);
            }

            return 1;
        });

        // Lets loader-injected Lua report into the loader log, which the panel
        // needs since its errors are swallowed by a pcall.
        // Lets the counters be read from in-game rather than only from the log,
        // which is the difference between checking a claim and filing a bug.
        LuaApi.Add("SMLoader", "stats", lua =>
        {
            lua.Push(Metrics.Report());
            return 1;
        });

        LuaApi.Add("SMLoader", "logMessage", lua =>
        {
            Logging.Write("[panel] " + (lua.ToStringValue(1) ?? "(nil)"));
            return 0;
        });

        // Returns the first key currently held, so the panel can rebind by
        // "press any key" rather than making the player type a number.
        LuaApi.Add("SMLoader", "pollAnyKey", lua =>
        {
            lua.Push((long)InputScanner.FirstPressedKey());
            return 1;
        });

        // Friendly name for a virtual-key code, so the panel can show "F2"
        // rather than 113.
        LuaApi.Add("SMLoader", "keyName", lua =>
        {
            lua.Push(KeyNames.Describe((int)lua.ToInteger(1)));
            return 1;
        });
    }

    private static string Describe(ModSettingEntry entry)
    {
        object value = entry.Value;
        return entry.Definition.Kind switch
        {
            SettingKind.Key => KeyNames.Describe(Convert.ToInt32(value)),
            SettingKind.Toggle => Convert.ToBoolean(value) ? "On" : "Off",
            SettingKind.Number => Convert.ToDouble(value).ToString("0.###"),
            _ => Convert.ToString(value) ?? string.Empty,
        };
    }
}
