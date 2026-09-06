#pragma once

namespace smloader::iat {

// Redirects ScrapMechanic.exe's import of lua51.dll!luaL_newstate to our detour
// so we learn the address of every lua_State the game creates.
bool InstallLuaHooks();

// Re-applies the patch if the loader (or anything else) has overwritten the
// IAT slot since InstallLuaHooks ran. Cheap; safe to call repeatedly.
void Reapply();

// True while the luaL_newstate IAT slot still points at our detour.
bool IsIntact();

// "newstate pcall setfenv load close", each intact/LOST/absent. Reporting only
// luaL_newstate hid the case where it survives and one of the others does not.
const char* HookSummary();

// Puts the original lua_pcall back once the managed side stops caring, so the
// detour costs nothing for the rest of the session.
void RetirePcallHook();

// Hooks CreateFileW across every loaded module so asset redirection also sees
// files the CRT opens on the engine's behalf. Must run off the loader lock.
// Also subscribes to loader notifications so modules loaded afterwards are
// hooked as they arrive.
void HookFileApis();

// Restores every IAT slot this shim patched, and unsubscribes from loader
// notifications. Only meaningful when the DLL is being unloaded while the
// process lives on - a slot still pointing into freed memory kills the
// process on the next call through it.
void UnhookAll();

// How many modules the loader notification has hooked since HookFileApis ran.
// The notification callback cannot log - it holds the loader lock - so this
// is how that work becomes visible.
int LateHookedCount();

}
