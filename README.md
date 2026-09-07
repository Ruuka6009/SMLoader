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
| `mods/PhysgunMod` | C# lib | physics gun for creative mode |

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

## Physgun

A Garry's Mod style physics gun for creative mode, built entirely on the game's
own scripting API - `sm.physics.applyImpulse` for the hold, a raycast for the
aim, a `ShapeRenderable` effect for the beam. No game file is touched.

It is **off until you turn it on**, because the grab key defaults to left mouse
and creative mode already has a use for that.

| | Default | |
|---|---|---|
| Physgun mode | `G` or `/physgun` | off at launch |
| Grab | left mouse | hold to carry what you are looking at |
| Freeze | right mouse | while carrying: pins it where it is and lets go |
| Unfreeze | `R` | releases the frozen object you are looking at |
| Push / pull | `PageUp` / `PageDown` | move the carried object further or nearer |

`/physgunrange <m>`, `/physgunforce <n>`, `/unfreezeall`, `/physgundebug` and
`/physgunstatus` round it out, and every key and constant is also a row in the
shared settings panel.

### It carries creations, not blocks

The ray resolves to a body, and the body to `getCreationBodies()` - the whole
welded, bearinged, pistoned thing. The spring then drives the creation's
*mass-weighted centre of mass*, and hands each body its share of the velocity
change in proportion to its own mass.

Both halves of that matter, and getting either wrong makes the physgun pull in
some directions and not others:

- Towing one body of a jointed contraption sends the force through its bearings,
  and a joint only transmits along the axes it does not constrain - so the
  directions that worked were the ones the joints left free.
- `body.worldPosition` is the body's *origin*, which can sit metres from its
  centre of mass, while an impulse always acts at the centre of mass. Driving
  the origin to the target leaves a lever arm that rotates with the object,
  biasing the pull in a direction that turns as the object turns.

A ray that lands on a bearing or piston reports type `"joint"` rather than
`"body"` and carries no shape of its own; it is resolved through the joint's
`shapeA`, the way `Lift.lua` does it. On most builds the moving parts are most
of what you can actually point at.

### Why it is a spring, not a teleport

Scrap Mechanic binds no `setWorldPosition` or `setVelocity` on a `Body` - in the
game's own scripts those methods appear only on characters, triggers and
effects. The only way to move a body is to push it, so the hold is a damped
spring evaluated on the server's fixed tick:

```lua
local dv = offset * ( stiffness * dt )
         - body.velocity * ( damping * dt )
         + sm.vec3.new( 0, 0, tuning.gravity * dt )   -- cancel this tick's fall

sm.physics.applyImpulse( body, dv * body.mass, true )
```

That constraint is also what makes it feel right. The gravity term is what stops
a carried object sagging until the spring error is large enough to hold its own
weight, and `stiffness` is capped because the spring is integrated once per
40 Hz tick - `k*dt` much above 1.5 overshoots further every tick.

It runs on the **server** half of the player script for the same reason noclip
does: the server owns body positions, and a client that moves things itself is
racing the physics it is trying to steer. The client sends only where it is
aiming.

### Spin damping is a loop over an inertia nothing exposes

Settling a carried object's tumble means feeding its angular velocity back as an
opposing angular impulse. Angular impulse is inertia times a change in spin, and
Scrap Mechanic binds no inertia anywhere - not on `Body`, not on `Shape`, not
under any name in the executable.

Scaling by mass instead, the way the game's own one-shot tumbles do
(`PlasmaDrill.lua:489`), makes that a controller whose gain is wrong by whatever
the ratio of mass to inertia happens to be. For a single block, mass
over-estimates inertia by roughly ten, so each correction overshoots, the spin
reverses larger every tick, and after a few seconds of carrying, the object is
spinning hard enough to fling itself off anything it touches. A one-shot impulse
never showed this because a one-shot closes no loop.

The impulse is therefore scaled by a *lower bound* on inertia: every shape is at
least one 0.25 m block, so inertia is at least `mass * 0.01`. The change in spin
actually applied is then `(bound / true inertia) * fraction * spin`, which can
never exceed the spin itself - so the loop cannot overshoot whatever it is
holding. Large creations settle more slowly than they could; that is what not
knowing inertia costs, and it is the right side to err on.

### Freeze pins, it does not spring

Nothing in the Lua API makes a dynamic body static. `sm.player.placeLift` is the
only true immobiliser, and it snaps the build to a quarter-metre grid under a
visible lift - not a freeze where you left it.

So freeze pins every body of the creation to its own centre of mass at the
moment you let go, and each tick steps it back towards that point while
cancelling whatever velocity it has picked up:

```lua
local dv = offset * ( PG_PIN_GAIN / dt )
         - body.velocity * PG_PIN_VELOCITY_CANCEL
         + pose.bias                                  -- learned, see below
```

A spring answers a shove with a restoring force, so the build absorbs the energy
and bobs; the pin cancels the velocity instead, so there is far less left to bob
with. Simulated against a 5 m/s shove, it is back within 5 mm in **250 ms**.

Proportional control alone cannot hold a body against a *steady* force: it
settles wherever its correction happens to balance that force, and that offset
is what you see as a frozen build sitting slightly off and drifting. The steady
force here is whatever the gravity feed-forward gets wrong - and the game reports
its real gravity nowhere, so rather than guess it, the pin integrates its own
error and learns it. Against a residual as large as 10 m/s^2 that takes the
settled error from about **25 mm to under 0.1 mm**.

Pinning each body separately is also what stops a contraption folding at its own
joints while frozen.

### Every correction here is closed over a stale reading

`PG_PIN_GAIN` is a quarter and not one, and that is the whole difference between
a frozen build sitting still and a frozen build flying across the map.

The script sees the solver's state as it was at the *start* of the step, not as
it is when the impulse lands. With that one tick of latency, a loop removing a
fraction `g` of its error per tick is no longer the textbook
`x[n+1] = (1-g)*x[n]`, stable for any `g < 2`. It is:

```
x[n+1] = x[n] - g*x[n-1]      ->   z^2 - z + g = 0,   |z| = sqrt(g)
```

so the real limit is `g < 1`, and `g = 1` sits exactly on the unit circle -
ringing forever instead of settling, with the joint solver more than enough to
push it over. Closing the whole gap in one tick is precisely that `g = 1`.

Every gain in the mod is picked against this limit, and the spin fractions are
capped for the same reason - the spin damping slider's own maximum was otherwise
enough to make a *carried* object diverge. Simulated with latency, on the
thinnest shape in the game:

Cancelling *all* of a pinned body's velocity is a second way to sit on that
limit, and a less obvious one: the velocity being cancelled was read before the
impulse lands, so at full strength the correction is a tick out of phase with
what it is correcting. `PG_PIN_VELOCITY_CANCEL` is 0.6 for that reason alone -
at 1.0, every proportional gain worth using was divergent.

The pin's three constants were swept together against simulated latencies of
zero, one and two ticks, since the true figure is not observable from outside
the game. Nothing survives two; these sit comfortably inside stability at one:

| loop | gain | root magnitude | peak overshoot |
|---|---|---|---|
| carry spring (default, and at max strength) | 0.01 - 0.03 | 0.09 - 0.16 | none |
| carry spin damping (default, and at max) | 0.27 - 0.72 | 0.52 - 0.85 | none |
| freeze spin damping | 0.72 | 0.85 | none |

### What it will not do

- **Creative only.** `CreativePlayer.lua` is not loaded in Survival.
- **Anchored bodies.** Anything welded to the ground or sat on a lift ignores
  impulses; the physgun says so rather than leaving you to wonder.
- **No inventory item.** It is a hotkey, not a tool in the hotbar - a real tool
  needs new UUIDs in the shapeset and `inventoryDescriptions.json` through
  `PatchAsset`, plus a model and animations.
- **A frozen single body can be left rotated.** The pin holds position exactly
  and brakes spin hard, but restoring an orientation needs inertia, which the
  API does not expose. Anything with more than one body holds its rotation
  anyway, because several pinned points leave nothing to rotate about.
- **Multiplayer needs the host to have the mod**, since all the physics is
  server-side.

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
