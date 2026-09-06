namespace SMLoader.Api;

/// <summary>
/// The version of <c>SMLoader.Api</c> a mod assembly was built against. Emitted
/// automatically for mods that import SMLoader's props file, and checked by the
/// loader before a mod is constructed.
/// </summary>
/// <remarks>
/// A string rather than the two integers, because MSBuild's
/// <c>&lt;AssemblyAttribute&gt;</c> item passes literals as strings and typing
/// them back to <c>int</c> is fragile across SDK versions. The parse lives in
/// <see cref="ApiVersion.TryParse"/> and is covered by tests.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SMLoaderApiVersionAttribute : Attribute
{
    public SMLoaderApiVersionAttribute(string version) => Version = version;

    /// <summary>The targeted version, as <c>major.minor</c>.</summary>
    public string Version { get; }
}

/// <summary>
/// The API version this loader provides, and the rules for whether a mod built
/// against a different one may load.
/// </summary>
/// <remarks>
/// Without this, a mod compiled against an API that has since lost a member
/// fails with <see cref="MissingMethodException"/> at an arbitrary later moment
/// - typically inside a Lua callback, where the trampoline catches it and the
/// player sees a feature quietly not working.
/// <para>
/// <b>2.0</b> because 0.1.0's implicit 1.0 has already been broken twice:
/// <c>IMemory.Unprotect</c> became <c>TryUnprotect</c> with a bool return, and
/// <c>LuaState</c> became a readonly struct.
/// </para>
/// </remarks>
public static class ApiVersion
{
    /// <summary>Incompatible when it differs: members have been removed or changed shape.</summary>
    public const int Major = 2;

    /// <summary>Additive. A mod targeting a lower minor is fine; a higher one is not.</summary>
    public const int Minor = 0;

    public static string Text => $"{Major}.{Minor}";

    /// <summary>
    /// Parses <c>major.minor</c>. Returns false for anything else rather than
    /// guessing, since a version that cannot be read is not a version.
    /// </summary>
    public static bool TryParse(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        if (string.IsNullOrWhiteSpace(version))
            return false;

        string[] parts = version.Split('.');
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], out major) &&
               int.TryParse(parts[1], out minor) &&
               major >= 0 && minor >= 0;
    }

    /// <summary>
    /// True when a mod targeting <paramref name="version"/> can run against this
    /// loader. <paramref name="reason"/> explains a refusal in the terms a mod
    /// author needs.
    /// </summary>
    public static bool IsCompatible(string? version, out string reason)
    {
        if (!TryParse(version, out int major, out int minor))
        {
            reason = $"'{version}' is not a major.minor API version";
            return false;
        }

        if (major != Major)
        {
            reason = $"targets SMLoader API {major}.{minor}, this loader provides {Text}";
            return false;
        }

        if (minor > Minor)
        {
            reason = $"needs SMLoader API {major}.{minor}, this loader provides {Text}";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
