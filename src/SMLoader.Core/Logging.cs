using System.Runtime.InteropServices;
using System.Text;

namespace SMLoader.Core;

internal enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>Appends to the same smloader.log the native shim writes to.</summary>
internal static class Logging
{
    private static readonly Lock Gate = new();
    private static string _path = "smloader.log";

    /// <summary>
    /// Lines below this are dropped. Set with <c>SMLOADER_LOG_LEVEL</c>
    /// (Trace/Debug/Info/Warn/Error), defaulting to Info.
    /// </summary>
    /// <remarks>
    /// The reason this exists: a player asked to send their log should not be
    /// sending one chunk name per script the engine ever compiled, and a
    /// developer chasing asset redirection should be able to turn that detail
    /// back up without a rebuild.
    /// </remarks>
    private static readonly LogLevel Threshold = ReadThreshold();

    private static LogLevel ReadThreshold()
    {
        string? configured = Environment.GetEnvironmentVariable("SMLOADER_LOG_LEVEL");
        return Enum.TryParse(configured, ignoreCase: true, out LogLevel level) ? level : LogLevel.Info;
    }

    public static void Initialize(string rootDirectory)
        => _path = Path.Combine(rootDirectory, "smloader.log");

    public static void Write(string message) => Write(LogLevel.Info, message);

    public static void Write(LogLevel level, string message)
    {
        if (level < Threshold)
            return;

        // Info is unlabelled, so the common line keeps the shape it has always
        // had and existing log-reading habits still work.
        string tag = level == LogLevel.Info ? string.Empty : level.ToString().ToUpperInvariant() + " ";
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [core] {tag}{message}{Environment.NewLine}";

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

    public static void Trace(string message) => Write(LogLevel.Trace, message);

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? exception = null)
        => Write(LogLevel.Error,
                 exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugStringW(string message);
}
