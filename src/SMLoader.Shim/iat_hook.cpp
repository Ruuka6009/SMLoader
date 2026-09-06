#include "iat_hook.h"
#include "shim.h"
#include "log.h"

#include <winnt.h>
#include <psapi.h>
#include <cstring>

namespace {

using luaL_newstate_t = void* (__cdecl*)();
using lua_pcall_t     = int (__cdecl*)(void*, int, int, int);

void**          g_slot = nullptr;   // the IAT entry we patched
luaL_newstate_t g_original = nullptr;

void**          g_pcallSlot = nullptr;
lua_pcall_t     g_originalPcall = nullptr;

using CreateFileW_t = HANDLE (__stdcall*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
void**          g_createFileSlot = nullptr;
CreateFileW_t   g_originalCreateFileW = nullptr;

using lua_setfenv_t = int (__cdecl*)(void*, int);
void**          g_setfenvSlot = nullptr;
lua_setfenv_t   g_originalSetfenv = nullptr;

using luaL_loadbufferx_t = int (__cdecl*)(void*, const char*, size_t, const char*, const char*);
void**             g_loadSlot = nullptr;
luaL_loadbufferx_t g_originalLoad = nullptr;

void* __cdecl Detour_luaL_newstate()
{
    void* L = g_original ? g_original() : nullptr;
    SMLOG("luaL_newstate -> lua_State* %p", L);
    if (L)
        smloader::OnLuaStateCreated(L);
    return L;
}

int __cdecl Detour_lua_pcall(void* L, int nargs, int nresults, int errfunc)
{
    const int result = g_originalPcall(L, nargs, nresults, errfunc);

    // Ask the managed side whether the VM is populated yet. It answers true
    // once it has emitted its splash, after which we unhook.
    if (smloader::NotifyLuaRunning(L))
        smloader::iat::RetirePcallHook();

    return result;
}

int __cdecl Detour_luaL_loadbufferx(void* L, const char* buff, size_t sz,
                                    const char* name, const char* mode)
{
    const char* replacement = nullptr;
    size_t      replacementLength = 0;

    const bool transformed =
        smloader::TransformScript(name, buff, sz, &replacement, &replacementLength);

    const int result = transformed
        ? g_originalLoad(L, replacement, replacementLength, name, mode)
        : g_originalLoad(L, buff, sz, name, mode);

    if (transformed)
        smloader::ReleaseScriptSource(replacement);

    return result;
}

int __cdecl Detour_lua_setfenv(void* L, int idx)
{
    // The environment table is on top of the stack right now. Seeding it here
    // is the only reliable way to reach a sandboxed script: the engine gives
    // each script its own env, so globals set via LUA_GLOBALSINDEX never
    // arrive. The callback is required to leave the stack balanced, which
    // keeps a relative idx valid for the original call.
    smloader::SeedScriptEnvironment(L);
    return g_originalSetfenv(L, idx);
}

HANDLE __stdcall Detour_CreateFileW(LPCWSTR fileName, DWORD access, DWORD share,
                                    LPSECURITY_ATTRIBUTES security, DWORD disposition,
                                    DWORD flags, HANDLE templateFile)
{
    // Only reads are candidates for redirection; leaving writes alone means a
    // mistake here can never corrupt a game file.
    if (fileName && (access & GENERIC_WRITE) == 0)
    {
        wchar_t replacement[1024]{};
        if (smloader::RedirectFileOpen(fileName, replacement, 1024))
            fileName = replacement;
    }

    return g_originalCreateFileW(fileName, access, share, security,
                                 disposition, flags, templateFile);
}

bool WriteSlot(void** slot, void* value)
{
    DWORD oldProtect = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtect))
        return false;
    *slot = value;
    DWORD ignored = 0;
    VirtualProtect(slot, sizeof(void*), oldProtect, &ignored);
    return true;
}

// Locates the IAT slot for `importDll`!`importName` in `module`.
void** FindIatSlot(HMODULE module, const char* importDll, const char* importName)
{
    auto* base = reinterpret_cast<BYTE*>(module);
    auto* dos  = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        return nullptr;

    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
        return nullptr;

    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (dir.VirtualAddress == 0 || dir.Size == 0)
        return nullptr;

    auto* desc = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);
    for (; desc->Name != 0; ++desc)
    {
        const char* dllName = reinterpret_cast<const char*>(base + desc->Name);
        if (_stricmp(dllName, importDll) != 0)
            continue;

        // OriginalFirstThunk holds the names; FirstThunk holds the resolved
        // addresses. They are parallel arrays, so one index serves both.
        auto* nameThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(
            base + (desc->OriginalFirstThunk ? desc->OriginalFirstThunk : desc->FirstThunk));
        auto* addrThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + desc->FirstThunk);

        for (; nameThunk->u1.AddressOfData != 0; ++nameThunk, ++addrThunk)
        {
            if (IMAGE_SNAP_BY_ORDINAL(nameThunk->u1.Ordinal))
                continue; // imported by ordinal, no name to match

            auto* byName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(
                base + nameThunk->u1.AddressOfData);
            if (std::strcmp(reinterpret_cast<const char*>(byName->Name), importName) == 0)
                return reinterpret_cast<void**>(&addrThunk->u1.Function);
        }
    }
    return nullptr;
}

// Patches kernel32!CreateFileW in one module's import table. Modules that do
// not import it are skipped silently.
bool HookCreateFileIn(HMODULE module, const wchar_t* moduleName)
{
    void** slot = FindIatSlot(module, "KERNEL32.dll", "CreateFileW");
    if (!slot)
        slot = FindIatSlot(module, "api-ms-win-core-file-l1-1-0.dll", "CreateFileW");
    if (!slot)
        return false;

    void* current = *slot;
    if (current == reinterpret_cast<void*>(&Detour_CreateFileW))
        return false; // already ours

    if (!g_originalCreateFileW)
        g_originalCreateFileW = reinterpret_cast<CreateFileW_t>(current);

    if (!WriteSlot(slot, reinterpret_cast<void*>(&Detour_CreateFileW)))
        return false;

    SMLOG("hooked CreateFileW in %ws (slot %p)", moduleName, static_cast<void*>(slot));
    return true;
}

void HookCreateFileEverywhere()
{
    HMODULE modules[512];
    DWORD needed = 0;

    if (!EnumProcessModules(GetCurrentProcess(), modules, sizeof(modules), &needed))
    {
        SMLOG("EnumProcessModules failed (%lu); asset redirection may be partial", GetLastError());
        return;
    }

    const int count = static_cast<int>(needed / sizeof(HMODULE));
    int hooked = 0;

    for (int i = 0; i < count; ++i)
    {
        wchar_t name[MAX_PATH]{};
        GetModuleBaseNameW(GetCurrentProcess(), modules[i], name, MAX_PATH);

        // Never hook ourselves: our detour calls CreateFileW to write the log.
        if (modules[i] == smloader::g_selfModule)
            continue;

        // Leave the OS core alone. Redirecting file opens made by ntdll or the
        // loader itself risks recursion during module load for no benefit - the
        // engine's assets are opened by the exe and the CRT.
        if (_wcsicmp(name, L"ntdll.dll") == 0 || _wcsicmp(name, L"kernel32.dll") == 0 ||
            _wcsicmp(name, L"kernelbase.dll") == 0)
        {
            continue;
        }

        if (HookCreateFileIn(modules[i], name))
            ++hooked;
    }

    SMLOG("CreateFileW hooked in %d of %d module(s)", hooked, count);
}


} // namespace

namespace smloader::iat {

bool InstallLuaHooks()
{
    HMODULE exe = GetModuleHandleW(nullptr);
    g_slot = FindIatSlot(exe, "lua51.dll", "luaL_newstate");
    if (!g_slot)
    {
        SMLOG("FAILED: no IAT slot for lua51.dll!luaL_newstate in the main module");
        return false;
    }

    g_original = reinterpret_cast<luaL_newstate_t>(*g_slot);
    if (!WriteSlot(g_slot, reinterpret_cast<void*>(&Detour_luaL_newstate)))
    {
        SMLOG("FAILED: VirtualProtect on IAT slot %p", static_cast<void*>(g_slot));
        g_slot = nullptr;
        return false;
    }

    SMLOG("hooked luaL_newstate (slot %p, original %p)",
          static_cast<void*>(g_slot), reinterpret_cast<void*>(g_original));

    g_pcallSlot = FindIatSlot(exe, "lua51.dll", "lua_pcall");
    if (g_pcallSlot)
    {
        g_originalPcall = reinterpret_cast<lua_pcall_t>(*g_pcallSlot);
        if (WriteSlot(g_pcallSlot, reinterpret_cast<void*>(&Detour_lua_pcall)))
            SMLOG("hooked lua_pcall (slot %p)", static_cast<void*>(g_pcallSlot));
        else
            g_pcallSlot = nullptr;
    }
    else
    {
        SMLOG("no IAT slot for lua_pcall; the game-log splash will be skipped");
    }


    g_setfenvSlot = FindIatSlot(exe, "lua51.dll", "lua_setfenv");
    if (g_setfenvSlot)
    {
        g_originalSetfenv = reinterpret_cast<lua_setfenv_t>(*g_setfenvSlot);
        if (WriteSlot(g_setfenvSlot, reinterpret_cast<void*>(&Detour_lua_setfenv)))
            SMLOG("hooked lua_setfenv (slot %p)", static_cast<void*>(g_setfenvSlot));
        else
            g_setfenvSlot = nullptr;
    }
    else
    {
        SMLOG("no IAT slot for lua_setfenv; scripts will not see the smloader table");
    }

    g_loadSlot = FindIatSlot(exe, "lua51.dll", "luaL_loadbufferx");
    if (g_loadSlot)
    {
        g_originalLoad = reinterpret_cast<luaL_loadbufferx_t>(*g_loadSlot);
        if (WriteSlot(g_loadSlot, reinterpret_cast<void*>(&Detour_luaL_loadbufferx)))
            SMLOG("hooked luaL_loadbufferx (slot %p)", static_cast<void*>(g_loadSlot));
        else
            g_loadSlot = nullptr;
    }
    else
    {
        SMLOG("no IAT slot for luaL_loadbufferx; script transforms unavailable");
    }

    return true;
}

void Reapply()
{
    if (!g_slot)
        return;
    if (*g_slot == reinterpret_cast<void*>(&Detour_luaL_newstate))
        return;

    // The loader resolved imports after we patched; re-take the slot.
    g_original = reinterpret_cast<luaL_newstate_t>(*g_slot);
    if (WriteSlot(g_slot, reinterpret_cast<void*>(&Detour_luaL_newstate)))
        SMLOG("re-applied luaL_newstate hook (original now %p)",
              reinterpret_cast<void*>(g_original));
}

void HookFileApis()
{
    // Deliberately NOT done from DllMain: enumerating and patching every
    // module's imports under the loader lock made the process refuse to load
    // the shim at all. Called from the boot thread instead, which is well
    // before any GUI layout is read.
    HookCreateFileEverywhere();
}

void RetirePcallHook()
{
    if (!g_pcallSlot)
        return;

    void** slot = g_pcallSlot;
    g_pcallSlot = nullptr;              // stop re-entry before we touch the slot
    WriteSlot(slot, reinterpret_cast<void*>(g_originalPcall));
    SMLOG("lua_pcall hook retired");
}

bool IsIntact()
{
    return g_slot != nullptr && *g_slot == reinterpret_cast<void*>(&Detour_luaL_newstate);
}

} // namespace smloader::iat
