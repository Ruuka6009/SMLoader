#include "log.h"
#include "shim.h"

#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>

namespace {
std::wstring g_logPath;
std::mutex   g_mutex;

// Held open for the life of the process rather than reopened per line. Under a
// script-heavy world load that was hundreds of open/close pairs on the thread
// compiling scripts.
//
// FILE_APPEND_DATA is what makes sharing this file with the managed side safe:
// an append through such a handle is atomic against other appenders, so two
// writers cannot interleave halfway through a line.
HANDLE       g_handle = INVALID_HANDLE_VALUE;

HANDLE OpenAppendHandle()
{
    return CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA,
                       FILE_SHARE_READ | FILE_SHARE_WRITE,
                       nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
}
}

namespace smloader::log {

void Init()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_logPath.empty())
        return;

    g_logPath = std::wstring(RootDir()) + L"smloader.log";

    // Truncate on each launch so the log always describes the current run.
    // Share writes as well as reads: the managed side appends to this same
    // file, and denying it a handle silently loses the lines that explain a
    // failed boot.
    HANDLE h = CreateFileW(g_logPath.c_str(), GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h != INVALID_HANDLE_VALUE)
        CloseHandle(h);

    g_handle = OpenAppendHandle();
}

void Write(const char* fmt, ...)
{
    char body[1024];
    va_list args;
    va_start(args, fmt);
    _vsnprintf_s(body, sizeof(body), _TRUNCATE, fmt, args);
    va_end(args);

    SYSTEMTIME st;
    GetLocalTime(&st);

    char line[1200];
    int n = _snprintf_s(line, sizeof(line), _TRUNCATE,
                        "[%02d:%02d:%02d.%03d] [shim] %s\r\n",
                        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, body);
    if (n <= 0)
        return;

    OutputDebugStringA(line);

    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_logPath.empty())
        return;

    // One retry: something outside the process can invalidate the handle, and
    // losing the log for the rest of the session over it would be worse than
    // the reopen.
    if (g_handle == INVALID_HANDLE_VALUE)
        g_handle = OpenAppendHandle();
    if (g_handle == INVALID_HANDLE_VALUE)
        return;

    DWORD written = 0;
    if (!WriteFile(g_handle, line, static_cast<DWORD>(n), &written, nullptr))
    {
        CloseHandle(g_handle);
        g_handle = INVALID_HANDLE_VALUE;
    }
}

void Shutdown()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_handle != INVALID_HANDLE_VALUE)
    {
        CloseHandle(g_handle);
        g_handle = INVALID_HANDLE_VALUE;
    }
}

} // namespace smloader::log
