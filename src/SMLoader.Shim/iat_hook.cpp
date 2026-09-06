#include "iat_hook.h"
#include "shim.h"
#include "log.h"

#include <winnt.h>
#include <winternl.h>
#include <psapi.h>
#include <cstring>
#include <atomic>
#include <cstdio>
#include <vector>

namespace {

using luaL_newstate_t = void* (__cdecl*)();
using lua_pcall_t     = int (__cdecl*)(void*, int, int, int);

void**          g_slot = nullptr;   // the IAT entry we patched
luaL_newstate_t g_original = nullptr;

// Atomic: Detour_lua_pcall can retire the hook from more than one thread at
// once, and the exchange is what makes exactly one of them do it.
std::atomic<void**> g_pcallSlot{nullptr};
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

using lua_close_t = void (__cdecl*)(void*);
void**        g_closeSlot = nullptr;
lua_close_t   g_originalClose = nullptr;

volatile long g_lateHooked = 0;
void*         g_notificationCookie = nullptr;

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

void __cdecl Detour_lua_close(void* L)
{
    // Before the original, never after: the managed side has to release its
    // registry references while the registry still exists. Once lua_close
    // returns, the allocator is free to hand this same address back for the
    // next state.
    if (L)
        smloader::OnLuaStateClosing(L);

    if (g_originalClose)
        g_originalClose(L);
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
    //
    // GENERIC_WRITE alone does not detect a write: FILE_WRITE_DATA,
    // FILE_APPEND_DATA, GENERIC_ALL and MAXIMUM_ALLOWED all grant write access
    // without setting it, and a write opened that way would land in the
    // loader's cache - the game writing to the wrong file, which is exactly
    // what the sentence above says must be impossible.
    //
    // OPEN_EXISTING too, so a create or a truncate is never redirected.
    constexpr DWORD kWriteBits = GENERIC_WRITE | GENERIC_ALL | MAXIMUM_ALLOWED
                               | FILE_WRITE_DATA | FILE_APPEND_DATA;

    if (fileName && (access & kWriteBits) == 0 && disposition == OPEN_EXISTING)
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
//
// Every dereference below is into a header we did not write, and since the
// DLL load notification points this at arbitrary modules as they arrive, a
// malformed or partially mapped image must not take the process down. The
// SEH guard is why this function holds no C++ objects.
void** FindIatSlotUnguarded(HMODULE module, const char* importDll, const char* importName)
{
    auto* base = reinterpret_cast<BYTE*>(module);
    auto* dos  = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        return nullptr;

    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
        return nullptr;

    // IMAGE_NT_HEADERS is the 64-bit form in this build, so a 32-bit module
    // mapped as data would be read through the wrong layout.
    if (nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
        return nullptr;

    const DWORD imageSize = nt->OptionalHeader.SizeOfImage;

    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (dir.VirtualAddress == 0 || dir.Size == 0 ||
        dir.VirtualAddress > imageSize || dir.Size > imageSize - dir.VirtualAddress)
    {
        return nullptr;
    }

    auto* desc = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);
    for (; desc->Name != 0; ++desc)
    {
        if (desc->Name >= imageSize || desc->FirstThunk >= imageSize)
            return nullptr;

        const char* dllName = reinterpret_cast<const char*>(base + desc->Name);
        if (_stricmp(dllName, importDll) != 0)
            continue;

        // OriginalFirstThunk holds the names; FirstThunk holds the resolved
        // addresses. They are parallel arrays, so one index serves both.
        const DWORD namesRva = desc->OriginalFirstThunk ? desc->OriginalFirstThunk
                                                        : desc->FirstThunk;
        if (namesRva >= imageSize)
            return nullptr;

        auto* nameThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + namesRva);
        auto* addrThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + desc->FirstThunk);

        for (; nameThunk->u1.AddressOfData != 0; ++nameThunk, ++addrThunk)
        {
            if (IMAGE_SNAP_BY_ORDINAL(nameThunk->u1.Ordinal))
                continue; // imported by ordinal, no name to match

            if (nameThunk->u1.AddressOfData >= imageSize)
                return nullptr;

            auto* byName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(
                base + nameThunk->u1.AddressOfData);
            if (std::strcmp(reinterpret_cast<const char*>(byName->Name), importName) == 0)
                return reinterpret_cast<void**>(&addrThunk->u1.Function);
        }
    }
    return nullptr;
}

void** FindIatSlot(HMODULE module, const char* importDll, const char* importName)
{
    if (!module)
        return nullptr;

    __try
    {
        return FindIatSlotUnguarded(module, importDll, importName);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return nullptr;
    }
}

// Patches kernel32!CreateFileW in one module's import table. Modules that do
// not import it are skipped silently.
//
// Logs nothing, because the DLL load notification calls it with the loader
// lock held and log::Write opens a file - which would re-enter our own
// CreateFileW detour underneath that lock.
bool HookCreateFileInQuiet(HMODULE module, void*** hookedSlot)
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

    if (hookedSlot)
        *hookedSlot = slot;
    return true;
}

bool HookCreateFileIn(HMODULE module, const wchar_t* moduleName)
{
    void** slot = nullptr;
    if (!HookCreateFileInQuiet(module, &slot))
        return false;

    SMLOG("hooked CreateFileW in %ws (slot %p)", moduleName, static_cast<void*>(slot));
    return true;
}

// The subset of LDR_DLL_NOTIFICATION_DATA we need. Declared here because the
// Windows SDK only exposes these through the DDK.
struct LdrNotificationData {
    ULONG                 Flags;
    const UNICODE_STRING* FullDllName;
    const UNICODE_STRING* BaseDllName;
    PVOID                 DllBase;
    ULONG                 SizeOfImage;
};

constexpr ULONG kDllLoaded = 1;

using LdrNotificationCallback = VOID(CALLBACK*)(ULONG, const LdrNotificationData*, PVOID);
using LdrRegisterDllNotification_t =
    LONG(NTAPI*)(ULONG, LdrNotificationCallback, PVOID, PVOID*);
using LdrUnregisterDllNotification_t = LONG(NTAPI*)(PVOID);

// Runs with the loader lock held. Nothing here may log, allocate, or call
// back into the loader - the same trap HookFileApis documents for DllMain.
VOID CALLBACK OnDllLoaded(ULONG reason, const LdrNotificationData* data, PVOID)
{
    if (reason != kDllLoaded || !data || !data->DllBase)
        return;

    auto module = reinterpret_cast<HMODULE>(data->DllBase);
    if (module == smloader::g_selfModule)
        return;

    // The notification carries the name, so there is no need to ask psapi for
    // it here. Copy it out bounded rather than trusting NUL termination.
    if (data->BaseDllName && data->BaseDllName->Buffer)
    {
        wchar_t name[64]{};
        size_t chars = data->BaseDllName->Length / sizeof(wchar_t);
        if (chars > 63)
            chars = 63;
        std::memcpy(name, data->BaseDllName->Buffer, chars * sizeof(wchar_t));

        if (_wcsicmp(name, L"ntdll.dll") == 0 || _wcsicmp(name, L"kernel32.dll") == 0 ||
            _wcsicmp(name, L"kernelbase.dll") == 0)
        {
            return;
        }
    }

    if (HookCreateFileInQuiet(module, nullptr))
        InterlockedIncrement(&g_lateHooked);
}

void SubscribeToModuleLoads()
{
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll)
        return;

    auto reg = reinterpret_cast<LdrRegisterDllNotification_t>(
        reinterpret_cast<void*>(GetProcAddress(ntdll, "LdrRegisterDllNotification")));
    if (!reg)
    {
        SMLOG("LdrRegisterDllNotification unavailable; modules loaded from now on "
              "will keep the real CreateFileW");
        return;
    }

    const LONG status = reg(0, &OnDllLoaded, nullptr, &g_notificationCookie);
    if (status != 0)
        SMLOG("LdrRegisterDllNotification failed (0x%lx); late modules will not be hooked",
              static_cast<unsigned long>(status));
    else
        SMLOG("subscribed to loader notifications for late-loaded modules");
}

void HookCreateFileEverywhere()
{
    const HANDLE self = GetCurrentProcess();

    // Size the list first. A fixed array silently truncates on a machine
    // carrying overlays, input remappers and capture tools, and the symptom -
    // some asset opens not redirected - looks machine-specific and random.
    DWORD needed = 0;
    if (!EnumProcessModules(self, nullptr, 0, &needed) || needed == 0)
    {
        SMLOG("EnumProcessModules sizing failed (%lu); asset redirection may be partial",
              GetLastError());
        return;
    }

    // Headroom, because a module can load between the two calls.
    std::vector<HMODULE> modules(needed / sizeof(HMODULE) + 16);
    if (!EnumProcessModules(self, modules.data(),
                            static_cast<DWORD>(modules.size() * sizeof(HMODULE)), &needed))
    {
        SMLOG("EnumProcessModules failed (%lu); asset redirection may be partial", GetLastError());
        return;
    }

    size_t count = needed / sizeof(HMODULE);
    if (count > modules.size())
        count = modules.size();

    int hooked = 0;

    for (size_t i = 0; i < count; ++i)
    {
        // Never hook ourselves: our detour calls CreateFileW to write the log.
        if (modules[i] == smloader::g_selfModule)
            continue;

        wchar_t name[MAX_PATH]{};
        if (GetModuleBaseNameW(self, modules[i], name, MAX_PATH) == 0)
        {
            // Without a name there is no way to tell ntdll from a game module,
            // and hooking the wrong one recurses during module load.
            SMLOG("GetModuleBaseNameW failed for module %p (%lu); skipping it",
                  static_cast<void*>(modules[i]), GetLastError());
            continue;
        }

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

    SMLOG("CreateFileW hooked in %d of %zu module(s)", hooked, count);
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

    g_closeSlot = FindIatSlot(exe, "lua51.dll", "lua_close");
    if (g_closeSlot)
    {
        g_originalClose = reinterpret_cast<lua_close_t>(*g_closeSlot);
        if (WriteSlot(g_closeSlot, reinterpret_cast<void*>(&Detour_lua_close)))
            SMLOG("hooked lua_close (slot %p)", static_cast<void*>(g_closeSlot));
        else
            g_closeSlot = nullptr;
    }
    else
    {
        SMLOG("no IAT slot for lua_close; registry references will be held for "
              "the life of the process");
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

    // A one-shot sweep leaves every DLL the engine loads later - a renderer
    // backend, an audio plug-in, a Steam module - holding the real
    // CreateFileW, so its opens escape redirection.
    SubscribeToModuleLoads();
}

int LateHookedCount()
{
    return static_cast<int>(g_lateHooked);
}

void UnhookAll()
{
    // Loader notifications first: once the slots are restored, a module
    // arriving would otherwise be hooked into a detour that is about to stop
    // existing.
    if (g_notificationCookie)
    {
        if (HMODULE ntdll = GetModuleHandleW(L"ntdll.dll"))
        {
            auto unregister = reinterpret_cast<LdrUnregisterDllNotification_t>(
                reinterpret_cast<void*>(GetProcAddress(ntdll, "LdrUnregisterDllNotification")));
            if (unregister)
                unregister(g_notificationCookie);
        }
        g_notificationCookie = nullptr;
    }

    // CreateFileW is hooked in many modules and we did not record which, so
    // walk them again and restore any slot still pointing at our detour.
    if (g_originalCreateFileW)
    {
        const HANDLE self = GetCurrentProcess();
        DWORD needed = 0;
        if (EnumProcessModules(self, nullptr, 0, &needed) && needed != 0)
        {
            std::vector<HMODULE> modules(needed / sizeof(HMODULE) + 16);
            if (EnumProcessModules(self, modules.data(),
                                   static_cast<DWORD>(modules.size() * sizeof(HMODULE)),
                                   &needed))
            {
                size_t count = needed / sizeof(HMODULE);
                if (count > modules.size())
                    count = modules.size();

                for (size_t i = 0; i < count; ++i)
                {
                    void** slot = FindIatSlot(modules[i], "KERNEL32.dll", "CreateFileW");
                    if (!slot)
                        slot = FindIatSlot(modules[i], "api-ms-win-core-file-l1-1-0.dll",
                                           "CreateFileW");

                    if (slot && *slot == reinterpret_cast<void*>(&Detour_CreateFileW))
                        WriteSlot(slot, reinterpret_cast<void*>(g_originalCreateFileW));
                }
            }
        }
    }

    // The Lua slots are each a single known location.
    if (g_slot && *g_slot == reinterpret_cast<void*>(&Detour_luaL_newstate))
        WriteSlot(g_slot, reinterpret_cast<void*>(g_original));
    if (g_pcallSlot && *g_pcallSlot == reinterpret_cast<void*>(&Detour_lua_pcall))
        WriteSlot(g_pcallSlot, reinterpret_cast<void*>(g_originalPcall));
    if (g_setfenvSlot && *g_setfenvSlot == reinterpret_cast<void*>(&Detour_lua_setfenv))
        WriteSlot(g_setfenvSlot, reinterpret_cast<void*>(g_originalSetfenv));
    if (g_loadSlot && *g_loadSlot == reinterpret_cast<void*>(&Detour_luaL_loadbufferx))
        WriteSlot(g_loadSlot, reinterpret_cast<void*>(g_originalLoad));
    if (g_closeSlot && *g_closeSlot == reinterpret_cast<void*>(&Detour_lua_close))
        WriteSlot(g_closeSlot, reinterpret_cast<void*>(g_originalClose));

    g_slot = nullptr;
    g_pcallSlot = nullptr;
    g_setfenvSlot = nullptr;
    g_loadSlot = nullptr;
    g_closeSlot = nullptr;
}

void RetirePcallHook()
{
    // Whoever wins the exchange owns the restore; everyone else sees null and
    // leaves. Reading then clearing let two threads both act on the same slot.
    void** slot = g_pcallSlot.exchange(nullptr, std::memory_order_acq_rel);
    if (!slot)
        return;

    WriteSlot(slot, reinterpret_cast<void*>(g_originalPcall));
    SMLOG("lua_pcall hook retired");
}

bool IsIntact()
{
    return g_slot != nullptr && *g_slot == reinterpret_cast<void*>(&Detour_luaL_newstate);
}

const char* HookSummary()
{
    static char summary[160];

    auto state = [](void** slot, void* detour) -> const char* {
        if (!slot)
            return "absent";
        return (*slot == detour) ? "intact" : "LOST";
    };

    _snprintf_s(summary, sizeof(summary), _TRUNCATE,
                "newstate %s, pcall %s, setfenv %s, load %s, close %s",
                state(g_slot, reinterpret_cast<void*>(&Detour_luaL_newstate)),
                state(g_pcallSlot, reinterpret_cast<void*>(&Detour_lua_pcall)),
                state(g_setfenvSlot, reinterpret_cast<void*>(&Detour_lua_setfenv)),
                state(g_loadSlot, reinterpret_cast<void*>(&Detour_luaL_loadbufferx)),
                state(g_closeSlot, reinterpret_cast<void*>(&Detour_lua_close)));
    return summary;
}

} // namespace smloader::iat
