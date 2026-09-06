#pragma once

namespace smloader::iat {

// Redirects ScrapMechanic.exe's import of lua51.dll!luaL_newstate to our detour
// so we learn the address of every lua_State the game creates.
bool InstallLuaHooks();

// Re-applies the patch if the loader (or anything else) has overwritten the
// IAT slot since InstallLuaHooks ran. Cheap; safe to call repeatedly.
void Reapply();

// True while the IAT slot still points at our detour.
bool IsIntact();

// Puts the original lua_pcall back once the managed side stops caring, so the
// detour costs nothing for the rest of the session.
void RetirePcallHook();

// Hooks CreateFileW across every loaded module so asset redirection also sees
// files the CRT opens on the engine's behalf. Must run off the loader lock.
void HookFileApis();

}
