# SMLoader

A C# mod loader for Scrap Mechanic (Steam app 387990), hosted inside the game
process by a small native shim.

Nothing is installed into the Steam folder. No game file is renamed, patched or
added, so *Verify integrity of game files* has nothing to undo.

## How it works

```
SMLoader.Launcher.exe
  starts ScrapMechanic.exe suspended, injects the shim, resumes it
      |
SMLoader.Shim.dll  (C++)
  1. hooks the exe's IAT entry for lua51.dll!luaL_newstate
  2. starts CoreCLR through hostfxr on a worker thread
      |
SMLoader.Core.dll  (C#)
  loads mods from Mods/, forwards every lua_State to them
      |
Mods/<Name>/<Name>.dll  (C#, implements IMod)
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
write there, tagged `[shim]` and `[core]`.

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
