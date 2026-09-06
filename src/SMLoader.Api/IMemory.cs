namespace SMLoader.Api;

/// <summary>
/// Direct access to the game's memory. SMLoader runs inside ScrapMechanic.exe,
/// so this is ordinary pointer work rather than cross-process reads.
/// </summary>
/// <remarks>
/// This is the escape hatch for behaviour the Lua API does not expose - moving a
/// character without the engine deriving a velocity from it, for instance. It is
/// also the fragile part of any mod: struct layouts and byte patterns are tied
/// to one build of the game and must be re-checked after an update. Prefer the
/// Lua API wherever it can do the job, and always verify a pointer before
/// writing through it.
/// </remarks>
public interface IMemory
{
    /// <summary>Base address of ScrapMechanic.exe.</summary>
    nint MainModuleBase { get; }

    /// <summary>Size in bytes of the main module's image.</summary>
    int MainModuleSize { get; }

    /// <summary>Base address of a loaded module, or 0 if it is not loaded.</summary>
    nint GetModuleBase(string moduleName);

    /// <summary>
    /// Scans a module for a byte pattern such as <c>"48 8B 05 ?? ?? ?? ?? 48 85 C0"</c>,
    /// where <c>??</c> matches any byte. Returns 0 when not found.
    /// </summary>
    /// <param name="moduleName">Module to search; null means the main module.</param>
    nint FindPattern(string pattern, string? moduleName = null);

    /// <summary>True when the whole range is committed and readable.</summary>
    bool IsReadable(nint address, int size);

    /// <summary>True when the whole range is committed and writable.</summary>
    bool IsWritable(nint address, int size);

    /// <summary>Reads a value, or the default when the address is unreadable.</summary>
    T Read<T>(nint address) where T : unmanaged;

    /// <summary>Writes a value. Returns false when the address is not writable.</summary>
    bool Write<T>(nint address, T value) where T : unmanaged;

    byte[] ReadBytes(nint address, int count);

    bool WriteBytes(nint address, byte[] bytes);

    /// <summary>
    /// Follows a pointer chain, e.g. base -> +0x10 -> +0x28. Returns 0 if any
    /// step is unreadable, so a bad offset yields nothing rather than a crash.
    /// </summary>
    nint ReadChain(nint address, params int[] offsets);

    /// <summary>Makes a range writable, returning the previous protection flags.</summary>
    uint Unprotect(nint address, int size);

    /// <summary>Restores protection flags previously returned by <see cref="Unprotect"/>.</summary>
    void Protect(nint address, int size, uint protection);

    /// <summary>
    /// Formats a hex dump with ASCII, for working out an unknown struct layout.
    /// </summary>
    string HexDump(nint address, int count);
}
