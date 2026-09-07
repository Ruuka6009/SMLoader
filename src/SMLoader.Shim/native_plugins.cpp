#include "native_plugins.h"
#include "log.h"

#include <windows.h>

#include <chrono>
#include <condition_variable>
#include <cwchar>
#include <mutex>
#include <string>
#include <vector>

namespace {

struct State
{
    std::mutex              mutex;
    std::condition_variable ready;
    bool                    done = false;
    std::wstring            report;
};

// Function-local static, so it is constructed on first use no matter which of
// the two threads gets here first.
State& state()
{
    static State s;
    return s;
}

std::wstring ReadEnvironment(const wchar_t* name)
{
    const DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
    if (needed == 0)
        return {};

    std::wstring value(needed, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), needed);
    if (written == 0 || written >= needed)
        return {};

    value.resize(written);
    return value;
}

std::vector<std::wstring> Split(const std::wstring& text, wchar_t separator)
{
    std::vector<std::wstring> parts;
    size_t start = 0;

    while (start <= text.size())
    {
        const size_t end = text.find(separator, start);
        const size_t stop = (end == std::wstring::npos) ? text.size() : end;

        if (stop > start)
            parts.emplace_back(text, start, stop - start);

        if (end == std::wstring::npos)
            break;
        start = end + 1;
    }

    return parts;
}

// The Windows loader tracks modules by base name as well as by path, so asking
// for Mods\ReShade\dxgi.dll once System32\dxgi.dll is already mapped can hand
// back the system one. Everything then looks like it worked while the plugin
// has not run a single instruction, which is exactly the failure a player has
// no way to diagnose. Loading this early makes it unlikely; checking makes it
// visible when it happens anyway.
//
// Returns the module's own path when it differs from the one we asked for, and
// an empty string when they agree (or when the path could not be read, which is
// not evidence of anything).
std::wstring PathIfDifferent(HMODULE module, const std::wstring& requested)
{
    std::wstring actual(1024, L'\0');
    const DWORD length = GetModuleFileNameW(module, actual.data(),
                                            static_cast<DWORD>(actual.size()));
    if (length == 0 || length >= actual.size())
        return {};

    actual.resize(length);
    return _wcsicmp(actual.c_str(), requested.c_str()) == 0 ? std::wstring{} : actual;
}

// Write-once: Report() hands out a pointer into `report`, so a second publish
// would move the string out from under a caller still holding it.
void Publish(std::wstring report)
{
    {
        std::lock_guard<std::mutex> lock(state().mutex);
        if (state().done)
            return;

        state().report = std::move(report);
        state().done = true;
    }
    state().ready.notify_all();
}

} // namespace

namespace smloader::plugins {

void LoadAll()
{
    // Defence in depth. The launcher states the same decision twice - the list
    // of paths, and this - so that a SMLOADER_NATIVE_PLUGINS left in someone's
    // environment cannot load anything the launcher did not approve this run.
    if (ReadEnvironment(L"SMLOADER_NO_NATIVE") == L"1")
    {
        SMLOG("native plugins are not enabled for this launch");
        Publish({});
        return;
    }

    const std::vector<std::wstring> paths =
        Split(ReadEnvironment(L"SMLOADER_NATIVE_PLUGINS"), L';');

    if (paths.empty())
    {
        Publish({});
        return;
    }

    std::wstring report;
    for (const std::wstring& path : paths)
    {
        const HMODULE module = LoadLibraryW(path.c_str());
        if (module == nullptr)
        {
            const DWORD error = GetLastError();
            SMLOG("FAILED: native plugin %ws, LoadLibrary error %lu", path.c_str(), error);
            report += L"err " + std::to_wstring(error) + L"|" + path + L"\n";
            continue;
        }

        const std::wstring elsewhere = PathIfDifferent(module, path);
        if (!elsewhere.empty())
        {
            SMLOG("FAILED: native plugin %ws resolved to %ws, which was already "
                  "mapped under that name; rename the DLL",
                  path.c_str(), elsewhere.c_str());
            report += L"other " + elsewhere + L"|" + path + L"\n";
            continue;
        }

        SMLOG("native plugin loaded: %ws (base 0x%p)", path.c_str(), static_cast<void*>(module));
        report += L"ok|" + path + L"\n";
    }

    Publish(std::move(report));
}

void Cancel()
{
    Publish({});
}

const wchar_t* Report()
{
    State& s = state();
    std::unique_lock<std::mutex> lock(s.mutex);

    // Bounded, because this is called on the path to the managed Boot. A plugin
    // whose DllMain never returns must not also cost the player the rest of the
    // loader; it just loses its line in the splash.
    s.ready.wait_for(lock, std::chrono::seconds(20), [&s] { return s.done; });

    if (!s.done)
    {
        SMLOG("native plugins are still loading after 20s; the splash will not list them");
        return L"";
    }

    // Written exactly once, before done became true, and never touched again -
    // so this pointer stays valid for the life of the process.
    return s.report.c_str();
}

} // namespace smloader::plugins
