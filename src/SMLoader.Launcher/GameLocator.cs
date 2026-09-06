using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SMLoader.Launcher;

/// <summary>Finds ScrapMechanic.exe by walking Steam's library configuration.</summary>
internal static partial class GameLocator
{
    private const string AppId = "387990";
    private const string RelativeExe = @"steamapps\common\Scrap Mechanic\Release\ScrapMechanic.exe";

    public static string? Locate()
    {
        foreach (string library in SteamLibraries())
        {
            string candidate = Path.Combine(library, RelativeExe);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        string? steamRoot = SteamRoot();
        if (steamRoot is null)
            yield break;

        yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            yield break;

        // Entries look like:  "path"  "D:\SteamLibrary"
        foreach (Match match in PathEntry().Matches(File.ReadAllText(vdf)))
        {
            string path = match.Groups[1].Value.Replace(@"\\", @"\");
            if (path.Length > 0)
                yield return path;
        }
    }

    private static string? SteamRoot()
    {
        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string subKey in new[] { @"SOFTWARE\Valve\Steam", @"SOFTWARE\WOW6432Node\Valve\Steam" })
            {
                using RegistryKey? key = root.OpenSubKey(subKey);
                if (key?.GetValue("SteamPath") is string p && p.Length > 0)
                    return p.Replace('/', '\\');
                if (key?.GetValue("InstallPath") is string i && i.Length > 0)
                    return i.Replace('/', '\\');
            }
        }

        string fallback = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(fallback) ? fallback : null;
    }

    [GeneratedRegex(@"""path""\s+""([^""]+)""")]
    private static partial Regex PathEntry();
}
