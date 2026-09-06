using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SMLoader.Launcher;

/// <summary>
/// Starts the game suspended and maps SMLoader.Shim.dll into it before the
/// game's own code runs, so the shim can hook luaL_newstate in time.
/// </summary>
internal static class Injector
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

    private const uint MEM_COMMIT  = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    public static void LaunchWithShim(string exePath, string shimPath, string arguments)
    {
        string workingDirectory = Path.GetDirectoryName(exePath)!;

        var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");

        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                            CREATE_SUSPENDED, IntPtr.Zero, workingDirectory,
                            ref startupInfo, out PROCESS_INFORMATION process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
        }

        try
        {
            InjectLibrary(process.hProcess, shimPath);

            if (ResumeThread(process.hThread) == unchecked((uint)-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed");

            Console.WriteLine($"Game started (pid {process.dwProcessId}) with SMLoader injected.");
        }
        catch
        {
            TerminateProcess(process.hProcess, 1);
            throw;
        }
        finally
        {
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
    }

    private static void InjectLibrary(IntPtr process, string dllPath)
    {
        byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + '\0');

        IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)pathBytes.Length,
                                       MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remote == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx failed");

        try
        {
            if (!WriteProcessMemory(process, remote, pathBytes, (nuint)pathBytes.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory failed");

            // kernel32 is mapped at the same base in every process for a given
            // boot, so this address is valid in the target too.
            IntPtr kernel32 = GetModuleHandleW("kernel32.dll");
            IntPtr loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcAddress(LoadLibraryW) failed");

            IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread failed");

            try
            {
                // WAIT_FAILED here would leave GetExitCodeThread reporting a
                // value for a thread we never actually waited on, which reads as
                // a successful injection.
                if (WaitForSingleObject(thread, INFINITE) == WAIT_FAILED)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed");

                if (!GetExitCodeThread(thread, out uint moduleHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeThread failed");

                // LoadLibraryW returns the module base; 0 means the load failed.
                // (Truncated to 32 bits by the thread exit code, but 0 is still 0.)
                if (moduleHandle == 0)
                    throw new InvalidOperationException($"The target process refused to load {dllPath}.");
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        }
    }

    // ---- interop -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint   dwProcessId;
        public uint   dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int    cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int    dwX, dwY, dwXSize, dwYSize;
        public int    dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short  wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, nuint stackSize,
                                                    IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
