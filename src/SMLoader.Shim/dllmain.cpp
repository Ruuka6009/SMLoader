#include "shim.h"
#include "iat_hook.h"
#include "clr_host.h"
#include "log.h"
#include "native_plugins.h"

#include <atomic>
#include <mutex>
#include <string>
#include <vector>

namespace smloader {

HMODULE g_selfModule = nullptr;

namespace {

// g_stateMutex still guards g_pendingStates, which is a real container with a
// real invariant. The callbacks below are write-once pointers read from the
// hottest paths the shim touches - RedirectFileOpen runs on every file open in
// the process - so they are atomics instead. A process-wide mutex to read one
// pointer bounces that cache line between cores under the engine's concurrent
// asset streaming, for no ordering that acquire/release does not already give.
std::mutex               g_stateMutex;
std::vector<void*>       g_pendingStates;   // seen before the CLR was ready
std::wstring             g_rootDir;
volatile long            g_stateCount = 0;
volatile long            g_readyDone = 0;

std::atomic<LuaStateCallback>   g_callback{nullptr};
std::atomic<LuaReadyCallback>   g_readyCallback{nullptr};
std::atomic<ScriptLoadCallback> g_scriptLoad{nullptr};
std::atomic<ScriptFreeCallback> g_scriptFree{nullptr};
std::atomic<SetFenvCallback>    g_setFenv{nullptr};
std::atomic<FileOpenCallback>   g_fileOpen{nullptr};
std::atomic<LuaCloseCallback>   g_closeCallback{nullptr};

// Substrings the managed side wants to see, lowercased. Published as a whole
// vector and read without a lock from every file open in the process.
//
// Never reclaimed. Readers are lock-free on the hottest path the shim has and
// updates happen a handful of times at startup, so freeing the old vector
// safely would mean hazard pointers or an epoch scheme to reclaim a few dozen
// bytes. Deliberate, and bounded by the number of registrations.
std::atomic<const std::vector<std::wstring>*> g_pathNeedles{nullptr};

// ASCII-only fold. The managed side lowercases with ToLowerInvariant, and a
// needle carrying anything outside ASCII disables the filter rather than risk
// disagreeing with OrdinalIgnoreCase on the managed side - see SetPathFilter.
inline wchar_t Fold(wchar_t c)
{
    return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c - L'A' + L'a') : c;
}

bool ContainsFolded(const wchar_t* haystack, const std::wstring& needle)
{
    // An empty needle is the "match everything" marker.
    if (needle.empty())
        return true;

    for (const wchar_t* h = haystack; *h; ++h)
    {
        size_t i = 0;
        while (i < needle.size() && h[i] && Fold(h[i]) == needle[i])
            ++i;

        if (i == needle.size())
            return true;
    }
    return false;
}

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

    // Still under the lock: the decision to queue has to be atomic with the
    // push, or a state can be dropped between the two.
    LuaStateCallback callback = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        callback = g_callback.load(std::memory_order_acquire);
        if (!callback)
        {
            // The CLR has not booted yet; replay this state once it has.
            //
            // Capped, because "not yet" and "never" look identical from here:
            // if clr::Start failed, every state the game ever creates would be
            // pushed into a vector nothing drains.
            constexpr size_t kMaxPending = 16;
            if (g_pendingStates.size() < kMaxPending)
            {
                g_pendingStates.push_back(L);
            }
            else if (g_pendingStates.size() == kMaxPending)
            {
                g_pendingStates.push_back(nullptr);   // marks the cap, logged once
                SMLOG("more than %zu lua_States queued and the CLR has not booted; "
                      "no further states will be replayed", kMaxPending);
            }
            return;
        }
    }
    callback(L);
}

void SetLuaCloseCallback(LuaCloseCallback cb)
{
    g_closeCallback.store(cb, std::memory_order_release);
    SMLOG("lua_State teardown callback installed");
}

void OnLuaStateClosing(void* L)
{
    {
        // A state queued for replay is about to stop existing; drop it so the
        // managed side is never handed a dangling lua_State*.
        std::lock_guard<std::mutex> lock(g_stateMutex);
        for (auto it = g_pendingStates.begin(); it != g_pendingStates.end(); )
            it = (*it == L) ? g_pendingStates.erase(it) : it + 1;
    }

    if (LuaCloseCallback callback = g_closeCallback.load(std::memory_order_acquire))
        callback(L);
}

void SetLuaReadyCallback(LuaReadyCallback cb)
{
    g_readyCallback.store(cb, std::memory_order_release);
}

bool NotifyLuaRunning(void* L)
{
    if (g_readyDone)
        return true;

    LuaReadyCallback callback = g_readyCallback.load(std::memory_order_acquire);
    if (!callback)
        return false;

    if (callback(L) == 0)
        return false;

    InterlockedExchange(&g_readyDone, 1);
    return true;
}

void SetScriptLoadCallback(ScriptLoadCallback load, ScriptFreeCallback free)
{
    // free first, so a transform can never be handed out before the thing that
    // releases it is visible.
    g_scriptFree.store(free, std::memory_order_release);
    g_scriptLoad.store(load, std::memory_order_release);
    SMLOG("script transform callback installed");
}

bool TransformScript(const char* name, const char* source, size_t length,
                     const char** outSource, size_t* outLength)
{
    ScriptLoadCallback callback = g_scriptLoad.load(std::memory_order_acquire);
    if (!callback || !source)
        return false;

    return callback(name ? name : "", source, length, outSource, outLength) != 0;
}

void ReleaseScriptSource(const char* buffer)
{
    ScriptFreeCallback callback = g_scriptFree.load(std::memory_order_acquire);
    if (callback && buffer)
        callback(buffer);
}

void SetSetFenvCallback(SetFenvCallback cb)
{
    g_setFenv.store(cb, std::memory_order_release);
    SMLOG("script environment seeding enabled");
}

void SeedScriptEnvironment(void* L)
{
    if (SetFenvCallback callback = g_setFenv.load(std::memory_order_acquire))
        callback(L);
}

void SetPathFilter(const wchar_t* needles)
{
    auto* built = new std::vector<std::wstring>();
    bool nonAscii = false;

    if (needles)
    {
        const wchar_t* start = needles;
        for (const wchar_t* p = needles; ; ++p)
        {
            if (*p > 0x7F)
                nonAscii = true;

            if (*p == L'\n' || *p == L'\0')
            {
                if (p > start)
                    built->emplace_back(start, static_cast<size_t>(p - start));
                if (*p == L'\0')
                    break;
                start = p + 1;
            }
        }
    }

    if (nonAscii)
    {
        // Our fold is ASCII-only and the managed matcher is OrdinalIgnoreCase.
        // Rather than risk filtering out a path the managed side would have
        // matched, hand it everything: one empty needle matches all.
        built->clear();
        built->emplace_back();
        SMLOG("path filter: non-ASCII needle, filtering disabled");
    }

    g_pathNeedles.store(built, std::memory_order_release);
    SMLOG("path filter published: %zu needle(s)", built->size());
}

void SetFileOpenCallback(FileOpenCallback cb)
{
    g_fileOpen.store(cb, std::memory_order_release);
    SMLOG("asset redirection enabled");
}

bool RedirectFileOpen(const wchar_t* path, wchar_t* out, int outChars)
{
    FileOpenCallback callback = g_fileOpen.load(std::memory_order_acquire);
    if (!callback || !path)
        return false;

    // Answer here rather than in managed code wherever we can. This runs on
    // whichever thread opened the file, including engine workers that have
    // never run managed code - so every crossing risks a CLR thread attach,
    // allocates a string, and can therefore trigger a GC inside a file open.
    // DLL loads, save files, shader caches and our own log never get that far.
    if (const auto* needles = g_pathNeedles.load(std::memory_order_acquire))
    {
        bool wanted = false;
        for (const auto& needle : *needles)
        {
            if (ContainsFolded(path, needle))
            {
                wanted = true;
                break;
            }
        }

        if (!wanted)
            return false;
    }

    return callback(path, out, outChars) != 0;
}

void SetLuaStateCallback(LuaStateCallback cb)
{
    std::vector<void*> replay;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_callback.store(cb, std::memory_order_release);
        replay.swap(g_pendingStates);
    }

    SMLOG("managed lua_State callback installed; replaying %zu state(s)", replay.size());
    if (!cb)
        return;

    for (void* L : replay)
    {
        if (L)   // the cap marker
            cb(L);
    }
}

} // namespace smloader

namespace {

HANDLE g_statusTimer = nullptr;

void ReportStatus(int elapsedSeconds)
{
    SMLOG("status at %ds: %s; lua_States seen %d, late modules hooked %d",
          elapsedSeconds,
          smloader::iat::HookSummary(),
          smloader::SeenStateCount(),
          smloader::iat::LateHookedCount());
}

VOID CALLBACK StatusTick(PVOID, BOOLEAN)
{
    // Runs on a thread-pool thread. It only formats and appends a line, which
    // is why the boot thread does not have to stay alive to do it.
    static int elapsed = 180;
    elapsed += 5 * 60;
    ReportStatus(elapsed);
}

DWORD WINAPI PluginThread(LPVOID)
{
    smloader::plugins::LoadAll();
    return 0;
}

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

    if (!smloader::clr::Start())
    {
        // Leaving the detours in place would add cost to every luaL_newstate,
        // lua_pcall, luaL_loadbufferx, lua_setfenv and CreateFileW for the rest
        // of the session while doing nothing at all. A player whose .NET install
        // is broken should get a game that runs exactly as fast as an unmodded
        // one, and one line saying why.
        SMLOG("the managed side did not start; removing every hook so the game "
              "runs unmodified");
        smloader::iat::UnhookAll();
        return 0;
    }

    // Scrap Mechanic does not create a lua_State until a world loads, so a
    // quiet log is expected at the main menu. These checkpoints make the
    // difference between "nothing to hook yet" and "we lost the hook" visible.
    const int checkpoints[] = { 15, 60, 180 };
    int elapsed = 0;
    for (int seconds : checkpoints)
    {
        Sleep((seconds - elapsed) * 1000);
        elapsed = seconds;
        ReportStatus(elapsed);
    }

    // Then every five minutes for the life of the process, on a timer-queue
    // thread rather than this one. Stopping at 180s meant a hook lost during a
    // long session left no trace at all, and a hook lost after three minutes is
    // exactly the interesting case - but parking a dedicated thread in Sleep
    // for the rest of the session to say so is not the way to get it.
    if (!CreateTimerQueueTimer(&g_statusTimer, nullptr, StatusTick, nullptr,
                               5 * 60 * 1000, 5 * 60 * 1000, WT_EXECUTEDEFAULT))
    {
        SMLOG("CreateTimerQueueTimer failed (%lu); status checkpoints stop here",
              GetLastError());
    }

    return 0;
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_DETACH)
    {
        // reserved is non-null when the process is terminating. The address
        // space is going away, other threads are already dead, and touching
        // another module's imports under the loader lock at that point buys
        // nothing and can deadlock. Close the log and let the OS do the rest.
        if (reserved != nullptr)
        {
            smloader::log::Shutdown();
            return TRUE;
        }

        // A real FreeLibrary: every slot we patched points into memory that is
        // about to be unmapped, so the process dies on the next call through
        // one unless they are restored first.
        SMLOG("shim unloading; restoring hooks");

        // INVALID_HANDLE_VALUE waits for a callback already running to finish,
        // which matters because that callback touches the log we close below.
        if (g_statusTimer)
        {
            DeleteTimerQueueTimer(nullptr, g_statusTimer, INVALID_HANDLE_VALUE);
            g_statusTimer = nullptr;
        }

        smloader::iat::UnhookAll();
        smloader::log::Shutdown();
        return TRUE;
    }

    if (reason != DLL_PROCESS_ATTACH)
        return TRUE;

    DisableThreadLibraryCalls(module);
    smloader::g_selfModule = module;

    smloader::log::Init();
    SMLOG("SMLoader shim attached to pid %lu", GetCurrentProcessId());

    smloader::iat::InstallLuaHooks();

    // Started before the boot thread, and not on it, because the two have
    // opposite deadlines. A graphics plugin must be mapped before the engine
    // creates its device, which can happen within a few hundred milliseconds;
    // the boot thread spends its first second re-taking the IAT slot and then
    // starts CoreCLR. Neither should be waiting on the other. LoadLibraryW is
    // also not something to do under the loader lock, which is why it is a
    // thread at all.
    HANDLE plugins = CreateThread(nullptr, 0, PluginThread, nullptr, 0, nullptr);
    if (plugins)
    {
        CloseHandle(plugins);
    }
    else
    {
        SMLOG("FAILED: CreateThread for native plugins, error %lu", GetLastError());
        smloader::plugins::Cancel();
    }

    // CoreCLR must not be started under the loader lock.
    HANDLE thread = CreateThread(nullptr, 0, BootThread, nullptr, 0, nullptr);
    if (thread)
        CloseHandle(thread);
    else
        SMLOG("FAILED: CreateThread for boot thread, error %lu", GetLastError());

    return TRUE;
}
