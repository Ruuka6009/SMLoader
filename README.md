# SMLoader

A C# mod loader for Scrap Mechanic (Steam app 387990), hosted inside the game
process by a small native shim.

Nothing is installed into the Steam folder. No game file is renamed, patched or
added, so *Verify integrity of game files* has nothing to undo.

## Trust

> Mods run as native code inside `ScrapMechanic.exe` with your user account's
> full privileges. A mod can read and write any file you can, make network
> connections, and modify any memory in the game. SMLoader does not sandbox
> mods and cannot. Only install mods from sources you trust.

That follows from what a mod loader is, rather than being a gap to close
later - see [SECURITY.md](SECURITY.md), which also covers the mod allowlist,
the `--no-mods` safe mode, opt-in native plugins, and multiplayer and anti-cheat.

## How it works

```
SMLoader.Launcher.exe
  starts ScrapMechanic.exe suspended, injects the shim, resumes it
      |
SMLoader.Shim.dll  (C++)
  1. hooks the exe's IAT entry for lua51.dll!luaL_newstate
  2. maps the native plugins the launcher approved, on a thread of its own
  3. starts CoreCLR through hostfxr on a worker thread
      |
SMLoader.Core.dll  (C#)
  loads mods from Mods/, forwards every lua_State to them
      |
Mods/<Name>/<Name>.dll  (C#, implements IMod)
Mods/<Name>/<Name>.dll  (native, e.g. ReShade - see --reshade below)
```

The important part is that the game ships **lua51.dll** (LuaJIT) as a separate
DLL exporting the whole Lua 5.1 C API. Mods therefore talk to the engine through
its own scripting ABI - `lua_pushcclosure`, `lua_setfield` and friends - instead
of scanning for struct offsets. That survives game updates; offset hunting does
not.

`luaL_newstate` is imported by name in `ScrapMechanic.exe`, so capturing the
`lua_State*` is a plain IAT pointer swap: no trampolines, no detour library.

## Layout

| Project | Kind | Purpose |
|---|---|---|
| `src/SMLoader.Shim` | C++ / CMake | IAT hook + CoreCLR hosting |
| `src/SMLoader.Api` | C# lib | `IMod`, `IModHost`, Lua bindings - what mods reference |
| `src/SMLoader.Core` | C# lib | boot entry, mod discovery, logging |
| `src/SMLoader.Launcher` | C# exe | finds the game, injects the shim |
| `mods/NoclipMod` | C# lib | sample mod |

## Build

```powershell
.\build.ps1
```

Stages everything into `dist\`. Then:

```powershell
.\dist\SMLoader.Launcher.exe -dev
```

Any unrecognised argument is forwarded to the game, so `-dev` gets you Scrap
Mechanic's developer mode (script hot-reload) alongside the loader.

Steam must be running. The launcher sets `SteamAppId=387990` in the child
environment so `steam_api64` initialises without a `steam_appid.txt` inside the
game folder.

Diagnostics land in `dist\smloader.log` - the shim and the managed side both
write there, tagged `[shim]` and `[core]`. Set `SMLOADER_LOG_LEVEL` to
`Trace`, `Debug`, `Warn` or `Error` to move the threshold; it defaults to
`Info`, and `Trace` adds a line per distinct script the engine compiles.

Tests:

```powershell
dotnet test SMLoader.slnx -c Release
```

If something misbehaves, `--no-mods` boots the loader with no mods at all,
which separates a loader problem from a mod problem in one launch.

## Startup splash

Both banners are written to three places: the console attached to the game
process, `dist/smloader.log`, and - once the VM is populated - the game's own
`Logs/game-*.log` via `sm.log.info`, where they show up tagged `[Lua]`.

```
##############################################################
#                                                            #
#  S M L O A D E R                                           #
#  Mod Loader is starting                                    #
#                                                            #
#    version   0.1.0                                         #
#    runtime   .NET 10.0.8                                   #
#    process   pid 23720                                     #
#    root      C:\Dev\SMLoader\dist\                         #
#                                                            #
##############################################################
```

If the game has not allocated a console (no `-dev`), SMLoader allocates one so
the splash is always visible.

### Why the game-log splash is deferred

A state fresh out of `luaL_newstate` is bare - neither `sm` nor `print` exists
yet, so writing the banner at that moment silently goes nowhere. SMLoader also
hooks `lua_pcall`, and on each call asks the managed side whether the global
`sm` table has appeared. Once it has, the splash is emitted and the `lua_pcall`
hook retires itself, so it costs nothing for the rest of the session.

## When does the Lua VM appear?

Not at the main menu. On a normal boot the log looks like this:

```
[shim] hooked luaL_newstate (slot ..., original ...)
[shim] hooked lua_pcall (slot ...)
[core] ... Mod Loader is starting ...
[shim] status at 15s: hook intact, lua_States seen 0     <- expected at the menu
[shim] luaL_newstate -> lua_State* 0000024E02130380      <- a world loaded
[core] lua_State 0x24e02130380 available
[core] splash echoed to the game log
[shim] lua_pcall hook retired
[core] [NoclipMod] registered global 'smloader' table
```

`lua_States seen 0` at the menu is normal, not a failure - the `status at Ns`
checkpoints exist precisely to distinguish that from a lost hook.

## ReShade and other native plugins

A mod folder can hold native code instead of a .NET assembly. Drop a ReShade
build into `dist/Mods/ReShade/` and launch with `--reshade`:

```
dist/Mods/ReShade/
  dxgi.dll            the ReShade DLL, whatever it happens to be named
  ReShade.ini         ReShade's own config, written here rather than in the game folder
  reshade-shaders/    Shaders/ and Textures/
  *.addon64           add-ons, loaded by ReShade itself
```

```powershell
.\dist\SMLoader.Launcher.exe --reshade -dev
```

```
Game  : D:\SteamLibrary\steamapps\common\Scrap Mechanic\Release\ScrapMechanic.exe
Loader: C:\Dev\SMLoader\dist
Native: ReShade keeps its config, shaders, presets and screenshots in C:\Dev\SMLoader\dist\Mods\ReShade
Native: ReShade 6.8.0 (+19 add-ons) - C:\Dev\SMLoader\dist\Mods\ReShade\dxgi.dll
```

and the Ready banner names it next to the mods:

```
##############################################################
#                                                            #
#  S M L O A D E R                                           #
#  Ready                                                     #
#                                                            #
#    mods      1 - NoclipMod 1.0.0                           #
#    plugins   ReShade 6.8.0 (+19 add-ons)                   #
#    waiting   for the game's Lua VM                         #
#                                                            #
##############################################################
```

Without `--reshade` the plugin is found and left alone, and the launcher says
so, so a folder that does nothing is never silent:

```
Native: ReShade 6.8.0 (+19 add-ons) found, not enabled (pass --reshade)
```

### Why it is opt-in

A native plugin is not a mod the loader controls. It hooks the graphics API,
it stays for the session, and nothing in SMLoader can unload it or contain it.
Launching normally therefore has to give the player the game they had before,
which means the renderer is only touched when someone asks for it in as many
words.

`--reshade` and `--no-mods` are independent, and combining them is the point:

```powershell
.\dist\SMLoader.Launcher.exe --reshade --no-mods
```

is ReShade with nothing behind it, which is the launch that says whether a
problem belongs to the plugin or to the loader. Both are off by default, so it
still takes two flags to get there.

### Nothing lands in the Steam folder

The usual ReShade install puts `dxgi.dll` next to `ScrapMechanic.exe` and lets
it scatter `ReShade.ini`, `ReShade.log`, `reshade-shaders/` and every
screenshot through the game directory - exactly what SMLoader exists not to do.

Instead the launcher points `RESHADE_BASE_PATH_OVERRIDE` at the mod folder
before the game starts, so ReShade resolves its config, log, shader and texture
search paths, presets and screenshots inside `Mods/ReShade/`. Deleting that one
folder undoes all of it, and *Verify integrity of game files* still has nothing
to find. If you set that variable yourself, SMLoader leaves it alone.

### Which DLL in the folder is the plugin

In order: the one named after the folder (`Mods/ReShade/ReShade.dll`), the only
DLL in the folder, or the one whose version resource names the folder - which
is how `dxgi.dll` is recognised in a folder called `ReShade` even with a
`d3dcompiler_47.dll` beside it. Several DLLs and no way to choose is reported
rather than guessed at, with the rename to make.

Managed mods are never picked up this way: the launcher reads the PE header, so
a .NET assembly stays SMLoader.Core's business and a 32-bit DLL is refused with
a reason instead of a Windows error code.

### When it is loaded

The shim maps native plugins on a thread of its own the moment it attaches -
before the game's main thread has run an instruction, and a full second before
CoreCLR is up. A renderer hook is worth nothing once the renderer exists, which
is why this does not wait behind the managed side, and why the managed side is
handed the *result* rather than the job.

One failure is worth knowing about: if the plugin is called `dxgi.dll` and
something has already mapped a `dxgi.dll` under that name, the Windows loader
can hand back that module instead of yours, and the plugin never runs while
everything looks fine. SMLoader compares what it got against what it asked for
and reports the mismatch rather than a load that did nothing. Renaming the file
to `ReShade64.dll` avoids the question.

### Multiplayer

`--reshade` is post-processing, not a game change - but it is still an injected
DLL in the process, so the caveat at the bottom of this file applies unchanged.

## Changing game behaviour without touching game files

SMLoader never edits `Data/Scripts`. It hooks `luaL_loadbufferx`, which every
game script passes through on its way to the Lua compiler, and lets a mod
rewrite the source in memory:

```csharp
host.PatchScript("CreativePlayer.lua", ctx => ctx.Append("""
    local original = CreativePlayer.client_onFixedUpdate
    CreativePlayer.client_onFixedUpdate = function(self, ...)
        -- your logic here, every physics tick
        if original then return original(self, ...) end
    end
    """));
```

The file on disk is untouched, Steam's verification has nothing to undo, and
removing the mod fully reverts the change.

### The script sandbox

The engine calls `lua_setfenv` on scripts and gives each one its own
`lua_State` (a world load creates ~10). Measured from inside
`CreativePlayer.lua`:

| Available | Stripped |
|---|---|
| `sm`, `_G`, `os`, `pairs`, `print`, `dofile`, `tostring` | `getfenv`, `setmetatable`, `rawget`, `require`, `package`, `loadstring`, `debug`, `ffi` |

Two consequences:

- **`ffi` is not reachable from game scripts.** `luaopen_ffi` is in the exe's
  import table, but `require` and `package` are absent from the sandbox, so a
  script cannot get at it. Native calls have to come from the loader side.
- **Globals registered from C# are invisible to game scripts.** `smloader` and
  `_G.smloader` both read `nil` inside a script, because the script's
  environment is not the table `lua_setglobal` writes to.

### Writing injected Lua safely

Reading an undefined global yields `nil`, but *calling* one aborts the entire
chunk - and the engine then reports `Failed to load file` for that script. A
transform that calls `rawget` will break `CreativePlayer.lua`. Only use what the
table above lists as available, or guard with `if fn then`.

### What works today

Method wrapping is confirmed working: an injected wrapper around
`CreativePlayer.client_onFixedUpdate` runs every physics tick, with the original
still called. That is the hook point for movement changes such as noclip.

The open gap is input: with no `smloader` global in the sandbox, injected Lua
cannot yet call back into C# for arbitrary key polling. Two ways to close it -
hook `lua_setfenv` (also imported) to seed the loader's functions into every
script environment, or drive the toggle from the game's own
`client_onAction` bindings in pure Lua.

## Writing a mod

```csharp
public sealed class MyMod : IMod
{
    public string Name => "MyMod";

    public void OnLoad(IModHost host)
        => host.LuaStateCreated += lua =>
        {
            lua.RegisterModule("mymod", t =>
                t.SetFunction("hello", L => { L.Push("hi"); return 1; }));
        };
}
```

Build it into `dist/Mods/<Name>/<Name>.dll` (see `mods/NoclipMod/NoclipMod.csproj`
for the `OutputPath` wiring) and it is picked up on next launch.

### Threading

`LuaStateCreated` fires on the game's own script thread, and your registered
functions are called by Lua on that thread. That is the only place it is safe to
touch a `LuaState`. Calling into Lua from a background thread corrupts the VM.

Exceptions escaping a mod callback are caught and logged rather than propagated:
letting one unwind into LuaJIT's frames would kill the process.

## Division of labour

The loader deliberately does not implement noclip. Movement, camera and world
manipulation all live behind the `sm.*` Lua API, so that logic belongs in Lua.
What C# is for is the things Lua cannot reach - `NoclipMod` exposes
`smloader.isKeyDown(vk)` precisely because the game's fixed action bindings in
`keybinds.json` cannot express an arbitrary hotkey.

Combine it with a patched `Data/Scripts/game/CreativePlayer.lua`:

```lua
if smloader and smloader.isKeyDown(0x71) then  -- F2
    -- toggle your fly state here
end
```

## Rider

Open `SMLoader.slnx`; all four C# projects load from it.

If projects fail with **MSB4236 `Microsoft.NET.SDK.WorkloadAutoImportPropsLocator`
not found**, Rider has selected Visual Studio's MSBuild instead of the .NET
SDK's. Fix in *Settings > Build, Execution, Deployment > Toolset and Build >
Use MSBuild version* - pick the entry under `C:\Program Files\dotnet\sdk\...`,
not the one under `Microsoft Visual Studio\18\BuildTools`.

The C++ shim is not in the `.slnx` (slnx holds the managed projects). `build.ps1`
generates `build\shim\SMLoader.Shim.vcxproj`, which Rider can open on its own;
alternatively open `src\SMLoader.Shim\CMakeLists.txt` directly as a CMake project.

## Caveats

- Single-player only. Patched scripts or injected globals will desync you from
  other players.
- The IAT hook is re-applied for one second after injection, because the Windows
  loader finishes resolving imports after our `DllMain` returns.
- `dist\` and `build\` are git-ignored; `build.ps1` regenerates both.
