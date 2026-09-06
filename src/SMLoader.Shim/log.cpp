#include "log.h"
#include "shim.h"

#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>

namespace {
std::wstring g_logPath;
std::mutex   g_mutex;
}

namespace smloader::log {

void Init()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_logPath.empty())
        return;

    g_logPath = std::wstring(RootDir()) + L"smloader.log";

    // Truncate on each launch so the log always describes the current run.
    HANDLE h = CreateFileW(g_logPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ,
                           nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h != INVALID_HANDLE_VALUE)
        CloseHandle(h);
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

    HANDLE h = CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE)
        return;

    DWORD written = 0;
    WriteFile(h, line, static_cast<DWORD>(n), &written, nullptr);
    CloseHandle(h);
}

} // namespace smloader::log
