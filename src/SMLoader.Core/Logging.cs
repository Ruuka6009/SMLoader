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
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the game down.
            }
        }
    }

    public static void Error(string message, Exception? exception = null)
        => Write(exception is null ? $"ERROR {message}" : $"ERROR {message}{Environment.NewLine}{exception}");
}
