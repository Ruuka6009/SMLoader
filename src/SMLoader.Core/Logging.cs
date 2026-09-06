using System.Runtime.InteropServices;
using System.Text;

namespace SMLoader.Core;

/// <summary>Appends to the same smloader.log the native shim writes to.</summary>
internal static class Logging
{
    private static readonly Lock Gate = new();
    private static string _path = "smloader.log";

    public static void Initialize(string rootDirectory)
        => _path = Path.Combine(rootDirectory, "smloader.log");

    public static void Write(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [core] {message}{Environment.NewLine}";

        // Purple so SMLoader's output stands out from the game's own console
        // spam. TrimEnd because the console adds its own newline.
        GameConsole.Write(line.TrimEnd());

        lock (Gate)
        {
            try
            {
                // FileShare.ReadWrite because the native shim appends to this
                // same file. Anything less and one side loses its lines during
                // boot, when both are chatty and a failure most needs both.
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
                                                  FileShare.ReadWrite);
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // Logging must never take the game down. The debugger channel
                // is the fallback because it cannot fail.
                OutputDebugStringW(line);
            }
        }
    }

    public static void Error(string message, Exception? exception = null)
        => Write(exception is null ? $"ERROR {message}" : $"ERROR {message}{Environment.NewLine}{exception}");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugStringW(string message);
}
