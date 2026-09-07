# Security

## The trust model, stated plainly

Mods run as native code inside `ScrapMechanic.exe` with your user account's full
privileges. A mod can read and write any file you can, make network connections,
and modify any memory in the game. **SMLoader does not sandbox mods and cannot.**
Only install mods from sources you trust.

This is not a gap to be closed later. It follows from what a mod loader is: mods
are .NET assemblies loaded into the game process, and the API deliberately hands
them raw memory access (`IMemory`) because that is the only way to do the things
mods exist to do. A sandbox strong enough to matter would have to remove that,
and would leave something that could no longer load a mod like NoclipMod.

## What SMLoader does do

- **Never writes into the game folder.** Patched assets go to SMLoader's own
  cache, script rewriting happens in memory, and the Steam App ID is passed by
  environment variable rather than a `steam_appid.txt` in the install. *Verify
  integrity of game files* has nothing to undo.
- **Refuses to inject from an unexpected folder.** `--dist` pointing outside the
  launcher's own directory is rejected unless you also pass
  `--allow-external-dist`. The DLL named there runs as native code in the game,
  so a mistyped path should not be enough to run it.
- **Supports an optional mod allowlist.** If `Mods/allowed.json` exists, only
  mods whose SHA-256 matches load:

  ```json
  { "NoclipMod.dll": "5E884898DA28047151D0E56F8DC6292773603D0D6AABBDD62A11EF721D1542D8" }
  ```

  A corrupt or unreadable allowlist refuses **everything** rather than falling
  back to loading anything — a broken allowlist must not be a way past the
  allowlist. `--any-mod` skips it for one launch.
- **Provides a safe mode.** `--no-mods` boots the loader with no mods at all, so
  you can tell a loader problem from a mod problem in one launch.
- **Keeps native plugins opt-in.** A mod folder holding native code rather than a
  .NET assembly - ReShade is the case this exists for - is found but not loaded
  unless you pass `--reshade`. Such a plugin is mapped into the process before
  the CLR exists and hooks the graphics API from its own `DllMain`; the loader
  cannot unload it, contain it or see what it does. Launching normally therefore
  runs the game unchanged. `--reshade` and `--no-mods` are independent - passing
  both gives ReShade with no managed mods behind it, which is how you tell the
  two apart when something breaks.
- **Checks native plugins against the same allowlist.** `Mods/allowed.json`
  covers them too. The check runs in the launcher, before the game process
  exists, because that is the last moment anything managed can look at the file
  - and a plugin that loads earlier and with fewer questions asked than any mod
  is the last thing that should get to skip it.

  The list the launcher hands the shim travels in the `SMLOADER_NATIVE_PLUGINS`
  environment variable, and the launcher clears anything it inherited first, so
  a value left in a shell does not get to decide what runs inside the game. When
  nothing is to be loaded it also sets `SMLOADER_NO_NATIVE=1`, and the shim
  refuses on that alone - the same decision stated twice, so the variable
  carrying the paths is never the only thing standing between a stale
  environment and a DLL being mapped.

## Multiplayer and anti-cheat

SMLoader modifies game memory and injects a DLL. Do not use it on servers you do
not own, and assume that any anti-cheat system will treat it as exactly what it
is. Nothing here is designed to hide from detection.

## Reporting a vulnerability

Open an issue describing the class of problem. Please do not include a working
exploit.
