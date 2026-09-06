using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SMLoader.Api;

namespace SMLoader.Core;

/// <summary>
/// In-process implementation of <see cref="IMemory"/>. Every read and write is
/// guarded by a VirtualQuery first, so a wrong offset from a mod produces a zero
/// or a false rather than taking the game down.
/// </summary>
internal sealed class ProcessMemory : IMemory
{
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_READWRITE = 0x04;

    private static readonly uint[] ReadableFlags =
    {
        0x02, 0x04, 0x08, 0x20, 0x40, 0x80, // READONLY, READWRITE, WRITECOPY, EXECUTE_READ, EXECUTE_READWRITE, EXECUTE_WRITECOPY
    };

    private static readonly uint[] WritableFlags = { 0x04, 0x08, 0x40, 0x80 };

    public ProcessMemory()
    {
        ProcessModule? main = Process.GetCurrentProcess().MainModule;
        MainModuleBase = main?.BaseAddress ?? 0;
        MainModuleSize = main?.ModuleMemorySize ?? 0;

        Logging.Write($"main module at 0x{MainModuleBase:X} size 0x{MainModuleSize:X}");
    }

    public nint MainModuleBase { get; }

    public int MainModuleSize { get; }

    public nint GetModuleBase(string moduleName) => GetModuleHandleW(moduleName);

    public bool IsReadable(nint address, int size) => HasProtection(address, size, ReadableFlags);

    public bool IsWritable(nint address, int size) => HasProtection(address, size, WritableFlags);

    private static bool HasProtection(nint address, int size, uint[] allowed)
    {
        if (address == 0 || size <= 0)
            return false;

        nint cursor = address;
        nint end = address + size;

        while (cursor < end)
        {
            if (VirtualQuery(cursor, out MEMORY_BASIC_INFORMATION info,
                             (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            {
                return false;
            }

            if (info.State != MEM_COMMIT)
                return false;

            uint protect = info.Protect;
            if ((protect & PAGE_GUARD) != 0 || (protect & PAGE_NOACCESS) != 0)
                return false;

            if (Array.IndexOf(allowed, protect & 0xFF) < 0)
                return false;

            cursor = info.BaseAddress + (nint)info.RegionSize;
        }

        return true;
    }

    public unsafe T Read<T>(nint address) where T : unmanaged
    {
        if (!IsReadable(address, sizeof(T)))
            return default;

        return *(T*)address;
    }

    public unsafe bool Write<T>(nint address, T value) where T : unmanaged
    {
        if (!IsWritable(address, sizeof(T)))
            return false;

        *(T*)address = value;
        return true;
    }

    public byte[] ReadBytes(nint address, int count)
    {
        if (count <= 0 || !IsReadable(address, count))
            return Array.Empty<byte>();

        var buffer = new byte[count];
        Marshal.Copy(address, buffer, 0, count);
        return buffer;
    }

    public bool WriteBytes(nint address, byte[] bytes)
    {
        if (bytes.Length == 0 || !IsWritable(address, bytes.Length))
            return false;

        Marshal.Copy(bytes, 0, address, bytes.Length);
        return true;
    }

    public nint ReadChain(nint address, params int[] offsets)
    {
        nint cursor = address;
        foreach (int offset in offsets)
        {
            if (cursor == 0)
                return 0;

            cursor = Read<nint>(cursor + offset);
        }
        return cursor;
    }

    public bool TryUnprotect(nint address, int size, out uint previous)
    {
        previous = 0;
        if (address == 0 || size <= 0)
            return false;

        // PAGE_READWRITE rather than PAGE_EXECUTE_READWRITE: the page only has
        // to be writable while the patch is applied, and an RWX page left in
        // the game's .text is both a W^X violation and an anti-cheat trigger.
        if (VirtualProtect(address, (nuint)size, PAGE_READWRITE, out previous))
            return true;

        int error = Marshal.GetLastWin32Error();
        previous = 0;
        Logging.Error($"VirtualProtect failed at 0x{address:X} ({size} bytes), error {error}");
        return false;
    }

    public bool Protect(nint address, int size, uint protection)
    {
        // 0 is not a valid protection constant, so refuse it rather than making
        // a call that fails and leaves the page writable without saying so.
        if (protection == 0 || address == 0 || size <= 0)
            return false;

        return VirtualProtect(address, (nuint)size, protection, out _);
    }

    public nint FindPattern(string pattern, string? moduleName = null)
    {
        (byte[] bytes, bool[] wildcard) = ParsePattern(pattern);
        if (bytes.Length == 0)
            return 0;

        nint start;
        int length;

        if (moduleName is null)
        {
            start = MainModuleBase;
            length = MainModuleSize;
        }
        else
        {
            start = GetModuleBase(moduleName);
            length = 0;
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    length = module.ModuleMemorySize;
                    break;
                }
            }
        }

        if (start == 0 || length <= 0)
            return 0;

        // Walk region by region: an image has holes, and reading straight
        // through them would fault.
        nint cursor = start;
        nint end = start + length;

        while (cursor < end)
        {
            if (VirtualQuery(cursor, out MEMORY_BASIC_INFORMATION info,
                             (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            {
                break;
            }

            nint regionEnd = info.BaseAddress + (nint)info.RegionSize;

            if (info.State == MEM_COMMIT &&
                (info.Protect & PAGE_GUARD) == 0 &&
                (info.Protect & PAGE_NOACCESS) == 0 &&
                Array.IndexOf(ReadableFlags, info.Protect & 0xFF) >= 0)
            {
                nint scanEnd = regionEnd < end ? regionEnd : end;

                // Overlap into the next region by pattern.Length - 1 when that
                // region is readable too. Clamping strictly to the region made a
                // signature spanning two adjacent committed pages invisible, and
                // the failure reads as "the game updated" rather than "we
                // mis-scanned" - the expensive kind of wrong.
                if (scanEnd == regionEnd && bytes.Length > 1)
                {
                    nint overlap = regionEnd + bytes.Length - 1;
                    if (overlap > end)
                        overlap = end;

                    if (overlap > scanEnd && IsReadable(regionEnd, (int)(overlap - regionEnd)))
                        scanEnd = overlap;
                }

                nint found = ScanRange(cursor, scanEnd, bytes, wildcard);
                if (found != 0)
                    return found;
            }

            cursor = regionEnd;
        }

        return 0;
    }

    private static unsafe nint ScanRange(nint from, nint to, byte[] pattern, bool[] wildcard)
    {
        long span = (long)to - (long)from - pattern.Length;
        if (span < 0)
            return 0;

        var data = (byte*)from;
        for (long i = 0; i <= span; i++)
        {
            bool matched = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (!wildcard[j] && data[i + j] != pattern[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return from + (nint)i;
        }

        return 0;
    }

    /// <summary>
    /// Internal rather than private so it can be tested directly. It is pure,
    /// and every interesting case - "??", a bare "?", invalid hex, an empty
    /// pattern - is a silent wrong answer at a scan site rather than an error.
    /// </summary>
    internal static (byte[] Bytes, bool[] Wildcard) ParsePattern(string pattern)
    {
        string[] tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var wildcard = new bool[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "?" or "??")
            {
                wildcard[i] = true;
                continue;
            }

            if (!byte.TryParse(tokens[i], System.Globalization.NumberStyles.HexNumber, null, out byte value))
                return (Array.Empty<byte>(), Array.Empty<bool>());

            bytes[i] = value;
        }

        return (bytes, wildcard);
    }

    public string HexDump(nint address, int count)
    {
        byte[] data = ReadBytes(address, count);
        if (data.Length == 0)
            return $"0x{address:X}: <unreadable>";

        var text = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            int run = Math.Min(16, data.Length - offset);

            text.Append($"+0x{offset:X3}  ");
            for (int i = 0; i < 16; i++)
                text.Append(i < run ? data[offset + i].ToString("X2") + " " : "   ");

            text.Append(' ');
            for (int i = 0; i < run; i++)
            {
                byte b = data[offset + i];
                text.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string moduleName);
}
