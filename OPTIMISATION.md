# SMLoader — Optimisation, Hardening & Robustness Review

A catalogue of everything this project *could* have, grouped by theme. Nothing
here is a bug report against a broken build — SMLoader works. This is the list
of what separates "works on my machine, on this build of the game" from
"survives a game update, a mod that misbehaves, and a 200-hour play session".

Every item carries a severity and, where it matters, the exact site.

| Tag | Meaning |
|---|---|
| **[C]** | Correctness — it is wrong now, or wrong under a condition that will occur |
| **[P]** | Performance — measurable cost on a hot path (per frame, per file open, per script compile) |
| **[S]** | Security / trust model |
| **[E]** | Error handling and failure isolation |
| **[Q]** | Code quality, maintainability, tooling |

---

## Table of contents

1. [Correctness defects worth fixing first](#1-correctness-defects-worth-fixing-first)
2. [Hot-path performance](#2-hot-path-performance)
3. [Memory and lifetime](#3-memory-and-lifetime)
4. [Concurrency and the native/managed boundary](#4-concurrency-and-the-nativemanaged-boundary)
5. [Error handling and failure isolation](#5-error-handling-and-failure-isolation)
6. [Security and the trust model](#6-security-and-the-trust-model)
7. [Native shim hardening](#7-native-shim-hardening)
8. [API design and mod compatibility](#8-api-design-and-mod-compatibility)
9. [Build, tooling and repository hygiene](#9-build-tooling-and-repository-hygiene)
10. [Testing](#10-testing)
11. [Observability](#11-observability)
12. [Prioritised roadmap](#12-prioritised-roadmap)

---

## 1. Correctness defects worth fixing first

### 1.1 [C] ~~The redirected asset path is not NUL-terminated~~ — RESOLVED, with a correction

The finding was **wrong on its central claim**, and the correction is worth more
than the finding was. `Entry.cs:267` never assigned a space:

```
00000030: 3d20 2700 273b 0a                        = '.';.
```

The source file held a **raw 0x00 byte between the quotes** — a valid C# NUL
character literal that every editor, review tool and terminal renders as a
space. The terminator was correct all along, written in the one form that cannot
be reviewed. It also made git treat `Entry.cs` as binary, so `grep` refused to
print matches from it.

Fixed by writing the escape, so the intent survives being read:

```csharp
((char*)outPtr)[replacement.Length] = '\0';
```

The second half of the finding was real and is also fixed: the receiving buffer
`wchar_t replacement[1024]` on the detour's stack (`iat_hook.cpp:91`) was
uninitialised, so any future path through that function that writes no
terminator hands `CreateFileW` stack garbage. It is now `{}`-initialised.

**Lesson for the rest of this document:** a finding derived from reading source
as rendered text can be defeated by a control character in that source. Where an
item turns on an exact byte, check the bytes.

### 1.2 [C] Log writes race between the shim and the managed side, and both lose

`src/SMLoader.Shim/log.cpp:55` opens with `FILE_SHARE_READ` only.
`src/SMLoader.Core/Logging.cs:26` uses `File.AppendAllText`, which opens with
`FileShare.Read`.

Neither permits a concurrent *writer*. Whenever the shim and the core log at the
same moment — which is exactly what happens during boot, when both are chatty —
one `CreateFileW` returns `INVALID_HANDLE_VALUE` (shim: silently returns) or one
`File.AppendAllText` throws (core: silently swallowed by the `catch`). Lines are
lost from the one file you would use to diagnose a failure.

**RESOLVED.** Both ends now open with `FILE_SHARE_READ | FILE_SHARE_WRITE`:
`log.cpp` in both `Init` and `Write`, and `Logging.Write` through an explicit
`FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)` in
place of `File.AppendAllText`. The managed catch now falls back to
`OutputDebugStringW` instead of swallowing the line, which also settles
[§5.2](#52-e-boots-own-catch-can-throw): a failure inside `Logging.Initialize`
can no longer lose its own report.

The single-writer design in
[§11.2](#112-p-one-writer-buffered-off-the-game-thread) is still the better
long-term answer; this removes the data loss in the meantime.

### 1.3 [C] `luaL_ref` slots are keyed by a raw `lua_State*` that can be recycled

`src/SMLoader.Core/LuaApi.cs:15,62-81`

```csharp
private static readonly Dictionary<nint, int> TableRefs = new();
```

Entries are never removed. A world load creates ~10 states; unloading destroys
them, and the allocator is free to hand the *same address* back for a state
created by the next world load. The cached registry `ref` then belongs to a dead
registry, and `lua_rawgeti(L, LUA_REGISTRYINDEX, staleRef)` reads an unrelated
slot in the new state — silently yielding the wrong value, or `nil`, so
`smloader` disappears from the sandbox for the rest of the session.

Two things are needed:

- Hook `lua_close` (it sits in the same import table as the other four) and evict
  the entry, calling `luaL_unref` first.
- Treat a cache hit as advisory: verify the fetched value is a table before using
  it, and rebuild if not.

```csharp
lua_rawgeti(L, LUA_REGISTRYINDEX, reference);
if (!lua_istable(L, -1)) { lua_pop(L, 1); Evict(L); reference = GetOrCreateTable(L); /* ... */ }
```

### 1.4 [C] `luaL_ref` can return a sentinel that is treated as a valid slot

Same file, line 77. `luaL_ref` returns `LUA_REFNIL` (`-1`) when the value on top
is nil and `LUA_NOREF` (`-2`) on failure. The code only rejects `0`. Reject
anything `<= 0`, and log the case — it means the table build itself failed.

### 1.5 [C] Pattern matches that straddle a memory-region boundary are never found

`src/SMLoader.Core/ProcessMemory.cs:170-195`

The scan is clamped to `min(regionEnd, end)` per region, so a signature spanning
two adjacent committed pages with different protection flags is invisible. The
guard signatures NoclipMod relies on are 10 bytes; a `.text` section split at an
awkward boundary is unlikely but not impossible, and the failure mode reads as
"the game updated" rather than "we mis-scanned".

Scan with an overlap of `pattern.Length - 1` into the next region when that
region is also readable.

### 1.6 [C] `Unprotect` ignores failure, and `Protect` can leave a page RWX

`src/SMLoader.Core/ProcessMemory.cs:128-135`

```csharp
public uint Unprotect(nint address, int size)
{
    VirtualProtect(address, (nuint)size, PAGE_EXECUTE_READWRITE, out uint previous);
    return previous;   // 0 if the call failed
}
```

If `VirtualProtect` fails, `previous` is `0`, which is not a valid protection
constant. The caller then does `Protect(jump, 2, 0)`, which fails too — and
because that return value is also discarded, a page of the game's `.text` is
left **PAGE_EXECUTE_READWRITE for the rest of the session**. That is both a
correctness bug and a W^X weakening (see [§6.4](#64-s-wx-is-violated-during-the-guard-patch)).

```csharp
public bool TryUnprotect(nint address, int size, out uint previous)
    => VirtualProtect(address, (nuint)size, PAGE_READWRITE, out previous);

public bool Protect(nint address, int size, uint protection)
    => protection != 0 && VirtualProtect(address, (nuint)size, protection, out _);
```

and have `PatchGuard` refuse to write when the unprotect failed.

**RESOLVED.** `IMemory` now exposes `bool TryUnprotect(nint, int, out uint)` and
`bool Protect(nint, int, uint)`. `ProcessMemory` unprotects to `PAGE_READWRITE`
rather than `PAGE_EXECUTE_READWRITE`, logs the Win32 error on failure, and
refuses a `Protect` call carrying a 0 protection constant instead of making a
call that silently fails. `NoclipMod.PatchGuard` returns early when the unprotect
fails and restores the captured protection in a `finally`, logging if the page
was left writable. That closes
[§6.4](#64-s-wx-is-violated-during-the-guard-patch) with it — no page is ever
RWX, and none is left writable.

This is a **breaking change** to `IMemory` for any mod compiled against 0.1.0,
which is exactly the situation [§8.1](#81-c-there-is-no-api-version-handshake)
describes.

### 1.7 [C] `EnumProcessModules` truncation is not detected

`src/SMLoader.Shim/iat_hook.cpp:180-190`

`HMODULE modules[512]` with no check of `needed > sizeof(modules)`. A process
with more than 512 modules (Scrap Mechanic plus overlays, input remappers,
RTSS, Discord, Steam) silently gets a partial hook set, so some asset opens are
never redirected — an intermittent, machine-specific failure.

```cpp
DWORD needed = 0;
EnumProcessModules(GetCurrentProcess(), nullptr, 0, &needed);
std::vector<HMODULE> modules(needed / sizeof(HMODULE) + 16);
if (!EnumProcessModules(GetCurrentProcess(), modules.data(),
                        static_cast<DWORD>(modules.size() * sizeof(HMODULE)), &needed))
    return;
```

### 1.8 [C] Modules loaded after `HookFileApis` are never hooked

Same function. It is a one-shot sweep on the boot thread. Any DLL the engine
loads later (a renderer backend, an audio plug-in, a Steam module) keeps the
real `CreateFileW`, so its file opens escape redirection. Register for load
notifications:

```cpp
LdrRegisterDllNotification(0, &OnDllLoaded, nullptr, &g_cookie);
```

resolved dynamically from `ntdll`. Do the actual hooking off the notification
callback — the loader lock is held there, which is the same trap
`HookFileApis` already documents for `DllMain`.

### 1.9 [C] `GENERIC_WRITE` is not the only way to open a file for writing

`src/SMLoader.Shim/iat_hook.cpp:89`

```cpp
if (fileName && (access & GENERIC_WRITE) == 0)
```

`FILE_WRITE_DATA` (0x0002), `FILE_APPEND_DATA` (0x0004) and `MAXIMUM_ALLOWED`
(0x02000000) all grant write access without setting `GENERIC_WRITE`. A write
opened that way would be redirected into the loader's cache — the game would
write to the wrong file, which is precisely the outcome the comment above the
check says must be impossible.

```cpp
constexpr DWORD kWriteBits = GENERIC_WRITE | GENERIC_ALL | MAXIMUM_ALLOWED
                           | FILE_WRITE_DATA | FILE_APPEND_DATA;
if (fileName && (access & kWriteBits) == 0 && disposition == OPEN_EXISTING)
```

Gating on `OPEN_EXISTING` as well means a create or truncate can never be
redirected.

### 1.10 [C] `ConcurrentDictionary.GetOrAdd` does not guarantee the factory runs once

`src/SMLoader.Core/AssetPatcher.cs:53`

`Build` reads the file, runs every mod transform, and writes the cache file. Two
threads opening the same asset concurrently both run it, and both call
`File.WriteAllText` on the same cache path — a torn or locked write. Use
`GetOrAdd(path, static p => new Lazy<string?>(() => Build(p))).Value`, or a
per-path lock.

### 1.11 [C] ~~`assembly.GetTypes()` throws away a partially loadable mod~~ — RESOLVED

`src/SMLoader.Core/ModLoader.cs:62`

A mod referencing a type from a dependency that failed to resolve raises
`ReflectionTypeLoadException`, caught by the outer handler — so the whole mod is
skipped even though the `IMod` class itself was loadable.

```csharp
Type[] types;
try { types = assembly.GetTypes(); }
catch (ReflectionTypeLoadException ex)
{
    Logging.Error($"{name}: some types failed to load", ex);
    types = ex.Types.Where(t => t is not null).ToArray()!;
}
```

Applied as `ModLoader.GetLoadableTypes`.

### 1.12 [C] ~~One bad `IMod` aborts the remaining mods in the same assembly~~ — RESOLVED

Same file, lines 62-75. `Activator.CreateInstance` and `mod.OnLoad` sat inside a
single `try` wrapping the whole type loop. A throwing constructor in the first
type prevented the second from ever being seen, and the log reported it as a
single mod failure.

`LoadFrom` is now two phases: one `try` around loading the assembly and
enumerating its types — a failure there skips the mod — then a per-type `try`
around construction and `OnLoad`, which names the offending type in the log
rather than blaming the whole mod.

---

## 2. Hot-path performance

Three paths matter, in order: **`CreateFileW`** (every file the process opens),
**`client_onUpdate` → `smloader.*`** (every rendered frame), and
**`luaL_loadbufferx`** (every script compile, ~10× per world load per script).

### 2.1 [P] `new LuaState(L)` allocates on every Lua→C# call

`src/SMLoader.Api/Lua/LuaState.cs:21,153`

`LuaState` is a `sealed class` holding a single `nint`. Every trampoline
invocation allocates one. NoclipMod's Lua calls `smloader.isKeyDown` six times
plus `setPosition`, `isNoclip` and `noclipSpeed` per rendered frame — roughly ten
allocations per frame, ~1,500/s at 144 fps, purely to wrap a pointer. That is
gen-0 pressure inside a game process, and a gen-0 collection is exactly the kind
of sub-millisecond hitch a player perceives as stutter.

Make it a value type:

```csharp
public readonly struct LuaState(nint handle)
{
    public nint Handle { get; } = handle;
    // ...
}
```

`LuaFunction` stays `delegate int LuaFunction(LuaState lua)` unchanged, and the
trampoline allocates nothing. If a reference type is required for API reasons,
cache one instance per `lua_State` in a `[ThreadStatic]` field instead.

### 2.2 [P] Every memory read costs a `VirtualQuery`

`src/SMLoader.Core/ProcessMemory.cs:79-113`

`Read<T>`/`Write<T>` call `IsReadable`/`IsWritable`, which call `VirtualQuery` —
a transition into the kernel — before touching the address.

`PlaceCharacter` (`mods/NoclipMod/NoclipMod.cs`) performs about **30 guarded
writes plus two guarded reads per rendered frame**, and `MatchesPosition` adds
more. At 144 fps that is over 4,000 `VirtualQuery` calls per second on the
game's render thread, validating addresses that were already validated when the
character was bound and that cannot change while the object is alive.

Two complementary fixes:

- **Cache the region descriptors.** A small array of validated
  `(base, size, protect)` ranges checked with a binary search, invalidated on a
  generation counter (bumped by a `VirtualAlloc`/`VirtualFree` hook, or simply
  time-expired). Turns a syscall into three compares.
- **Offer an unchecked fast path.** Add `IMemory.CreateRegion(nint, int)`
  returning a handle validated once; reads and writes through the handle skip the
  query. Mods that have already proven a pointer — as NoclipMod does with
  `MatchesPosition` — opt in explicitly.

```csharp
public interface IMemoryRegion
{
    bool IsValid { get; }
    T Read<T>(int offset) where T : unmanaged;
    bool Write<T>(int offset, T value) where T : unmanaged;
}
```

### 2.3 [P] `IsGameFocused()` makes two USER32 calls per key check

`mods/NoclipMod/NoclipMod.cs:118-125` and the `IsGameFocused` helper

`isKeyDown` calls `IsGameFocused()`, which calls `GetForegroundWindow` +
`GetWindowThreadProcessId`. Six movement keys per frame plus the toggle key means
~14 window-manager calls per frame for a value that changes at most a few times a
second.

```csharp
private static uint _focusStamp;
private static bool _focused;

private static bool IsGameFocused()
{
    uint now = (uint)Environment.TickCount;
    if (now - _focusStamp < 100) return _focused;
    _focusStamp = now;
    // ... real check, cache into _focused
    return _focused;
}
```

Better still: expose a single `smloader.readKeys(vk1, vk2, ...)` returning a
bitmask, so one managed call covers the whole frame's input.

### 2.4 [P] `smloader.noclipKey()` deserialises JSON every fixed tick

`mods/NoclipMod/NoclipMod.cs:154-158`, `src/SMLoader.Core/ModConfig.cs:45-61`

```csharp
host.AddLuaFunction("noclipKey", lua => { lua.Push((long)host.Config.Get(KeyOption, DefaultKey)); return 1; });
```

`Config.Get` takes a lock, looks up a `JsonElement` and calls
`element.Deserialize<T>(Options)` — a full `System.Text.Json` converter dispatch,
per call. The Lua side calls this every fixed tick (40 Hz), and `noclipSpeed()`
every rendered frame while flying.

Cache the typed value in the mod and refresh it from `IModSettings.Changed`:

```csharp
_noclipKey = host.Settings.Declare<int>(/* ... */);
host.Settings.Changed += key =>
{
    if (key == KeyOption) _noclipKey = host.Settings.Get(KeyOption, DefaultKey);
};
```

The same applies inside `ModConfig`: hold a `Dictionary<string, object?>` of
already-materialised values alongside the `JsonElement` map.

### 2.5 [P] Every setting write does a synchronous full-file JSON serialise

`src/SMLoader.Core/ModSettings.cs:33-34,76`

`Declare` saves once per declared setting at startup (N file writes), and
`WriteBoxed` saves on **every** change — including each click of the panel's
`+`/`-` stepper, which runs on the game thread. A synchronous file write inside a
click handler is a guaranteed frame spike.

- Mark dirty and flush on a one-second timer plus on process exit.
- Expose `IModConfig.SaveAsync()`; make `Save()` the explicit-flush escape hatch.
- Batch `Declare` — one save after `ModLoader.LoadAll` completes.

### 2.6 [P] Script transforms re-run on every compile with no caching

`src/SMLoader.Core/ScriptPatcher.cs:44-82` and `Entry.cs:169-201`

For each of the ~10 `lua_State`s a world load creates, `CreativePlayer.lua` is:
UTF-8 decoded in full → passed through every registration → string-concatenated
by `ScriptLoadContext.Append` → UTF-8 re-encoded → copied into `AllocHGlobal`.
`CreativePlayer.lua` is a large file and there are already two registrations
appending to it (SettingsPanel and NoclipMod), so this is repeated work sitting
directly on the world-load critical path.

`AssetPatcher` already caches by path — `ScriptPatcher` should cache by
`(name, sourceHash)`:

```csharp
private static readonly ConcurrentDictionary<(string Name, ulong Hash), byte[]> Cache = new();
// key on XxHash3.HashToUInt64(sourceBytes) — computable without decoding
```

Return the cached UTF-8 bytes directly and skip the decode/encode round trip
entirely on a hit.

### 2.7 [P] `ScriptLoadContext.Append` is O(n) string concatenation

`src/SMLoader.Api/ScriptPatch.cs:26-29`

```csharp
public void Append(string lua) => Source += Environment.NewLine + lua + Environment.NewLine;
```

Two intermediate strings per append, each a full copy of the script. With several
mods appending to the same large file this is quadratic. Back the context with a
`StringBuilder` and materialise `Source` lazily:

```csharp
private StringBuilder? _builder;
public string Source
{
    get => _builder?.ToString() ?? _source;
    set { _source = value; _builder = null; }
}
public void Append(string lua) => (_builder ??= new StringBuilder(_source)).Append('\n').Append(lua).Append('\n');
```

### 2.8 [P] `ScriptPatcher.WantsScript` locks and logs on every compile

`src/SMLoader.Core/ScriptPatcher.cs:27-41`

A lock, a `HashSet.Add`, and on a miss a linear scan of registrations — for every
chunk the engine compiles, including the ones no mod cares about. The
`SeenScripts` set also grows without bound and triggers a **file write** the
first time each name is seen.

- Registrations are append-only: publish an immutable `Registration[]` with
  `Volatile.Write` and read it lock-free.
- Skip the whole path with a single field read when there are no registrations.
  `AssetPatcher.Resolve` already does exactly this — copy the pattern.
- Demote the `script: {name}` line to a verbose level (§11.1).

### 2.9 [P] `RedirectFileOpen` takes a global mutex on every file open in the process

`src/SMLoader.Shim/dllmain.cpp:153-164`

```cpp
FileOpenCallback callback = nullptr;
{ std::lock_guard<std::mutex> lock(g_scriptMutex); callback = g_fileOpen; }
```

A pointer read guarded by a process-wide mutex, on the hottest path the shim
touches. Uncontended it is cheap; under Scrap Mechanic's concurrent asset
streaming the shared cache line bounces between cores for no reason. The same
pattern appears in `TransformScript`, `SeedScriptEnvironment`,
`ReleaseScriptSource` and `NotifyLuaRunning`.

All five callbacks are write-once. Use relaxed atomics:

```cpp
std::atomic<FileOpenCallback> g_fileOpen{nullptr};
// write:  g_fileOpen.store(cb, std::memory_order_release);
// read:   auto cb = g_fileOpen.load(std::memory_order_acquire);
```

### 2.10 [P] Managed code runs inside `CreateFileW`, on arbitrary threads

`src/SMLoader.Core/Entry.cs:243-278`

`OnFileOpen` is a managed callback reached from any thread that opens a file,
including engine worker threads that have never run managed code. Each first
entry costs a CLR thread attach; each call can allocate (`PtrToStringUni`
allocates a string; `GetOrAdd` may allocate a node) and can therefore trigger a
GC — inside a file-open call, potentially while a loader lock is held elsewhere.

Add a native-side prefilter so the vast majority of opens never cross into
managed code at all:

```cpp
// Built once from the registered patterns; a case-folded substring set.
if (!smloader::MightMatch(fileName))
    return g_originalCreateFileW(fileName, access, share, security, disposition, flags, templateFile);
```

Even a crude filter — "does the path contain `\Data\` and end in `.layout`,
`.json` or `.lua`" — removes DLL loads, save files, shader caches and the
loader's own log from the managed path entirely.

### 2.11 [P] `InputScanner.FirstPressedKey` makes 247 syscalls per call

`src/SMLoader.Core/InputScanner.cs:12-23`

One `GetAsyncKeyState` per virtual-key code, called every frame while the panel
is awaiting a rebind. `GetKeyboardState` fills all 256 states in a single call:

```csharp
Span<byte> state = stackalloc byte[256];
if (!GetKeyboardState(ref MemoryMarshal.GetReference(state))) return 0;
for (int key = 0x07; key <= 0xFE; key++)
    if (key is not (0x10 or 0x11 or 0x12) && (state[key] & 0x80) != 0) return key;
return 0;
```

Note `GetKeyboardState` reflects the calling thread's message queue, so it must
run on the game's UI thread — which the panel poll already does.

### 2.12 [P] `[SuppressGCTransition]` on the trivial Lua bindings

`src/SMLoader.Api/Lua/LuaNative.cs`

`lua_gettop`, `lua_settop`, `lua_type`, `lua_toboolean`, `lua_tonumber`,
`lua_tointeger`, `lua_objlen`, `lua_pushnil`, `lua_pushnumber`,
`lua_pushinteger`, `lua_pushboolean` and `lua_pushvalue` are each a handful of
instructions with no allocation, no blocking and no callback into managed code —
exactly the profile `[SuppressGCTransition]` exists for. It removes the
cooperative→preemptive GC mode transition, which is most of the cost of a
P/Invoke this small.

```csharp
[LibraryImport(Lib), SuppressGCTransition]
public static partial int lua_gettop(nint L);
```

Do **not** apply it to `lua_pcall`, `luaL_loadstring`, `lua_gettable`,
`lua_settable`, `lua_setfield`, `lua_getfield` or anything that can invoke a
metamethod, run Lua code, raise an error, or block — those can take arbitrarily
long and must stay preemptible.

### 2.13 [P] `LuaState.Push(string)` allocates a byte array per push

`src/SMLoader.Api/Lua/LuaState.cs:64-72`

```csharp
byte[] utf8 = Encoding.UTF8.GetBytes(value);
```

Stack-allocate for the common short case:

```csharp
public unsafe void Push(string value)
{
    int max = Encoding.UTF8.GetMaxByteCount(value.Length);
    byte[]? rented = max > 256 ? ArrayPool<byte>.Shared.Rent(max) : null;
    Span<byte> buffer = rented ?? stackalloc byte[256];
    int written = Encoding.UTF8.GetBytes(value, buffer);
    fixed (byte* p = buffer) lua_pushlstring(Handle, (nint)p, (nuint)written);
    if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
}
```

### 2.14 [P] `ProcessMemory.ScanRange` is a naive nested loop over the whole image

`src/SMLoader.Core/ProcessMemory.cs:200-224`

Byte-by-byte with an inner comparison loop over a multi-megabyte module. Two
cheap improvements:

- Skip to the first **non-wildcard** byte of the pattern and use
  `MemoryMarshal.CreateReadOnlySpan(...).IndexOf(firstByte)` — vectorised — then
  verify the remainder. Typically an order of magnitude faster.
- Precompute a Boyer-Moore-Horspool skip table for the wildcard-free suffix.

It runs twice at startup today, so this is latency rather than throughput — but
startup latency inside a game process is exactly what a player notices.

### 2.15 [P] Console output does three API calls per line and flushes each time

`src/SMLoader.Core/GameConsole.cs:47-80`

Setting and restoring `Console.ForegroundColor` is two `SetConsoleTextAttribute`
calls per line, and `AutoFlush = true` forces a `WriteFile` per line. Write the
ANSI colour escape inline (`\e[35m…\e[0m`) after enabling
`ENABLE_VIRTUAL_TERMINAL_PROCESSING`, keep `AutoFlush = false`, and flush on a
timer.

### 2.16 [P] Per-frame closure allocation in the injected Lua

`src/SMLoader.Core/SettingsPanel.cs:228`

```lua
local ok, err = pcall( function()   -- a new closure every frame
```

Lua allocates a closure for this on every `client_onUpdate`. Hoist it:

```lua
local smloader_tick
smloader_tick = function( self, dt ) --[[ ... ]] end
-- ...
local ok, err = pcall( smloader_tick, self, dt )
```

Likewise `smloader_step` is defined *inside* the `for i = 1, 32` loop
(`SettingsPanel.cs:190`), creating 32 identical closures — hoist it out.

### 2.17 [P] The panel registers 96 handlers regardless of how many settings exist

`src/SMLoader.Core/SettingsPanel.cs:171-203` — three closures ×
`SMLOADER_MAX_ROWS` (32), assigned onto `CreativePlayer`, per compile, per
`lua_State`. Register only `smloader.settingCount()` rows and re-register if the
count grows.

### 2.18 [P] `SettingsRegistry.All` allocates an array per access

`src/SMLoader.Core/SettingsRegistry.cs:33-40`, called once per row inside
`settingAt`. Publish an immutable snapshot on write instead of copying on read.

### 2.19 [P] `Process.GetCurrentProcess()` is allocated and never disposed

`src/SMLoader.Core/ProcessMemory.cs:30,155`. In `FindPattern` it also
materialises the entire `Modules` collection per call. Cache the main module
base/size once (the constructor already does) and use `GetModuleHandleW` +
`GetModuleInformation` for other modules instead of the `Process` API.

### 2.20 [P] `BootThread` burns a thread for three minutes

`src/SMLoader.Shim/dllmain.cpp:186-215` — 200 × `Sleep(5)` then sleeps out to
the 180-second checkpoint. Harmless, but the reapply loop can exit as soon as
the slot has been stable for, say, 100 ms, and the checkpoints can hang off a
waitable timer rather than a dedicated thread.

---

## 3. Memory and lifetime

### 3.1 [C] `LuaState.Registered` grows without bound

`src/SMLoader.Api/Lua/LuaState.cs:23,75-82`

```csharp
private static readonly ConcurrentDictionary<int, LuaFunction> Registered = new();
private static int _nextId;
```

Every `Push(LuaFunction)` mints a new id and inserts a permanent entry. Nothing
is ever removed. `LuaApi.GetOrCreateTable` pushes every registered function for
every `lua_State`, and `LuaApi.Add` clears `TableRefs` so the tables are rebuilt
from scratch whenever a mod registers another function. Ten functions × ten
states per world load × every world load in a session — and the dictionary only
grows. Each entry roots a delegate, which roots the mod instance.

Give each function a stable id keyed on the function itself:

```csharp
private static readonly ConcurrentDictionary<LuaFunction, int> Ids = new(ReferenceEqualityComparer.Instance);
private static readonly ConcurrentDictionary<int, LuaFunction> ById = new();

int id = Ids.GetOrAdd(function, static f =>
{
    int i = Interlocked.Increment(ref _nextId);
    ById[i] = f;
    return i;
});
```

A function pushed into ten states now costs one entry, not ten.

### 3.2 [C] Registry references are never released

`src/SMLoader.Core/LuaApi.cs:77` — `luaL_ref` with no matching `luaL_unref`.
Each rebuild leaks a registry slot **and** keeps the old table (and its closures)
alive in the Lua GC. The `lua_close` hook in §1.3 fixes both.

### 3.3 [P] The asset-resolution cache is unbounded

`src/SMLoader.Core/AssetPatcher.cs:23,53`

Once any mod registers an asset transform, **every path the process ever opens**
gets an entry — including misses. Over a long session with worlds streaming in
and out, that is tens of thousands of path strings held forever.

Bound it: an LRU of a few thousand entries, or cache only the hits and use the
native prefilter from §2.10 to make misses cheap without needing to remember
them.

### 3.4 [P] The cache directory is never cleaned

`AssetPatcher.CachePathFor` writes into `dist/Cache/<hash8>/<name>` and nothing
ever deletes it. Stale entries survive a mod being uninstalled, and a stale
`.layout` will still be served. Stamp each entry with the source file's mtime
plus a hash of the active registration set, and sweep the directory at boot.

### 3.5 [C] `g_pendingStates` is unbounded if the CLR never boots

`src/SMLoader.Shim/dllmain.cpp:17,52-67`

If `clr::Start()` fails — no .NET runtime installed, a bad
`runtimeconfig.json` — every `lua_State` the game creates is pushed into a vector
that is never drained. Cap it (16 is generous) and log once when the cap is hit.

### 3.6 [Q] `MemoryStream` over `File.ReadAllBytes` copies the assembly twice

`src/SMLoader.Core/ModLoader.cs:85`. Minor:
`new MemoryStream(bytes, writable: false)` avoids one copy. The streams are
correctly disposed after `LoadFromStream` returns.

### 3.7 [E] `SeenScripts` grows without bound

`src/SMLoader.Core/ScriptPatcher.cs:14`. Bounded in practice by the number of
distinct chunk names, but a dynamically generated chunk name would not be. Cap
it, or drop it once logging is levelled (§2.8, §11.1).

---

## 4. Concurrency and the native/managed boundary

### 4.1 [C] Detour globals are plain, non-atomic pointers

`src/SMLoader.Shim/iat_hook.cpp:14-30`

`g_original`, `g_originalPcall`, `g_originalSetfenv`, `g_originalLoad`,
`g_originalCreateFileW` and their slots are written by the boot thread
(`Reapply`, `HookFileApis`) and read by the game's threads inside the detours,
with no synchronisation. On x64 a naturally aligned pointer load/store will not
tear, but the compiler is free to reorder or cache the read across the detour
body. `std::atomic<T>` with acquire/release costs nothing at runtime on x64 and
makes both the intent and the guarantee explicit.

The `Reapply` sequence is the sharp case:

```cpp
g_original = reinterpret_cast<luaL_newstate_t>(*g_slot);   // (1)
WriteSlot(g_slot, &Detour_luaL_newstate);                  // (2)
```

Between (1) and (2), a call arriving on another thread goes through the *real*
function without notifying us — a lost `lua_State`. Release-store at (1) before
(2), and stop reapplying as soon as the slot has been stable (§2.20) to shrink
the window to nothing.

### 4.2 [C] `RetirePcallHook` clears the slot pointer before restoring it

`src/SMLoader.Shim/iat_hook.cpp:312-321`

```cpp
void** slot = g_pcallSlot;
g_pcallSlot = nullptr;
WriteSlot(slot, reinterpret_cast<void*>(g_originalPcall));
```

Two threads entering `Detour_lua_pcall` simultaneously can both read a non-null
`g_pcallSlot`, both null it, and both write. Harmless here (the same value is
written twice) but the pattern is fragile. Use
`std::atomic_exchange(&g_pcallSlot, nullptr)` and act only on a non-null result.

### 4.3 [C] `GameConsole._ready` is a racy, non-volatile guard

`src/SMLoader.Core/GameConsole.cs:13-19`

```csharp
private static bool _ready;
public static void Ensure() { if (_ready) return; _ready = true; /* ... */ }
```

Two threads logging concurrently can both pass the check and both call
`AllocConsole`. Use `Interlocked.Exchange(ref _ready, 1) != 0`, or
`LazyInitializer.EnsureInitialized`.

### 4.4 [C] `LuaApi.Seed` holds a lock across calls into the Lua VM

`src/SMLoader.Core/LuaApi.cs:62-81`

`GetOrCreateTable` performs `NewTable`, N × `SetFunction` and `luaL_ref` while
holding `Gate` — the same lock `Add` takes. If a mod ever called
`AddLuaFunction` from inside a Lua callback (entirely plausible: a "register my
commands on world load" pattern), that is a self-deadlock across a non-reentrant
boundary. Build the function list into an immutable snapshot outside the lock and
take the lock only for the dictionary write.

### 4.5 [E] `_resolvingAsset` guards recursion but not the same file from another thread

`src/SMLoader.Core/Entry.cs:240-241` — `[ThreadStatic]`, which is right for the
recursion it was written for. But `AssetPatcher.Build` calls `Logging.Write`,
which enters `CreateFileW` on the same thread (guarded), while a *different*
thread inside `Logging.Write` for the same file is unguarded and passes through
`AssetPatcher.Resolve`. It cannot recurse infinitely, but the log path gets
cached as a miss and pays the managed round trip. Add the loader's own directory
to a native-side exclusion list (§2.10).

### 4.6 [Q] `volatile long` + `Interlocked` is not the modern idiom

`src/SMLoader.Shim/dllmain.cpp:20,22`. `std::atomic<long>` says what is meant and
carries defined ordering; `volatile` in C++ carries no threading semantics at
all.

### 4.7 [C] `_resolveCharacter` is published without a barrier

`mods/NoclipMod/NoclipMod.cs` — assigned on the loader thread by
`CaptureCharacterResolver`, read on the game thread in `bindCharacter`, with no
`volatile`. In practice `OnLoad` completes long before, but a
`Volatile.Write`/`Volatile.Read` pair costs nothing and documents the handoff.

### 4.8 [E] `Entry._splash` is a mutable static crossing threads

`src/SMLoader.Core/Entry.cs:33` — built on the boot thread, read on the game
thread in `EchoSplashToGameLog`. Publish it once as an
`ImmutableArray<string>` with `Volatile.Write`.

---

## 5. Error handling and failure isolation

The project already does the hard part well: every native→managed callback is
wrapped, `LuaState.ErrorSink` exists, transforms are individually caught, and
logging swallows its own failures. The gaps are at the edges.

### 5.1 [E] ~~No global unhandled-exception handler~~ — RESOLVED

Nothing subscribes to `AppDomain.CurrentDomain.UnhandledException` or
`TaskScheduler.UnobservedTaskException`. An exception on a background thread
inside SMLoader terminates the **game process** with no entry in `smloader.log` —
indistinguishable from a game crash, and the player will blame the game.

```csharp
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Logging.Error("FATAL unhandled exception", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) =>
    { Logging.Error("unobserved task exception", e.Exception); e.SetObserved(); };
```

Installed as `Entry.InstallProcessWideHandlers`, called as the first statement of
`Boot` — ahead of the null-context check and of `Logging.Initialize`, so the
window in which a background exception is invisible is as small as it can be. The
install is `Interlocked`-guarded, since a second `Boot` would otherwise subscribe
twice. `IsTerminating` is logged, because in the terminating case that line is the
only record there will be.

### 5.2 [E] `Boot`'s own catch can throw

`src/SMLoader.Core/Entry.cs:98-102` — the handler calls `Logging.Error`, but if
the failure *was* `Logging.Initialize`, `_path` is a bare relative filename and
the write may fail. It is caught inside `Logging.Write`, so this is safe today —
make it explicit with a fallback to `OutputDebugStringW` via P/Invoke, which
cannot fail.

### 5.3 [E] A failed CLR boot leaves the hooks installed and inert

`src/SMLoader.Shim/dllmain.cpp:198` — `clr::Start()`'s return value is discarded.
The detours stay in place, adding cost to every `luaL_newstate`, `lua_pcall`,
`luaL_loadbufferx`, `lua_setfenv` and `CreateFileW` for the rest of the session
while doing nothing at all. On failure, **unhook everything** and log one clear
line. A player whose .NET install is broken should get a game that runs exactly
as fast as an unmodded one.

### 5.4 [E] `Declare<T>`'s `Convert.ChangeType` can throw and kill a mod load

`src/SMLoader.Core/ModSettings.cs:32`

```csharp
T current = _config.Get(setting.Key, (T)Convert.ChangeType(setting.Default, typeof(T)));
```

A mod declaring `Declare<int>(new ModSetting(..., SettingKind.Key, "F2"))` throws
`InvalidCastException` out of `OnLoad`, and the mod never loads. Validate and
report it as a mod authoring error instead:

```csharp
if (!TryConvert<T>(setting.Default, out T fallback))
{
    Logging.Error($"[{_modName}] setting '{setting.Key}': default '{setting.Default}' " +
                  $"is not a {typeof(T).Name}; using default({typeof(T).Name})");
    fallback = default!;
}
```

Also validate that `Kind` and `T` agree (`SettingKind.Toggle` with `T = string`
is a bug the loader can catch at declaration time), and that a `Choice` setting
has a non-empty `Choices` list.

### 5.5 [E] ~~`ModConfig.Save` is not crash-atomic~~ — RESOLVED

`src/SMLoader.Core/ModConfig.cs:75` — `File.WriteAllText` truncates first. A crash
or a force-quit (common with games) between truncate and write leaves a zero-byte
or half-written config. `Load` handles the corrupt case gracefully — but the
player's settings are gone.

```csharp
string temp = _path + ".tmp";
File.WriteAllText(temp, json);
File.Move(temp, _path, overwrite: true);   // atomic rename on NTFS
```

Applied, with the temp file deleted on a failed write so a full disk does not
leave `.tmp` files accumulating beside the configs.

### 5.6 [E] `Get<T>` swallows the deserialisation error silently

`src/SMLoader.Core/ModConfig.cs:56-59` — `catch { return fallback; }`. A setting
that quietly reverts to its default every launch is very hard to diagnose. Log
once per key.

### 5.7 [E] Keybinds are loaded once and a failure is permanent

`src/SMLoader.Core/GameKeybinds.cs:24-28`

```csharp
if (!_loaded) { _loaded = true; Load(); }
```

`_loaded` is set *before* `Load()`, so a transient failure — the game writing the
file at that instant — is cached for the session. Distinguish "loaded" from
"attempted", and re-check the file's mtime periodically so rebinding mid-session
works.

### 5.8 [E] `Entry.OnFileOpen`'s bare `catch` hides real faults

`src/SMLoader.Core/Entry.cs:270-273` — `catch { return 0; }` on the hottest
managed path. Correct as a policy, but a *persistent* failure is invisible. Count
failures, log the first, then one line per thousand.

### 5.9 [E] No graceful shutdown

Nothing runs at process exit: pending config writes are not flushed, log buffers
are not drained, IAT hooks are not removed, `AllocHGlobal` buffers are not
audited. Add a `DLL_PROCESS_DETACH` path in the shim (unhook, flush) and a
`ProcessExit` handler on the managed side (flush configs and the log).

### 5.10 [E] Missing `lua_checkstack` before multi-value pushes

`src/SMLoader.Core/SettingsRegistry.cs:56-75` pushes six values. Lua guarantees
`LUA_MINSTACK` (20) free slots on entry to a C function, so six is safe today —
but a helper pushing a variable number (a future `settingChoices`) is not. Add
`lua.EnsureStack(n)` wrapping `lua_checkstack` and use it in the API surface.

### 5.11 [E] `ToStringValue` on a number mutates the value in place

`src/SMLoader.Api/Lua/LuaState.cs:38-47` — `lua_tolstring` converts a number to a
string *in the stack slot*. Doing that to a key during a `lua_next` traversal
breaks the traversal. Document it on the method and add a `ToStringValueSafe`
that pushes a copy first.

### 5.12 [E] The `smloader` table can be `nil` in injected Lua and nothing checks

`src/SMLoader.Core/SettingsPanel.cs:111` calls `smloader.settingAt(i)` after only
checking that `smloader.settingCount` exists. If seeding failed — no
`lua_setfenv` slot, a case the shim already logs at `iat_hook.cpp:269` — the
panel throws inside a `pcall` and silently does nothing. Guard each call, and
surface "SMLoader could not reach this script's environment" in the log once.

---

## 6. Security and the trust model

The honest headline: **a mod is arbitrary native-capable code running inside the
game process with the player's full user privileges.** `IMemory` hands it
unrestricted read/write over the process, `PatchAsset` lets it redirect any file
read, and assembly loading is unsandboxed by construction. That is a legitimate
design for a mod loader — but it should be *stated*, not implied.

### 6.1 [S] Document the trust model in the README and at first run

Add a `SECURITY.md` and a paragraph in the README:

> Mods run as native code inside ScrapMechanic.exe with your user account's full
> privileges. A mod can read and write any file you can, make network
> connections, and modify any memory in the game. Only install mods from sources
> you trust. SMLoader does not sandbox mods and cannot.

Print the same warning once at load, listing the mods being loaded.

### 6.2 [S] No integrity check on anything that gets loaded or injected

- `Program.cs:38` takes `--dist <path>` and injects `<dist>/SMLoader.Shim.dll`
  into the game with no verification. A wrong or substituted `dist` folder is a
  code-execution vector dressed up as a typo.
- `ModLoader` loads every DLL under `Mods/` with no signature, hash or manifest
  check.

Minimum viable hardening:

- Verify the shim's Authenticode signature, or a pinned SHA-256, before
  `CreateRemoteThread`; refuse otherwise.
- Support an opt-in `Mods/allowed.json` hash allowlist, with an `--any-mod`
  override for developers.
- Add a `--no-mods` safe mode that boots the loader with zero mods, so a player
  can tell whether a crash is SMLoader or a mod.

### 6.3 [S] Command-line construction is not quote-safe

`src/SMLoader.Launcher/Injector.cs:26`

```csharp
var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");
```

`arguments` is `string.Join(' ', passThrough)` with no escaping. An argument
containing a quote changes the parse of everything after it. Low severity — the
user supplies their own arguments — but it is one helper away from correct:
apply the standard `CommandLineToArgvW` quoting rules per argument.

### 6.4 [S] ~~W^X is violated during the guard patch~~ — RESOLVED with [§1.6](#16-c-unprotect-ignores-failure-and-protect-can-leave-a-page-rwx)

`mods/NoclipMod/NoclipMod.cs` `PatchGuard` (via `ProcessMemory.Unprotect`) sets
`PAGE_EXECUTE_READWRITE` on a page of the game's `.text`. Combined with §1.6,
that page can stay RWX for the session. An RWX page in a game process is exactly
what an exploit wants and exactly what anti-cheat heuristics flag.

The page does not need to be executable *while being written*. Use
`PAGE_READWRITE`, then restore the captured original protection, and verify the
restore succeeded:

```csharp
if (!memory.TryUnprotect(jump, 2, out uint previous)) { host.LogError(/* ... */); return; }
try { memory.WriteBytes(jump, new byte[] { 0x90, 0x90 }); }
finally { if (!memory.Protect(jump, 2, previous)) host.LogError($"page left writable at 0x{jump:X}"); }
```

### 6.5 [S] Code is patched with no `FlushInstructionCache` and no thread suspension

Same site. x86/x64 keeps the instruction cache coherent with stores, so this
works in practice — but the documented contract requires
`FlushInstructionCache(GetCurrentProcess(), addr, size)` after modifying code,
and another thread could be executing the exact two bytes being replaced. For an
aligned two-byte write the risk is very low; add the flush, document the
assumption, and suspend other threads if this ever grows beyond two bytes.

### 6.6 [S] The pattern-scan → `GetDelegateForFunctionPointer` chain is an indirect jump to a scanned address

`mods/NoclipMod/NoclipMod.cs` `CaptureCharacterResolver` derives a function
pointer from a byte offset relative to a pattern match and calls it with an
assumed signature. A false-positive match jumps into arbitrary code with a
fabricated ABI. The code already validates the `0xE8` opcode — extend that:

- Verify the resolved target lies inside the main module's `.text` range.
- Verify the target's first bytes look like a function prologue.
- Verify the pattern matched **exactly once** in the module. A second match means
  the signature is not unique and must not be trusted.

That last one is the important one, and it applies to `PatchGuard` too. Give
`FindPattern` a variant that reports the match count, and refuse to patch on
`> 1`.

### 6.7 [S] MD5 for the cache key

`src/SMLoader.Core/AssetPatcher.cs:116`. Not a security boundary — but it will
trip every static analyser and every corporate scanner. `XxHash128` (in
`System.IO.Hashing`) is faster and carries no baggage; if a cryptographic hash is
genuinely wanted, `SHA256.HashData`. Also `path.ToLowerInvariant()` should be
`ToUpperInvariant()` for correct invariant case folding — the Turkish-I rule cuts
the other way — or drop the string allocation entirely and hash with an
ordinal-ignore-case hasher.

### 6.8 [S] Redirected reads are unbounded in what they can point at

`AssetPatcher.Build` returns a path that the shim hands straight to
`CreateFileW`. `Path.GetFileName` sanitises the leaf and the directory is the
loader's own cache, so traversal is not currently reachable — but the invariant
is implicit. Make it explicit: assert the returned path is rooted under
`_cacheDirectory` before returning it.

### 6.9 [S] `AllocConsole` in a game process has consequences worth flagging

`src/SMLoader.Core/GameConsole.cs:23` — the allocated console steals focus on
creation, and **closing that console window terminates the game process**. Add a
`SMLOADER_NO_CONSOLE` environment variable / config toggle, and remove the
console's close button via `GetSystemMenu` + `DeleteMenu` so a player cannot end
their session by tidying their taskbar.

### 6.10 [S] Anti-cheat and multiplayer

The README says single-player only. Make the loader enforce what it can: detect a
multiplayer session (the engine exposes it to Lua) and disable script patching
and memory writes, or at minimum log a prominent warning. A player who gets
banned from a server will not remember the caveat in the README.

---

## 7. Native shim hardening

### 7.1 [E] `FindIatSlot` walks arbitrary module headers with no exception guard

`src/SMLoader.Shim/iat_hook.cpp:112-152` dereferences `e_lfanew`, the NT headers,
the import directory and every name thunk of **every module in the process**
during `HookCreateFileEverywhere`. A packed, hollowed or partially-mapped module —
injected overlays are common — will fault, on the boot thread, with no handler.

```cpp
__try { /* header walk */ }
__except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
```

Plus explicit bounds: check `dir.VirtualAddress + dir.Size` against
`nt->OptionalHeader.SizeOfImage`, and check each thunk RVA the same way.

### 7.2 [E] No `IMAGE_NT_OPTIONAL_HDR64_MAGIC` check

Same function uses `IMAGE_NT_HEADERS` (the 64-bit form in an x64 build) without
verifying `nt->OptionalHeader.Magic`. A 32-bit module mapped as data would be
misparsed. Cheap to check.

### 7.3 [E] `GetModuleBaseNameW` return value is discarded

`iat_hook.cpp:195` — on failure `name` stays zeroed and the `_wcsicmp` exclusions
silently do not match, so `ntdll` could be hooked after all, which is exactly
what the comment above says must never happen. Check the return and skip the
module on failure.

### 7.4 [E] The shim never unhooks

There is no `DLL_PROCESS_DETACH` handling (`dllmain.cpp:221` returns early for
every reason but attach). If the shim is ever unloaded — by a debugger, by a
tool, by a future `FreeLibrary` — every patched IAT slot points into freed memory
and the process dies on the next Lua call. Add a detach path that restores all
slots.

### 7.5 [Q] `log::Write` truncates silently

`src/SMLoader.Shim/log.cpp:33,42` — a 1024/1200-byte buffer with `_TRUNCATE`.
That is the right choice, but a truncated line gives no indication it was
truncated. Append `…` when `_vsnprintf_s` returns `-1`.

### 7.6 [P] `log::Write` opens and closes the file per line

`log.cpp:55-62`. Keep a single handle open for the process lifetime — opened with
`FILE_SHARE_READ | FILE_SHARE_WRITE`, written with `FILE_APPEND_DATA`, which is
atomic across processes — and flush on a timer. See §11.2.

### 7.7 [Q] Build flags are not hardened

`src/SMLoader.Shim/CMakeLists.txt` sets `/W4 /permissive-` and nothing else. For
a DLL that gets injected into someone else's process:

```cmake
target_compile_options(SMLoader.Shim PRIVATE
    /W4 /WX /permissive- /GS /guard:cf /Qspectre /sdl
    $<$<CONFIG:Release>:/O2 /Oi /GL /Zi>)
target_link_options(SMLoader.Shim PRIVATE
    /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf
    $<$<CONFIG:Release>:/LTCG /DEBUG /OPT:REF /OPT:ICF>)
set_property(TARGET SMLoader.Shim PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
```

The static CRT (`/MT`) matters most: it removes the VC++ 2015-2022
redistributable as a deployment prerequisite. A user without it currently gets
`LoadLibraryW` returning 0 and the launcher's message "The target process refused
to load …" with nothing explaining why.

`/Zi /DEBUG` in Release produces a PDB, which is what makes a user-submitted
crash dump actionable.

### 7.8 [Q] No version resource on the DLL

Add a `.rc` with `FILEVERSION`, `CompanyName`, `FileDescription` and
`OriginalFilename`. An unsigned, version-less DLL injected into a game is the
first thing security software flags.

---

## 8. API design and mod compatibility

### 8.1 [C] There is no API version handshake

`IMod.Version` describes the *mod*. Nothing checks that the mod was built against
a compatible `SMLoader.Api`. A mod compiled against an older API that has since
lost a member fails with `MissingMethodException` at an arbitrary later moment,
inside a Lua callback, where the trampoline swallows it.

```csharp
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SMLoaderApiVersionAttribute(int major, int minor) : Attribute
{
    public int Major { get; } = major;
    public int Minor { get; } = minor;
}
```

Emitted automatically by a props file mods import; checked by `ModLoader` before
`Activator.CreateInstance`, with a clear "NoclipMod targets SMLoader API 1.0,
this loader provides 2.0" message.

### 8.2 [Q] No mod metadata, dependencies, or load order

Mods are discovered by directory scan and loaded in
`Directory.EnumerateDirectories` order — filesystem order, which is neither
stable nor meaningful. Two mods both appending to `CreativePlayer.lua` and both
wrapping `client_onUpdate` (SettingsPanel and NoclipMod do exactly this today)
get a non-deterministic nesting order.

Add a `mod.json` per mod:

```json
{
  "id": "noclip",
  "name": "NoclipMod",
  "version": "1.1.0",
  "apiVersion": "1.0",
  "dependencies": { "smloader": ">=0.1.0" },
  "loadAfter": ["settings-panel"]
}
```

and topologically sort before loading. Give `PatchScript` an explicit
`int priority` too, so append order is declared rather than emergent.

### 8.3 [Q] Duplicate mod names are not detected

`ModLoader` will happily load two mods called `NoclipMod`; they then share a
config file (`Config/NoclipMod.json`) and collide in `SettingsRegistry`. Reject
duplicates with a clear error.

### 8.4 [Q] Mods cannot be unloaded or reloaded

`ModLoadContext` is `isCollectible: false` (`ModLoader.cs:106`). The
load-from-memory trick already avoids file locking, so making the context
collectible and supporting hot reload is within reach — and would pair naturally
with the game's own `-dev` script hot-reload. It requires unregistering Lua
functions, script patches, asset patches and settings entries, all of which are
currently append-only. Worth designing for even if not implemented now: give
every registration a disposable handle.

```csharp
IDisposable PatchScript(string pathContains, Action<ScriptLoadContext> patch);
```

### 8.5 [Q] `IMemory` has no way to express "this pointer is still valid"

Mods cache `nint` character pointers across frames (`NoclipMod._character`) with
no way to learn the object was destroyed. Add a cheap validity token, or a
`LuaStateDestroyed` / `WorldUnloaded` event mods can hook to drop their caches.
Today, leaving a world and joining another leaves NoclipMod holding stale
pointers that `IsWritable` may still consider perfectly valid — and it will write
through them.

### 8.6 [Q] `ModSetting.Default` is `object`

Boxing plus a runtime `Convert.ChangeType` where the type is statically known. A
generic `ModSetting<T>` (with a non-generic base for the registry) removes both
the boxing and the entire class of error in §5.4.

### 8.7 [Q] The panel's own hotkey is hard-coded

`src/SMLoader.Core/SettingsPanel.cs:39` — `SMLOADER_PANEL_KEY = 0x79` while every
mod setting is rebindable. The loader should declare its own settings through the
same mechanism it offers mods; it is the best possible dogfood test of the API.

### 8.8 [Q] `TeleportOption` is read but never declared

`mods/NoclipMod/NoclipMod.cs:201` reads `teleportMovement` from config while
`noclipKey` and `noclipSpeed` are declared settings. Inconsistent — declare it as
a `Toggle` so it shows up in the panel like everything else.

### 8.9 [Q] Injected Lua uses undeclared globals as a namespace

`SMLOADER_KEY_DOWN`, `SMLOADER_PANEL_ALIVE`, `SMLOADER_PANEL_NOCL` and
`SMLOADER_PANEL_ERR` are written into the script environment. They work, and the
comments explain why script-locals were wrong for the edge state — but as
environment globals they are one name collision away from two mods fighting.
Give the loader a single reserved table:

```lua
smloader._state = smloader._state or {}
smloader._state.panelKeyWasDown = down
```

### 8.10 [Q] Dead code and orphaned comments in NoclipMod

`ExplorePointerGraph` and `SearchForPosition` are never called — they were the
offset-discovery tools. Move them behind a `#if SMLOADER_FIELD_DISCOVERY` or into
a separate diagnostic mod. There are also several orphaned comment blocks
(around the `setPosition` registration, before `PatchCharacterGuards`, and above
`PlaceCharacter`) describing code that no longer exists.

---

## 9. Build, tooling and repository hygiene

### 9.1 [Q] ~~Two JVM crash dumps were committed to the repository~~ — RESOLVED

```
hs_err_pid24900.log      60 KB
replay_pid24900.log     883 KB
```

Rider/JVM crash artefacts, not project files. Worse than clutter: an `hs_err`
dump embeds the full `PATH`, `USERNAME`, CPU model, every loaded module path and
a complete installed-software inventory of the machine that produced it. That is
a machine fingerprint, and it does not belong in a public repository.

Both are now removed and excluded. The general rule for this project: **anything
matching a crash-dump or diagnostic-log pattern is untrackable by default**,
because these files are generated without the author choosing their contents.

```gitignore
hs_err_pid*.log
replay_pid*.log
*.hprof
*.mdmp
*.dmp
core.*
smloader.log
```

Note that deleting such a file at HEAD is *not* sufficient once it has been
committed — the blob remains reachable in every later commit's history and is
published with the repository. It has to be purged from history, or the history
started fresh.

### 9.2 [Q] ~~`.idea/` is tracked~~ — RESOLVED

Five files under `.idea/.idea.SMLoader/` were committed. Rider's own generated
`.idea/.gitignore` excludes most of it, but `encodings.xml`, `indexLayout.xml`
and `vcs.xml` were in. Harmless in content, but IDE state does not belong in a
repo shared with other contributors. Now untracked and covered by `.idea/` in
`.gitignore`; the folder stays on disk so Rider keeps working.

### 9.3 [Q] No `.editorconfig`

The code is written in a consistent, deliberate style — file-scoped namespaces,
expression-bodied members where they fit, `var` only where the type is obvious.
None of that is enforced. An `.editorconfig` makes it survive the second
contributor:

```ini
root = true
[*.cs]
indent_style = space
indent_size = 4
csharp_style_namespace_declarations = file_scoped:error
dotnet_style_namespace_match_folder = true
```

### 9.4 [Q] No `global.json`

`dotnet --version` reports 10.0.300 here; nothing pins it. A contributor on a
different SDK feature band gets different analyser behaviour and potentially a
different `net10.0` patch runtime.

```json
{ "sdk": { "version": "10.0.300", "rollForward": "latestFeature" } }
```

### 9.5 [Q] Analysers are off and warnings are not errors

`Directory.Build.props` sets language and platform properties but no analysis:

```xml
<EnableNETAnalyzers>true</EnableNETAnalyzers>
<AnalysisLevel>latest-recommended</AnalysisLevel>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<Deterministic>true</Deterministic>
<ContinuousIntegrationBuild Condition="'$(CI)'=='true'">true</ContinuousIntegrationBuild>
```

CA1416 (platform compatibility) alone would flag every unguarded Win32 call in
the cross-platform-targeting `SMLoader.Core`.

### 9.6 [Q] `SMLoader.Core` targets `net10.0` but is Windows-only

`SMLoader.Core.csproj` has no `-windows` TFM, yet the assembly P/Invokes
`kernel32` and `user32` throughout. Target `net10.0-windows` — as the Launcher
already does — so the platform analyser can do its job and the intent is
declared.

### 9.7 [Q] No `SupportedOSPlatform` attributes

Even with the right TFM, `[SupportedOSPlatform("windows")]` on the interop types
documents the constraint at the API surface, where mod authors will see it.

### 9.8 [P] Startup JIT cost is paid on every launch

`SMLoader.Core` is loaded and fully JIT-compiled while the game is starting.
`<PublishReadyToRun>true</PublishReadyToRun>` — or a crossgen2 step in
`build.ps1` — precompiles it and takes that off the game's boot path. Worth
measuring: it is one of the few changes that make the loader's presence less
visible to the player.

Also pin the runtime configuration explicitly:

```json
{ "configProperties": {
    "System.GC.Concurrent": true,
    "System.GC.Server": false,
    "System.Globalization.Invariant": true,
    "System.Runtime.TieredPGO": true } }
```

`Server: false` is already the default for a hosted component — stating it
prevents a stray `DOTNET_gcServer=1` in a player's environment from spawning a GC
heap per core inside the game. (`InvariantGlobalization` is already set in
`Directory.Build.props`, which also avoids the ICU load — good.)

### 9.9 [Q] `build.ps1` has no incremental path and no `clean`

Every run reconfigures CMake. Add `-Clean` and `-SkipNative`, and skip the CMake
configure when `CMakeCache.txt` is newer than `CMakeLists.txt`. Note also that
`$Configuration` is validated but not reflected in the mods' output path, so a
Debug mod build overwrites the Release one in `dist/Mods/`.

### 9.10 [Q] No `dist` manifest

Nothing records which loader version produced a `dist/`. Write
`dist/smloader.version.json` with the git SHA, build timestamp and configuration.
It turns "which build was this?" from guesswork into a one-line answer in every
bug report.

### 9.11 [Q] No LICENSE, CONTRIBUTING, CHANGELOG or SECURITY

The README is genuinely excellent — it explains *why* at every turn, which is
rare — but it is doing the work of four documents. Split out:

- `LICENSE` — the project is currently "all rights reserved" by default, which
  prevents anyone from contributing or redistributing.
- `SECURITY.md` — the trust model from §6.1, and how to report a vulnerability.
- `CHANGELOG.md` — the git log already reads like one.
- `docs/WRITING-MODS.md` — the mod-authoring half of the README, which is already
  the best documentation of Scrap Mechanic's script sandbox anywhere.

### 9.12 [Q] `SMLoader.Api` is not packaged

Mod authors currently need the whole repository to build against `IMod`. Publish
`SMLoader.Api` as a NuGet package (`<IsPackable>true</IsPackable>`, symbols,
`PackageReadmeFile`) so a mod becomes `dotnet new classlib` plus one
`PackageReference`.

---

## 10. Testing

There is no test project. A surprising amount of this codebase is pure and
directly testable — including the parts most likely to break silently.

### 10.1 [Q] Unit-testable today, with no refactoring

| Target | What to assert |
|---|---|
| `Banner.Build` | width, truncation with `…`, exact frame characters |
| `KeyNames.Describe` | every range boundary: 0x2F/0x30/0x39/0x3A, 0x40/0x41/0x5A/0x5B, 0x6F/0x70/0x87, 0x5F/0x60/0x69 |
| `ScriptLoadContext` | `Append`/`Prepend`/`ReplaceFirst`, including the not-found path |
| `ProcessMemory.ParsePattern` | `??`, `?`, odd tokens, invalid hex, empty, all-wildcard |
| `GameLocator.PathEntry` regex | real `libraryfolders.vdf` samples, escaped backslashes, UNC paths |
| `ScriptPatcher` matching | case-insensitive `Contains` semantics, multiple registrations |

### 10.2 [Q] Testable after a small seam

`ModConfig`, `AssetPatcher` and `GameKeybinds` all reach the filesystem directly.
Introduce a minimal abstraction — not a full `IFileSystem`, just what they use:

```csharp
internal interface IFileStore
{
    bool Exists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
}
```

Then `ModConfig` round-trips, corrupt-JSON recovery, atomic-save behaviour, and
the whole `AssetPatcher` transform pipeline become ordinary unit tests.

### 10.3 [Q] The Lua layer needs an integration harness

`LuaState`, `LuaApi` and the trampoline are the highest-risk code (§1.3, §3.1,
§3.2) and are currently exercised only by launching the game. Ship a `lua51.dll`
in the test project — LuaJIT binaries are freely available and this is precisely
the ABI the loader targets — and test:

- push/pop stack balance for every `LuaState` method
- `RegisterModule` on an existing table vs a fresh one
- `Seed` leaving the stack exactly as it found it
- the trampoline swallowing an exception and returning 0
- registry ref lifetime across a simulated `lua_close`

A stack-balance assertion around every API call —
`int top = lua_gettop(L); /* ... */ Assert.Equal(top, lua_gettop(L))` — would have
caught an entire class of bug before it reached a player.

### 10.4 [Q] Golden-file checks for the injected Lua

`PanelLua`, `PlayerPatch` and `GamePatch` are ~500 lines of Lua embedded in C#
string literals, with no syntax checking at build time. A typo is discovered by
the game silently failing to load `CreativePlayer.lua` — a failure mode the
README itself warns about. At minimum, run `luac -p` (or `luajit -bl`) over each
embedded chunk as a build step. Better: move them to `.lua` files as embedded
resources so an editor can lint and highlight them.

### 10.5 [Q] CI

```yaml
name: build
on: [push, pull_request]
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: cmake -S src/SMLoader.Shim -B build/shim -A x64
      - run: cmake --build build/shim --config Release
      - run: dotnet build SMLoader.slnx -c Release
      - run: dotnet test -c Release
      - uses: actions/upload-artifact@v4
        with: { name: dist, path: dist/ }
```

Add `dotnet format --verify-no-changes` and a `clang-format` check for the shim.

---

## 11. Observability

### 11.1 [Q] There are no log levels

Everything goes through `Logging.Write` at one level. `script: {name}` — one line
per distinct chunk, dozens per world load — sits alongside "boot failed". A
player asked to send their log sends noise; a developer debugging asset
redirection cannot turn the detail *up*.

```csharp
internal enum LogLevel { Trace, Debug, Info, Warn, Error }
Logging.Write(LogLevel.Trace, $"script: {name}");
```

Threshold from `SMLOADER_LOG_LEVEL`, defaulting to `Info`.

### 11.2 [P] One writer, buffered, off the game thread

Both logging implementations open, write and close the file per line (§1.2,
§7.6, §2.15). Under a script-heavy world load that is hundreds of open/close
pairs on the thread compiling scripts.

The right shape: the shim owns a single append handle, and the managed side
writes through a callback in `BootContext` rather than opening the file itself.
That fixes the sharing conflict in §1.2 by construction and gives one place to
add buffering.

```cpp
using LogCallback = void(__cdecl*)(int level, const char* utf8);
```

Managed side enqueues into a bounded `Channel<string>` drained by a dedicated
low-priority thread. Bounded, with `DropOldest` — logging must never block the
game and must never grow without limit.

### 11.3 [Q] No log rotation

The shim truncates on launch (`log.cpp:25`), so a crash's log is destroyed by the
next launch attempt — exactly when the player is retrying and about to report the
problem. Keep `smloader.log` plus `smloader.log.1`, rotating on launch.

### 11.4 [Q] No timing or counters

Add cheap instrumentation the loader can report on demand:

- `luaL_loadbufferx` calls, transform hits, transform time (p50/p99)
- `CreateFileW` calls, prefilter rejects, cache hits, redirects
- Lua→C# calls per second, allocated bytes per frame
- mod load time, per mod

Surface it as `smloader.stats()` in the panel, and a line in the log every 60
seconds at `Debug`. Without this, every claim in [§2](#2-hot-path-performance) is
an argument rather than a measurement — and the first thing to do with this
document is turn its performance items into numbers.

### 11.5 [Q] The `status at Ns` checkpoints are a good idea, half-finished

`dllmain.cpp:203-213` reports hook integrity at 15/60/180 s and then stops
forever. Make it periodic (every five minutes at `Debug`), and report all five
hooks rather than only `luaL_newstate`.

---

## 12. Prioritised roadmap

### Do first — correctness, cheap, high consequence

1. ~~§1.1 NUL-terminate the redirected path; zero-init the native buffer~~ — done
   (the terminator was already there, written as a raw NUL byte; see the item)
2. ~~§1.2 Fix log file sharing on both sides~~ — done, and §5.2 with it
3. ~~§1.6 / §6.4 Make `Unprotect`/`Protect` report failure; stop leaving RWX pages~~ — done
4. ~~§5.1 Global unhandled-exception handlers~~ — done
5. ~~§1.11 / §1.12 Isolate mod load failures per type~~ — done
6. ~~§5.5 Atomic config writes~~ — done
7. ~~§9.1 / §9.2 Remove the JVM dumps and `.idea/` from git~~ — done

Found while doing the above, and fixed: the repository's blobs are stored with
CRLF while `core.autocrlf` is `true`, so a one-line edit to any file diffed as a
whole-file rewrite. A `.gitattributes` with `* -text` settles it, and is the
reason the diffs for this batch are readable.

The next block is [§1.3 / §3.2](#13-c-lual_ref-slots-are-keyed-by-a-raw-lua_state-that-can-be-recycled)
onward — the lifetime bugs.

### Do next — the lifetime bugs that surface after hours of play

8. §1.3 / §3.2 Hook `lua_close`; unref and evict per state
9. §3.1 Stop `LuaState.Registered` growing without bound
10. §3.3 Bound the asset-resolution cache
11. §1.7 / §1.8 Correct module enumeration; hook late-loaded modules

### Then — performance, in descending order of expected win

12. §2.1 `LuaState` as a struct (removes ~10 allocations per frame)
13. §2.2 Region-cached or handle-based memory access (removes ~4,000 syscalls/s)
14. §2.10 Native prefilter before entering managed code on `CreateFileW`
15. §2.6 Cache patched script output by content hash
16. §2.4 / §2.5 Cache typed settings; debounce config saves
17. §2.3 / §2.11 Batch input polling
18. §2.12 `[SuppressGCTransition]` on the trivial Lua bindings
19. §2.9 Atomics instead of mutexes for the write-once callbacks

### Then — hardening and tooling

20. §6.1 / §6.2 State the trust model; add `--no-mods` and an integrity check
21. §7.1 SEH-guard the PE header walk
22. §7.7 Harden the shim's build flags; static CRT; PDB in Release
23. §9.5 Turn analysers on and warnings into errors
24. §10 A test project, starting with the pure functions
25. §11 Log levels and a single buffered writer

### Longer term — design

26. §8.1 / §8.2 API version handshake, mod manifests, deterministic load order
27. §8.4 Collectible load contexts and disposable registrations → hot reload
28. §11.4 Instrumentation, so the next version of this document has numbers in it

---

## A closing note on what is already right

Worth recording, because it is the reason the harder items above are worth doing
at all:

- **Hooking the game's own scripting ABI instead of hunting struct offsets.**
  That single decision is why this survives game updates, and the README explains
  it better than most projects explain anything.
- **Never writing to the game folder.** The asset cache, the in-memory script
  rewriting, the `SteamAppId` environment variable instead of a
  `steam_appid.txt` — every one of these was a deliberate choice to leave
  *Verify integrity of game files* with nothing to undo.
- **Catching everything at the native boundary.** `Trampoline`,
  `OnLuaStateCreated`, `OnScriptLoading`, `OnSetFenv`, `OnFileOpen` and
  `OnScriptFree` are all wrapped, with comments explaining that an exception
  unwinding into LuaJIT's frames kills the process. That is the correct instinct,
  applied consistently.
- **The comments explaining what was tried and why it failed.** The notes on
  `body+0x1C0`, on why a sixth Options tab is not viable, on why per-instance
  edge state made the panel need two presses — that is knowledge normally lost.
  Keep writing them.
- **`AssetPatcher.Resolve`'s early-out on an empty registration list.** Exactly
  the right shape for a hook on a hot path: one field read when nobody cares.
  Apply the same pattern in `ScriptPatcher` (§2.8) and in the native layer
  (§2.10).
