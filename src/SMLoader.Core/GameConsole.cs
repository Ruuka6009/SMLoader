using System.Runtime.InteropServices;
using System.Text;

namespace SMLoader.Core;

/// <summary>
/// Writes to the console window attached to the game process. Scrap Mechanic
/// allocates one itself in -dev mode; if it has not (or we got there first),
/// we allocate one so the splash is always visible.
/// </summary>
internal static partial class GameConsole
{
    private static int _ready;

    public static void Ensure()
    {
        // Interlocked, because two threads logging at once could otherwise both
        // pass a plain check and both call AllocConsole.
        if (Interlocked.Exchange(ref _ready, 1) != 0)
            return;

        try
        {
            if (GetConsoleWindow() == 0 && !AllocConsole())
                return;

            SetConsoleOutputCP(65001); // UTF-8, so box characters survive
            SetConsoleTitleW("Scrap Mechanic - SMLoader");

            // A console allocated after startup is not wired to Console.Out yet.
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            Console.SetOut(stdout);
        }
        catch (Exception ex)
        {
            Logging.Error("could not attach a console", ex);
        }
    }

    /// <summary>
    /// Writes one line to the game's console. Every SMLoader line goes through
    /// here so the loader's output is one recognisable colour among the game's
    /// own logging.
    /// </summary>
    public static void Write(string line, ConsoleColor colour = ConsoleColor.Magenta)
    {
        Ensure();

        ConsoleColor previous = ConsoleColor.Gray;
        bool coloured = false;

        try
        {
            previous = Console.ForegroundColor;
            Console.ForegroundColor = colour;
            coloured = true;
        }
        catch
        {
            // No console attached; still attempt the write below.
        }

        try
        {
            Console.Out.WriteLine(line);
        }
        catch
        {
            // Console writes must never take the game down.
        }
        finally
        {
            if (coloured)
            {
                try { Console.ForegroundColor = previous; } catch { /* ignore */ }
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleOutputCP(uint codePageId);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleTitleW(string title);
}
