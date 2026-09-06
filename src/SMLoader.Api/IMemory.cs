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

    /// <summary>
    /// Makes a range writable, reporting the previous protection flags. Returns
    /// false when the range could not be unprotected, in which case nothing was
    /// changed and <paramref name="previous"/> is meaningless.
    /// </summary>
    /// <remarks>
    /// The range is made PAGE_READWRITE, not PAGE_EXECUTE_READWRITE: code does
    /// not need to be executable while it is being written, and an RWX page in
    /// a game process is what anti-cheat heuristics flag. Always restore the
    /// captured protection through <see cref="Protect"/> in a finally block.
    /// </remarks>
    bool TryUnprotect(nint address, int size, out uint previous);

    /// <summary>
    /// Restores protection flags previously captured by <see cref="TryUnprotect"/>.
    /// Returns false if the restore failed - the page is then left writable, which
    /// is worth logging.
    /// </summary>
    bool Protect(nint address, int size, uint protection);

    /// <summary>
    /// Formats a hex dump with ASCII, for working out an unknown struct layout.
    /// </summary>
    string HexDump(nint address, int count);
}
