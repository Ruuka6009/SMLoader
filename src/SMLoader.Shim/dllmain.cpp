#include "shim.h"
#include "iat_hook.h"
#include "clr_host.h"
#include "log.h"

#include <mutex>
#include <string>
#include <vector>

namespace smloader {

HMODULE g_selfModule = nullptr;

namespace {

std::mutex               g_stateMutex;
std::vector<void*>       g_pendingStates;   // seen before the CLR was ready
LuaStateCallback         g_callback = nullptr;
std::wstring             g_rootDir;
volatile long            g_stateCount = 0;
LuaReadyCallback         g_readyCallback = nullptr;
volatile long            g_readyDone = 0;
ScriptLoadCallback       g_scriptLoad = nullptr;
ScriptFreeCallback       g_scriptFree = nullptr;
SetFenvCallback          g_setFenv = nullptr;
FileOpenCallback         g_fileOpen = nullptr;
LuaCloseCallback         g_closeCallback = nullptr;
std::mutex               g_scriptMutex;

} // namespace

const wchar_t* RootDir()
{
    if (!g_rootDir.empty())
        return g_rootDir.c_str();

    wchar_t path[MAX_PATH]{};
    DWORD len = GetModuleFileNameW(g_selfModule, path, MAX_PATH);
    if (len == 0)
        return L".\\";

    std::wstring full(path, len);
    const size_t slash = full.find_last_of(L'\\');
    g_rootDir = (slash == std::wstring::npos) ? L".\\" : full.substr(0, slash + 1);
    return g_rootDir.c_str();
}

int SeenStateCount()
{
    return static_cast<int>(g_stateCount);
}

void OnLuaStateCreated(void* L)
{
    InterlockedIncrement(&g_stateCount);
    LuaStateCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        if (!g_callback)
        {
            // The CLR has not booted yet; replay this state once it has.
            g_pendingStates.push_back(L);
            return;
        }
        callback = g_callback;
    }
    callback(L);
}

void SetLuaCloseCallback(LuaCloseCallback cb)
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    g_closeCallback = cb;
    SMLOG("lua_State teardown callback installed");
}

void OnLuaStateClosing(void* L)
{
    LuaCloseCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        callback = g_closeCallback;

        // A state queued for replay is about to stop existing; drop it so the
        // managed side is never handed a dangling lua_State*.
        for (auto it = g_pendingStates.begin(); it != g_pendingStates.end(); )
            it = (*it == L) ? g_pendingStates.erase(it) : it + 1;
    }
    if (callback)
        callback(L);
}

void SetLuaReadyCallback(LuaReadyCallback cb)
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    g_readyCallback = cb;
}

bool NotifyLuaRunning(void* L)
{
    if (g_readyDone)
        return true;

    LuaReadyCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        callback = g_readyCallback;
    }
    if (!callback)
        return false;

    if (callback(L) == 0)
        return false;

    InterlockedExchange(&g_readyDone, 1);
    return true;
}

void SetScriptLoadCallback(ScriptLoadCallback load, ScriptFreeCallback free)
{
    std::lock_guard<std::mutex> lock(g_scriptMutex);
    g_scriptLoad = load;
    g_scriptFree = free;
    SMLOG("script transform callback installed");
}

bool TransformScript(const char* name, const char* source, size_t length,
                     const char** outSource, size_t* outLength)
{
    ScriptLoadCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_scriptMutex);
        callback = g_scriptLoad;
    }
    if (!callback || !source)
        return false;

    return callback(name ? name : "", source, length, outSource, outLength) != 0;
}

void ReleaseScriptSource(const char* buffer)
{
    ScriptFreeCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_scriptMutex);
        callback = g_scriptFree;
    }
    if (callback && buffer)
        callback(buffer);
}

void SetSetFenvCallback(SetFenvCallback cb)
{
    std::lock_guard<std::mutex> lock(g_scriptMutex);
    g_setFenv = cb;
    SMLOG("script environment seeding enabled");
}

void SeedScriptEnvironment(void* L)
{
    SetFenvCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_scriptMutex);
        callback = g_setFenv;
    }
    if (callback)
        callback(L);
}

void SetFileOpenCallback(FileOpenCallback cb)
{
    std::lock_guard<std::mutex> lock(g_scriptMutex);
    g_fileOpen = cb;
    SMLOG("asset redirection enabled");
}

bool RedirectFileOpen(const wchar_t* path, wchar_t* out, int outChars)
{
    FileOpenCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_scriptMutex);
        callback = g_fileOpen;
    }
    if (!callback || !path)
        return false;

    return callback(path, out, outChars) != 0;
}

void SetLuaStateCallback(LuaStateCallback cb)
{
    std::vector<void*> replay;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_callback = cb;
        replay.swap(g_pendingStates);
    }

    SMLOG("managed lua_State callback installed; replaying %zu state(s)", replay.size());
    if (!cb)
        return;
    for (void* L : replay)
        cb(L);
}

} // namespace smloader

namespace {

DWORD WINAPI BootThread(LPVOID)
{
    // The Windows loader finishes resolving the exe's imports after our
    // DllMain runs, which can overwrite the slot we patched. Re-take it for a
    // short while before the game gets far enough to create its lua_State.
    for (int i = 0; i < 200; ++i)
    {
        smloader::iat::Reapply();
        Sleep(5);
    }

    smloader::iat::HookFileApis();
    smloader::clr::Start();

    // Scrap Mechanic does not create a lua_State until a world loads, so a
    // quiet log is expected at the main menu. These checkpoints make the
    // difference between "nothing to hook yet" and "we lost the hook" visible.
    const int checkpoints[] = { 15, 60, 180 };
    int elapsed = 0;
    for (int seconds : checkpoints)
    {
        Sleep((seconds - elapsed) * 1000);
        elapsed = seconds;
        SMLOG("status at %ds: hook %s, lua_States seen %d, late modules hooked %d",
              elapsed,
              smloader::iat::IsIntact() ? "intact" : "LOST",
              smloader::SeenStateCount(),
              smloader::iat::LateHookedCount());
    }
    return 0;
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason != DLL_PROCESS_ATTACH)
        return TRUE;

    DisableThreadLibraryCalls(module);
    smloader::g_selfModule = module;

    smloader::log::Init();
    SMLOG("SMLoader shim attached to pid %lu", GetCurrentProcessId());

    smloader::iat::InstallLuaHooks();

    // CoreCLR must not be started under the loader lock.
    HANDLE thread = CreateThread(nullptr, 0, BootThread, nullptr, 0, nullptr);
    if (thread)
        CloseHandle(thread);
    else
        SMLOG("FAILED: CreateThread for boot thread, error %lu", GetLastError());

    return TRUE;
}
