using System.Security.Cryptography;
using System.Text.Json;

namespace SMLoader.Core;

/// <summary>
/// The optional SHA-256 allowlist at <c>Mods/allowed.json</c>, shaped
/// <c>{ "NoclipMod.dll": "&lt;hex&gt;" }</c>. Absent means every mod loads,
/// which is the default and matches what the loader has always done.
/// </summary>
/// <remarks>
/// Compiled into SMLoader.Core, which vets managed mods once the CLR is up, and
/// linked into SMLoader.Launcher, which has to vet native plugins before the
/// game process even starts. One implementation, because two would drift and
/// the one that drifted would be the one nobody was watching.
///
/// Deliberately free of <c>Logging</c> and of anything else that only
/// exists inside the game process: the launcher compiles this same file.
/// </remarks>
internal sealed class ModAllowList
{
    private readonly Dictionary<string, string> _entries;

    private ModAllowList(Dictionary<string, string> entries) => _entries = entries;

    public int Count => _entries.Count;

    public static string PathFor(string modsDirectory) => Path.Combine(modsDirectory, "allowed.json");

    /// <summary>
    /// Returns null when no allowlist is in force. A file that exists but
    /// cannot be read yields an <em>empty</em> allowlist - which refuses
    /// everything - along with the reason: a corrupt allowlist must not be a
    /// way past the allowlist.
    /// </summary>
    public static ModAllowList? Load(string modsDirectory, out string? failure)
    {
        failure = null;

        string path = PathFor(modsDirectory);
        if (!File.Exists(path))
            return null;

        try
        {
            Dictionary<string, string>? entries =
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));

            // OrdinalIgnoreCase because the keys are Windows file names, and
            // "noclipmod.dll" naming the same file as "NoclipMod.dll" should
            // not be the difference between a mod loading and not.
            return new ModAllowList(new Dictionary<string, string>(
                entries ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or NotSupportedException)
        {
            failure = $"could not read {path}: {ex.Message}";
            return new ModAllowList(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// True when the file at <paramref name="dllPath"/> may load. On false,
    /// <paramref name="reason"/> says why, in a form fit to log verbatim.
    /// </summary>
    public bool IsAllowed(string dllPath, out string reason)
    {
        string name = Path.GetFileName(dllPath);

        if (!_entries.TryGetValue(name, out string? expected))
        {
            reason = $"{name} is not in the allowlist";
            return false;
        }

        string actual;
        try
        {
            using FileStream stream = File.OpenRead(dllPath);
            actual = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"could not hash {dllPath}: {ex.Message}";
            return false;
        }

        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            reason = string.Empty;
            return true;
        }

        reason = $"{name} does not match the allowlist (expected {expected}, found {actual})";
        return false;
    }
}
