#include "clr_host.h"
#include "shim.h"
#include "log.h"

#include <string>
#include <vector>

namespace {

// ---- Minimal hostfxr / coreclr ABI ----------------------------------------
// Declared by hand so the shim builds against nothing but the Windows SDK.

using char_t = wchar_t;

using hostfxr_initialize_for_runtime_config_fn =
    int(__cdecl*)(const char_t* runtimeConfigPath, const void* parameters, void** hostContext);
using hostfxr_get_runtime_delegate_fn =
    int(__cdecl*)(void* hostContext, int type, void** delegate);
using hostfxr_close_fn = int(__cdecl*)(void* hostContext);

using load_assembly_and_get_function_pointer_fn =
    int(__cdecl*)(const char_t* assemblyPath, const char_t* typeName, const char_t* methodName,
                  const char_t* delegateTypeName, void* reserved, void** delegate);

constexpr int            kHdtLoadAssemblyAndGetFunctionPointer = 5;
const char_t* const      kUnmanagedCallersOnly = reinterpret_cast<const char_t*>(-1);

// The managed entry point's signature (see SMLoader.Core.Entry.Boot).
using managed_boot_fn = int(__cdecl*)(smloader::BootContext*);

// ---- dotnet discovery ------------------------------------------------------

std::wstring ReadRegistryString(HKEY root, const wchar_t* subKey, const wchar_t* value)
{
    HKEY key{};
    if (RegOpenKeyExW(root, subKey, 0, KEY_READ | KEY_WOW64_64KEY, &key) != ERROR_SUCCESS)
        return {};

    wchar_t buffer[MAX_PATH]{};
    DWORD   size = sizeof(buffer);
    DWORD   type = 0;
    LSTATUS status = RegQueryValueExW(key, value, nullptr, &type,
                                      reinterpret_cast<BYTE*>(buffer), &size);
    RegCloseKey(key);

    if (status != ERROR_SUCCESS || type != REG_SZ)
        return {};
    return buffer;
}

std::wstring FindDotnetRoot()
{
    wchar_t envBuffer[MAX_PATH]{};
    if (GetEnvironmentVariableW(L"DOTNET_ROOT", envBuffer, MAX_PATH) > 0)
        return envBuffer;

    std::wstring fromRegistry = ReadRegistryString(
        HKEY_LOCAL_MACHINE,
        L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64",
        L"InstallLocation");
    if (!fromRegistry.empty())
        return fromRegistry;

    return L"C:\\Program Files\\dotnet";
}

// Parses "10.0.8" into a sortable triple; anything unparsable sorts lowest.
bool ParseVersion(const std::wstring& text, int out[3])
{
    out[0] = out[1] = out[2] = -1;
    int   part = 0;
    int   value = 0;
    bool  sawDigit = false;
    for (wchar_t ch : text)
    {
        if (ch >= L'0' && ch <= L'9')
        {
            value = value * 10 + (ch - L'0');
            sawDigit = true;
        }
        else if (ch == L'.')
        {
            if (!sawDigit || part >= 2)
                break;
            out[part++] = value;
            value = 0;
            sawDigit = false;
        }
        else
        {
            break; // preview suffix such as "-rc.1"; stop here
        }
    }
    if (sawDigit && part <= 2)
        out[part] = value;
    return out[0] >= 0;
}

std::wstring FindHostFxr()
{
    const std::wstring fxrRoot = FindDotnetRoot() + L"\\host\\fxr";

    WIN32_FIND_DATAW findData{};
    HANDLE find = FindFirstFileW((fxrRoot + L"\\*").c_str(), &findData);
    if (find == INVALID_HANDLE_VALUE)
    {
        SMLOG("no hostfxr versions under %ws", fxrRoot.c_str());
        return {};
    }

    std::wstring best;
    int          bestVersion[3] = { -1, -1, -1 };
    do
    {
        if (!(findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
            continue;
        if (findData.cFileName[0] == L'.')
            continue;

        int version[3]{};
        if (!ParseVersion(findData.cFileName, version))
            continue;

        if (version[0] > bestVersion[0] ||
            (version[0] == bestVersion[0] && version[1] > bestVersion[1]) ||
            (version[0] == bestVersion[0] && version[1] == bestVersion[1] && version[2] > bestVersion[2]))
        {
            bestVersion[0] = version[0];
            bestVersion[1] = version[1];
            bestVersion[2] = version[2];
            best = findData.cFileName;
        }
    } while (FindNextFileW(find, &findData));
    FindClose(find);

    if (best.empty())
        return {};

    return fxrRoot + L"\\" + best + L"\\hostfxr.dll";
}

} // namespace

namespace smloader::clr {

bool Start()
{
    const std::wstring hostfxrPath = FindHostFxr();
    if (hostfxrPath.empty())
    {
        SMLOG("FAILED: could not locate hostfxr.dll - is the .NET runtime installed?");
        return false;
    }
    SMLOG("hostfxr: %ws", hostfxrPath.c_str());

    HMODULE hostfxr = LoadLibraryW(hostfxrPath.c_str());
    if (!hostfxr)
    {
        SMLOG("FAILED: LoadLibrary(hostfxr) error %lu", GetLastError());
        return false;
    }

    auto init = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        GetProcAddress(hostfxr, "hostfxr_initialize_for_runtime_config"));
    auto getDelegate = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        GetProcAddress(hostfxr, "hostfxr_get_runtime_delegate"));
    auto close = reinterpret_cast<hostfxr_close_fn>(
        GetProcAddress(hostfxr, "hostfxr_close"));

    if (!init || !getDelegate || !close)
    {
        SMLOG("FAILED: hostfxr is missing expected exports");
        return false;
    }

    const std::wstring root       = RootDir();
    const std::wstring configPath = root + L"SMLoader.Core.runtimeconfig.json";
    const std::wstring assemblyPath = root + L"SMLoader.Core.dll";

    void* hostContext = nullptr;
    int   rc = init(configPath.c_str(), nullptr, &hostContext);
    // 1 == Success_HostAlreadyInitialized, 2 == Success_DifferentRuntimeProperties
    if ((rc != 0 && rc != 1 && rc != 2) || hostContext == nullptr)
    {
        SMLOG("FAILED: hostfxr_initialize_for_runtime_config rc=0x%x config=%ws",
              rc, configPath.c_str());
        if (hostContext)
            close(hostContext);
        return false;
    }

    void* rawDelegate = nullptr;
    rc = getDelegate(hostContext, kHdtLoadAssemblyAndGetFunctionPointer, &rawDelegate);
    close(hostContext);

    if (rc != 0 || rawDelegate == nullptr)
    {
        SMLOG("FAILED: hostfxr_get_runtime_delegate rc=0x%x", rc);
        return false;
    }

    auto loadAssembly = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(rawDelegate);

    managed_boot_fn boot = nullptr;
    rc = loadAssembly(assemblyPath.c_str(),
                      L"SMLoader.Core.Entry, SMLoader.Core",
                      L"Boot",
                      kUnmanagedCallersOnly,
                      nullptr,
                      reinterpret_cast<void**>(&boot));
    if (rc != 0 || boot == nullptr)
    {
        SMLOG("FAILED: could not resolve SMLoader.Core.Entry.Boot rc=0x%x path=%ws",
              rc, assemblyPath.c_str());
        return false;
    }

    BootContext context{};
    context.size                 = sizeof(BootContext);
    context.rootDir              = root.c_str();
    context.setLuaStateCallback  = &SetLuaStateCallback;
    context.setLuaReadyCallback  = &SetLuaReadyCallback;
    context.setScriptLoadCallback = &SetScriptLoadCallback;
    context.setSetFenvCallback   = &SetSetFenvCallback;
    context.setFileOpenCallback  = &SetFileOpenCallback;
    context.setLuaCloseCallback  = &SetLuaCloseCallback;
    context.setPathFilter        = &SetPathFilter;

    SMLOG("calling managed Boot");
    const int bootResult = boot(&context);
    SMLOG("managed Boot returned %d", bootResult);
    return bootResult == 0;
}

} // namespace smloader::clr
