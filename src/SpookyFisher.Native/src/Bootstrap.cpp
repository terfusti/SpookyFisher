// Loads SpookyFisher.Managed.dll through Terraria's already-loaded CLR v4.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "Bootstrap.h"
#include "Overlay.h"
#include <metahost.h>
#include <mscoree.h>
#include <wrl/client.h>
#include <cstdio>
#include <cwchar>
#include <string>
#include <stdexcept>
#include <vector>

// Keep this name aligned with the output assembly of your C# project.
static constexpr wchar_t ManagedAssemblyName[] = L"SpookyFisher.Managed.dll";
static constexpr int ToggleKey = VK_F6;
static constexpr int DetachKey = VK_F7;

#pragma comment(lib, "mscoree.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "user32.lib")

using Microsoft::WRL::ComPtr;

static void Log(const wchar_t* message, HRESULT result) noexcept {
    wchar_t line[1024]{};
    swprintf_s(line, L"%.900ls (0x%08lX)\n", message, static_cast<unsigned long>(result));
    OutputDebugStringW(line);
    AppendOverlayLog(L"Native", line);
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
        GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&Log), &module)) return;
    wchar_t path[32768]{};
    DWORD length = GetModuleFileNameW(module, path, 32768);
    if (!length || length >= 32768) return;
    // Use the bridge directory, never Terraria's working directory.
    wchar_t* slash = wcsrchr(path, L'\\');
    if (!slash) return;
    const size_t remaining = 32768 - static_cast<size_t>(slash + 1 - path);
    if (wcscpy_s(slash + 1, remaining, L"SpookyFisher.bootstrap.log") != 0) return;
    FILE* file = nullptr;
    if (_wfopen_s(&file, path, L"a, ccs=UTF-8") == 0 && file) {
        fwprintf(file, L"%ls", line);
        fclose(file);
    }
}

static DWORD RunManaged(const wchar_t* assemblyPath, const wchar_t* eventPrefix) {
    try {
        if (!assemblyPath || !*assemblyPath) return static_cast<DWORD>(E_INVALIDARG);
        Log(L"Starting managed initialization", S_OK);

        // Mono/CoreCLR are different hosting systems; do not start an unrelated CLR.
        if (GetModuleHandleW(L"coreclr.dll") || GetModuleHandleW(L"mono-2.0-bdwgc.dll") ||
            GetModuleHandleW(L"mono-2.0-sgen.dll") || GetModuleHandleW(L"mono.dll")) {
            Log(L"Mono/CoreCLR detected. This bridge only supports .NET Framework CLR v4", E_NOTIMPL);
            return static_cast<DWORD>(E_NOTIMPL);
        }

        ComPtr<ICLRMetaHost> meta;
        HRESULT hr = CLRCreateInstance(CLSID_CLRMetaHost, IID_PPV_ARGS(meta.GetAddressOf()));
        if (FAILED(hr)) { Log(L"CLRCreateInstance failed", hr); return static_cast<DWORD>(hr); }
        ComPtr<IEnumUnknown> runtimes;
        hr = meta->EnumerateLoadedRuntimes(GetCurrentProcess(), runtimes.GetAddressOf());
        if (FAILED(hr)) { Log(L"EnumerateLoadedRuntimes failed", hr); return static_cast<DWORD>(hr); }

        ComPtr<ICLRRuntimeHost> host;
        while (true) {
            ComPtr<IUnknown> unknown;
            ULONG fetched = 0;
            hr = runtimes->Next(1, unknown.GetAddressOf(), &fetched);
            if (hr == S_FALSE) break;
            if (FAILED(hr)) { Log(L"Runtime enumeration failed", hr); return static_cast<DWORD>(hr); }
            if (!fetched) break;
            ComPtr<ICLRRuntimeInfo> info;
            hr = unknown.As(&info);
            if (FAILED(hr)) { Log(L"Runtime QueryInterface failed", hr); return static_cast<DWORD>(hr); }
            wchar_t version[64]{};
            DWORD size = 64;
            hr = info->GetVersionString(version, &size);
            if (FAILED(hr)) { Log(L"GetVersionString failed", hr); return static_cast<DWORD>(hr); }
            if (wcsncmp(version, L"v4.", 3) != 0) continue;
            hr = info->GetInterface(CLSID_CLRRuntimeHost, IID_PPV_ARGS(host.GetAddressOf()));
            if (FAILED(hr)) { Log(L"GetInterface(ICLRRuntimeHost) failed", hr); return static_cast<DWORD>(hr); }
            Log(L"Attached to existing CLR v4", S_OK);
            break;
        }
        if (!host) {
            Log(L"No loaded CLR v4 found. Reach Terraria's main menu and check its runtime", E_NOTIMPL);
            return static_cast<DWORD>(E_NOTIMPL);
        }

        // Entry.Start must be public static int Start(string argument).
        // The running Terraria assembly must be in this default AppDomain.
        DWORD managedResult = 0;
        hr = host->ExecuteInDefaultAppDomain(assemblyPath, L"SpookyFisher.Entry",
            L"Start", eventPrefix, &managedResult);
        if (FAILED(hr)) { Log(L"ExecuteInDefaultAppDomain failed", hr); return static_cast<DWORD>(hr); }
        if (managedResult != 0) {
            Log(L"Entry.Start returned an error; inspect your managed exception log",
                static_cast<HRESULT>(managedResult));
            return static_cast<DWORD>(E_FAIL);
        }
        Log(L"Entry.Start returned success", S_OK);
        // Never Stop() the game's runtime or unload its AppDomain.
        return 0;
    }
    catch (...) {
        Log(L"Native bootstrap threw an exception", E_FAIL);
        return static_cast<DWORD>(E_FAIL);
    }
}

DWORD WINAPI RunBootstrap(void* parameter) {
    const HMODULE module = static_cast<HMODULE>(parameter);
    Log(L"SpookyFisher loaded. Insert: menu; F6: fishing; F7: detach", S_OK);

    HANDLE toggleEvent = nullptr, stopEvent = nullptr, detachedEvent = nullptr;
    DWORD result = static_cast<DWORD>(E_FAIL);
    bool canUnload = false;
    try {
        const std::wstring prefix = L"Local\\SpookyFisher." + std::to_wstring(GetCurrentProcessId());
        toggleEvent = CreateEventW(nullptr, FALSE, FALSE, (prefix + L".Toggle").c_str());
        stopEvent = CreateEventW(nullptr, TRUE, FALSE, (prefix + L".Stop").c_str());
        detachedEvent = CreateEventW(nullptr, TRUE, FALSE, (prefix + L".Detached").c_str());
        if (!toggleEvent || !stopEvent || !detachedEvent) {
            Log(L"Could not create control events", HRESULT_FROM_WIN32(GetLastError()));
            throw std::runtime_error("Control events unavailable");
        }
        ResetEvent(stopEvent);
        ResetEvent(detachedEvent);
        std::vector<wchar_t> buffer(32768);
        const DWORD length = GetModuleFileNameW(module, buffer.data(),
            static_cast<DWORD>(buffer.size()));
        if (!length || length >= buffer.size()) {
            Log(L"Could not resolve native DLL path", E_FAIL);
            throw std::runtime_error("Native path unavailable");
        }
        std::wstring path(buffer.data(), length);
        const size_t slash = path.find_last_of(L"\\/");
        if (slash == std::wstring::npos) throw std::runtime_error("Invalid native path");
        path = path.substr(0, slash + 1) + ManagedAssemblyName;
        Log(path.c_str(), S_OK);
        const DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES ||
            (attributes & FILE_ATTRIBUTE_DIRECTORY)) {
            Log(L"Managed DLL is missing beside the native DLL", HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND));
            throw std::runtime_error("Managed DLL missing");
        }
        result = RunManaged(path.c_str(), prefix.c_str());
        if (result == 0) {
            bool toggleWasDown = (GetAsyncKeyState(ToggleKey) & 0x8000) != 0;
            bool stopWasDown = (GetAsyncKeyState(DetachKey) & 0x8000) != 0;
            bool stopRequested = false;
            Log(L"Ready. AFK starts OFF. Aim at the water, then press F6", S_OK);
            while (true) {
                const bool toggleDown = (GetAsyncKeyState(ToggleKey) & 0x8000) != 0;
                const bool stopDown = (GetAsyncKeyState(DetachKey) & 0x8000) != 0;
                HWND foreground = GetForegroundWindow();
                DWORD foregroundPid = 0;
                GetWindowThreadProcessId(foreground, &foregroundPid);
                const bool focused = foregroundPid == GetCurrentProcessId();
                if (focused && toggleDown && !toggleWasDown && !stopRequested)
                    SetEvent(toggleEvent);
                if (focused && stopDown && !stopWasDown && !stopRequested) {
                    stopRequested = true;
                    SetEvent(stopEvent);
                    Log(L"Detach requested. Waiting for game-thread cleanup", S_OK);
                }
                toggleWasDown = toggleDown;
                stopWasDown = stopDown;
                const DWORD wait = WaitForSingleObject(detachedEvent, 30);
                if (wait == WAIT_OBJECT_0) { canUnload = true; break; }
                if (wait == WAIT_FAILED) {
                    Log(L"Detach wait failed; native bridge will remain loaded", HRESULT_FROM_WIN32(GetLastError()));
                    result = static_cast<DWORD>(E_FAIL);
                    break;
                }
            }
        }
    }
    catch (...) {
        Log(L"Initialization worker threw an exception", E_FAIL);
        result = static_cast<DWORD>(E_FAIL);
    }
    if (canUnload) Log(L"Managed patches removed. Unloading native bridge", S_OK);
    if (toggleEvent) CloseHandle(toggleEvent);
    if (stopEvent) CloseHandle(stopEvent);
    if (detachedEvent) CloseHandle(detachedEvent);
    // C++ locals and COM references have been destroyed before self-unloading.
    // Only unload after C# acknowledges removal of this project's Harmony patches.
    if (canUnload) FreeLibraryAndExitThread(module, 0);
    return result;
}

