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
    /// Held open rather than reopened per line. A world load compiles a lot of
    /// scripts, and an open/close pair per line lands on the thread doing the
    /// compiling.
    /// </summary>
    /// <remarks>
    /// Sharing the file with the shim is safe because both sides append: the
    /// stream is opened <see cref="FileMode.Append"/> with
    /// <see cref="FileShare.ReadWrite"/>, which maps to FILE_APPEND_DATA, and an
    /// append through such a handle is atomic against other appenders. Each line
    /// is written in one call and flushed, so two writers cannot interleave
    /// halfway through one.
    /// <para>
    /// Deliberately still synchronous. Draining through a background queue would
    /// take logging off the game thread, but it would also mean the last lines
    /// before a crash are the ones sitting in the queue - and those are the lines
    /// the log exists for. Revisit alongside a real shutdown path (§5.9).
    /// </para>
    /// </remarks>
    private static FileStream? _stream;

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
    {
        lock (Gate)
        {
            _path = Path.Combine(rootDirectory, "smloader.log");

            // The path changed, so whatever was open pointed at the fallback.
            _stream?.Dispose();
            _stream = null;
        }
    }

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
                // bufferSize 1 disables the stream's own buffering, so one line is
                // one write and there is nothing left sitting in a buffer when the
                // process dies.
                _stream ??= new FileStream(_path, FileMode.Append, FileAccess.Write,
                                           FileShare.ReadWrite, bufferSize: 1);

                byte[] bytes = Encoding.UTF8.GetBytes(line);
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
            catch
            {
                // Logging must never take the game down. Drop the handle so the
                // next line tries again rather than the log going quiet for the
                // rest of the session, and use the debugger channel meanwhile
                // because it cannot fail.
                try { _stream?.Dispose(); } catch { /* nothing left to try */ }
                _stream = null;

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
