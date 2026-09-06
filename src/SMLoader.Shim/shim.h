#pragma once
#include <windows.h>

namespace smloader {

// Function pointer the managed side installs so it is told about every
// lua_State the game creates (including ones created before the CLR booted).
using LuaStateCallback = void(__cdecl*)(void* L);

// Polled from the lua_pcall detour until it returns non-zero, which means the
// managed side has found a populated VM and no longer needs to be asked.
using LuaReadyCallback = int(__cdecl*)(void* L);

// Called before the engine compiles a script. Return non-zero having filled the
// out params to compile different source instead - the file on disk is never
// touched. The shim releases the replacement through ScriptFreeCallback.
using ScriptLoadCallback = int(__cdecl*)(const char* name, const char* source, size_t length,
                                         const char** outSource, size_t* outLength);
using ScriptFreeCallback = void(__cdecl*)(const char* buffer);

// Called just before the game destroys a lua_State, while the state is still
// valid, so the managed side can release the registry references it holds in
// it. The allocator reuses addresses, so without this a later state can be
// handed the same lua_State* and inherit a reference to a dead registry.
using LuaCloseCallback = void(__cdecl*)(void* L);

// Called with the environment table on top of the Lua stack, just before the
// engine installs it on a script. The callback MUST leave the stack balanced.
using SetFenvCallback = void(__cdecl*)(void* L);

// Called before the game opens a file. Fill `out` with a replacement path and
// return non-zero to have the engine read that instead - which is how assets
// are modified without touching anything in the game folder.
using FileOpenCallback = int(__cdecl*)(const wchar_t* path, wchar_t* out, int outChars);

struct BootContext {
    unsigned int   size;             // sizeof(BootContext), for forward compat
    const wchar_t* rootDir;          // directory containing SMLoader.Core.dll
    void (__cdecl* setLuaStateCallback)(LuaStateCallback cb);
    void (__cdecl* setLuaReadyCallback)(LuaReadyCallback cb);
    void (__cdecl* setScriptLoadCallback)(ScriptLoadCallback load, ScriptFreeCallback free);
    void (__cdecl* setSetFenvCallback)(SetFenvCallback cb);
    void (__cdecl* setFileOpenCallback)(FileOpenCallback cb);
    // Appended after 0.1.0. Guarded by `size` on the managed side, which is
    // what that field has always been for.
    void (__cdecl* setLuaCloseCallback)(LuaCloseCallback cb);

    // Managed -> native, unlike everything above it. Publishes the substrings
    // mods have registered so the CreateFileW detour can answer for itself.
    void (__cdecl* setPathFilter)(const wchar_t* needles);
};

extern HMODULE g_selfModule;

// Called by the IAT detour whenever the game creates a lua_State.
void OnLuaStateCreated(void* L);

// Called by the lua_close detour before the state is destroyed.
void OnLuaStateClosing(void* L);
void SetLuaCloseCallback(LuaCloseCallback cb);

// Installed by the managed side through BootContext::setLuaStateCallback.
void SetLuaStateCallback(LuaStateCallback cb);
void SetLuaReadyCallback(LuaReadyCallback cb);

// Called by the lua_pcall detour. Returns true once the managed side is done
// and the detour can retire itself.
bool NotifyLuaRunning(void* L);

void SetScriptLoadCallback(ScriptLoadCallback load, ScriptFreeCallback free);

// Called by the luaL_loadbufferx detour. Returns true when it filled the out
// params with a replacement the caller must release via ReleaseScriptSource.
bool TransformScript(const char* name, const char* source, size_t length,
                     const char** outSource, size_t* outLength);
void ReleaseScriptSource(const char* buffer);

void SetSetFenvCallback(SetFenvCallback cb);

// Called by the lua_setfenv detour with the env table at stack top.
void SeedScriptEnvironment(void* L);

void SetFileOpenCallback(FileOpenCallback cb);

// Called by the CreateFileW detour. Returns true when `out` holds a
// replacement path for the file being opened.
bool RedirectFileOpen(const wchar_t* path, wchar_t* out, int outChars);

// Installed by the managed side. `needles` is a single newline-separated,
// already-lowercased string of the substrings mods want to match; an empty
// string means nobody wants anything. Copied immediately.
void SetPathFilter(const wchar_t* needles);

// How many lua_States the detour has observed so far.
int SeenStateCount();

// Directory this DLL lives in, with a trailing backslash.
const wchar_t* RootDir();

} // namespace smloader
