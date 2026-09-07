using SMLoader.Launcher;

const string AppId = "387990";

string? gamePath = null;
string? distPath = null;
bool noMods = false;
bool allowExternalDist = false;
bool anyMod = false;
bool reshade = false;
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
        case "--no-mods":
            noMods = true;
            break;
        case "--allow-external-dist":
            allowExternalDist = true;
            break;
        case "--any-mod":
            anyMod = true;
            break;
        case "--reshade":
            reshade = true;
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

string ownDirectory = Path.GetFullPath(AppContext.BaseDirectory);
distPath = Path.GetFullPath(distPath ?? ownDirectory);

// The shim named here is injected into the game and runs as native code with
// this user's full privileges. A --dist pointing somewhere unexpected is
// therefore a code-execution vector that looks like a typo, so it has to be
// stated rather than merely typed.
if (!string.Equals(distPath.TrimEnd(Path.DirectorySeparatorChar),
                   ownDirectory.TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase) && !allowExternalDist)
{
    Console.Error.WriteLine($"""
        Refusing to inject from a folder other than the launcher's own.

          launcher : {ownDirectory}
          --dist   : {distPath}

        SMLoader.Shim.dll from that folder would run inside ScrapMechanic.exe with
        your account's full privileges. If that is what you meant, pass
        --allow-external-dist as well.
        """);
    return 1;
}

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

// CreateProcessW is called with a null environment block, so the game inherits
// ours - which is how these reach the managed side inside the game process.
if (noMods)
    Environment.SetEnvironmentVariable("SMLOADER_NO_MODS", "1");
if (anyMod)
    Environment.SetEnvironmentVariable("SMLOADER_ANY_MOD", "1");

Console.WriteLine($"Loader: {distPath}");
if (noMods)
    Console.WriteLine("Mods  : disabled (--no-mods)");

// Before the game starts, because the shim maps these the moment it attaches -
// a renderer hook is no use once the renderer exists. Off unless asked for:
// launching normally must give the player the game they had before.
//
// Deliberately not gated on --no-mods. Passing both is how you get ReShade with
// no managed mods behind it, which is the launch that tells you which of the two
// a problem belongs to.
NativePlugins.Publish(Path.Combine(distPath, "Mods"),
                      enabled: reshade,
                      ignoreAllowList: anyMod);

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

          --game <path>           Path to ScrapMechanic.exe (default: found via Steam)
          --dist <path>           Folder holding SMLoader.Shim.dll / SMLoader.Core.dll
                                  (default: next to this launcher)
          --allow-external-dist   Permit --dist outside the launcher's own folder
          --no-mods               Safe mode: boot the loader with no mods at all,
                                  to tell a loader problem from a mod problem
          --any-mod               Ignore Mods/allowed.json and load every mod found
          --reshade               Also load the native plugins under Mods/, such as
                                  ReShade. Off by default: they hook the renderer,
                                  so a normal launch leaves the game untouched.
                                  Combine with --no-mods for ReShade on its own
          --help                  Show this text

        Any other arguments are forwarded to the game, e.g. -dev.

        Mods run as native code inside ScrapMechanic.exe with your user account's
        full privileges. SMLoader does not sandbox them and cannot. Only install
        mods from sources you trust.
        """);
}
