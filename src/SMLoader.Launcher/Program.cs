using SMLoader.Launcher;

const string AppId = "387990";

string? gamePath = null;
string? distPath = null;
var passThrough = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--game" when i + 1 < args.Length:
            gamePath = args[++i];
            break;
        case "--dist" when i + 1 < args.Length:
            distPath = args[++i];
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            passThrough.Add(args[i]);
            break;
    }
}

gamePath ??= GameLocator.Locate();
if (gamePath is null || !File.Exists(gamePath))
{
    Console.Error.WriteLine("Could not find ScrapMechanic.exe. Pass --game <path to ScrapMechanic.exe>.");
    return 1;
}

Console.WriteLine($"Game  : {gamePath}");

distPath ??= AppContext.BaseDirectory;
string shimPath = Path.Combine(distPath, "SMLoader.Shim.dll");
string corePath = Path.Combine(distPath, "SMLoader.Core.dll");

foreach (string required in new[] { shimPath, corePath })
{
    if (!File.Exists(required))
    {
        Console.Error.WriteLine($"Missing {required}. Run build.ps1 first.");
        return 1;
    }
}

// Lets steam_api64 initialise without a steam_appid.txt inside the game folder,
// so SMLoader never has to write into the Steam install. Steam must be running.
Environment.SetEnvironmentVariable("SteamAppId", AppId);
Environment.SetEnvironmentVariable("SteamGameId", AppId);

Console.WriteLine($"Loader: {distPath}");

try
{
    Injector.LaunchWithShim(gamePath, shimPath, string.Join(' ', passThrough));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Launch failed: {ex.Message}");
    return 1;
}

Console.WriteLine($"Log   : {Path.Combine(distPath, "smloader.log")}");
return 0;

static void PrintUsage()
{
    Console.WriteLine("""
        SMLoader.Launcher - starts Scrap Mechanic with SMLoader injected.

          --game <path>   Path to ScrapMechanic.exe (default: found via Steam)
          --dist <path>   Folder holding SMLoader.Shim.dll / SMLoader.Core.dll
                          (default: next to this launcher)
          --help          Show this text

        Any other arguments are forwarded to the game, e.g. -dev.
        """);
}
