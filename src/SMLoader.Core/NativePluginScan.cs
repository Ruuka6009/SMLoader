using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace SMLoader.Core;

/// <summary>What a DLL sitting under <c>Mods/</c> turns out to be.</summary>
internal enum ImageKind
{
    /// <summary>Present but unreadable; leave it to whoever tries to load it.</summary>
    Unreadable,

    /// <summary>Not a PE image at all.</summary>
    NotAnImage,

    /// <summary>A .NET assembly - <c>ModLoader</c>'s business, not the shim's.</summary>
    Managed,

    /// <summary>Native x64 code, which is what the shim can LoadLibrary.</summary>
    Native,

    /// <summary>Native, but not x64. ScrapMechanic.exe is a 64-bit process.</summary>
    NativeWrongBitness,
}

/// <summary>One native DLL found under <c>Mods/</c>, ready to hand to the shim.</summary>
/// <param name="Directory">The mod folder it lives in - also its ReShade base path.</param>
/// <param name="Path">Full path to the DLL itself.</param>
/// <param name="Product">Version-resource product name, or the file name.</param>
/// <param name="Version">Version-resource version, or empty.</param>
/// <param name="AddonCount">ReShade add-ons sitting next to it.</param>
internal sealed record NativePluginInfo(
    string Directory, string Path, string Product, string Version, int AddonCount)
{
    /// <summary>"ReShade 6.8.0 (+22 add-ons)" - the line the splash shows.</summary>
    public string Describe()
    {
        string head = Version.Length == 0 ? Product : Product + " " + Version;
        if (AddonCount == 0)
            return head;

        return $"{head} (+{AddonCount} add-on{(AddonCount == 1 ? string.Empty : "s")})";
    }

    /// <summary>
    /// ReShade resolves its config, log, shaders, presets and screenshots
    /// against a base path. Knowing which plugin is ReShade is what lets the
    /// launcher point that base path at this folder, so none of it lands in the
    /// Steam install.
    /// </summary>
    public bool IsReShade => Product.Contains("ReShade", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds the native DLLs under <c>Mods/</c>. A native plugin has to be mapped
/// before the game creates its graphics device, which is long before the CLR
/// inside the game exists - so the launcher does this scan and the shim is
/// handed the answer rather than working it out for itself.
/// </summary>
/// <remarks>
/// Compiled into SMLoader.Core, where the tests can reach it and where
/// <c>ModLoader</c> needs the same idea of what a managed mod is, and
/// linked into SMLoader.Launcher, which is what actually runs it. Keep it free
/// of anything that only exists inside the game process.
/// </remarks>
internal static class NativePluginScan
{
    /// <summary>ReShade add-on extensions, as they appear next to the DLL.</summary>
    private static readonly string[] AddonExtensions = { ".addon", ".addon64" };

    /// <summary>
    /// Every mod folder whose entry DLL is native code, in the same ordinal
    /// order <c>ModLoader</c> walks them.
    /// </summary>
    /// <param name="note">
    /// Receives one line per folder that was examined and not published. A mod
    /// that silently does nothing is the worst outcome available here.
    /// </param>
    public static List<NativePluginInfo> Discover(string modsDirectory, Action<string> note)
    {
        var found = new List<NativePluginInfo>();
        if (!Directory.Exists(modsDirectory))
            return found;

        List<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(modsDirectory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note($"could not list {modsDirectory}: {ex.Message}");
            return found;
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);

            string? entry = SelectEntryDll(directory, note);
            if (entry is null)
                continue;

            switch (Classify(entry))
            {
                case ImageKind.Native:
                    found.Add(Inspect(entry));
                    break;

                case ImageKind.NativeWrongBitness:
                    note($"{name}: {Path.GetFileName(entry)} is 32-bit; " +
                         "ScrapMechanic.exe is a 64-bit process");
                    break;

                case ImageKind.NotAnImage:
                    note($"{name}: {Path.GetFileName(entry)} is not a Windows DLL");
                    break;

                default:
                    // Managed is the ordinary case - a .NET mod, loaded by
                    // SMLoader.Core once the CLR is up. Unreadable is reported
                    // there too, by whichever loader ends up failing on it.
                    break;
            }
        }

        return found;
    }

    /// <summary>
    /// Which DLL in a mod folder <em>is</em> the mod. In order: the one named
    /// after the folder, the only one there, or the one whose version resource
    /// says it is this mod - which is how <c>Mods/ReShade/dxgi.dll</c> is
    /// recognised even sitting next to a <c>d3dcompiler_47.dll</c>.
    /// </summary>
    public static string? SelectEntryDll(string modDirectory, Action<string> note)
    {
        string name = Path.GetFileName(modDirectory);

        string[] candidates;
        try
        {
            candidates = Directory.EnumerateFiles(modDirectory, "*.dll")
                .Where(p => !Path.GetFileName(p)
                                 .StartsWith("SMLoader.", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note($"{name}: could not be read: {ex.Message}");
            return null;
        }

        if (candidates.Length == 0)
            return null;

        string? byName = candidates.FirstOrDefault(p => string.Equals(
            Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
            return byName;

        if (candidates.Length == 1)
            return candidates[0];

        string[] identified = candidates.Where(p => ProductIs(p, name)).ToArray();
        if (identified.Length == 1)
            return identified[0];

        note($"{name}: {candidates.Length} DLLs here and none called {name}.dll; " +
             $"rename the one you meant to {name}.dll");
        return null;
    }

    /// <summary>
    /// Reads only the PE headers, so classifying a 5 MB ReShade build costs
    /// about what classifying a 20 KB mod does.
    /// </summary>
    public static ImageKind Classify(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var reader = new PEReader(stream);

            if (reader.HasMetadata)
                return ImageKind.Managed;

            return reader.PEHeaders.CoffHeader.Machine == Machine.Amd64
                ? ImageKind.Native
                : ImageKind.NativeWrongBitness;
        }
        catch (BadImageFormatException)
        {
            return ImageKind.NotAnImage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImageKind.Unreadable;
        }
    }

    /// <summary>Names a native DLL from its version resource, falling back to its file name.</summary>
    public static NativePluginInfo Inspect(string dllPath)
    {
        string full = Path.GetFullPath(dllPath);
        string directory = Path.GetDirectoryName(full)!;
        string product = Path.GetFileName(full);
        string version = string.Empty;

        FileVersionInfo? info = ReadVersion(full);
        if (info is not null)
        {
            if (!string.IsNullOrWhiteSpace(info.ProductName))
                product = info.ProductName.Trim();

            // ProductVersion first: ReShade stamps "6.8.0" there and
            // "6.8.0.2155" in FileVersion, and the short one is what players
            // recognise from the installer they ran.
            string? text = info.ProductVersion ?? info.FileVersion;
            version = text?.Trim() ?? string.Empty;
        }

        return new NativePluginInfo(directory, full, product, version, CountAddons(directory));
    }

    private static bool ProductIs(string dllPath, string name)
    {
        string? product = ReadVersion(dllPath)?.ProductName;
        return product is not null &&
               string.Equals(product.Trim(), name, StringComparison.OrdinalIgnoreCase);
    }

    private static FileVersionInfo? ReadVersion(string dllPath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(dllPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// ReShade loads its own add-ons out of its base path; we only count them,
    /// so the splash can say at a glance that this is an add-on install.
    /// </summary>
    private static int CountAddons(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).Count(
                p => AddonExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
