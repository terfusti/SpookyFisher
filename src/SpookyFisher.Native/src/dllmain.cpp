#include "Bootstrap.h"

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        // Managed initialization and all waits belong outside loader lock.
        const HANDLE thread = CreateThread(nullptr, 0, RunBootstrap, module, 0, nullptr);
        if (!thread) return FALSE;
        CloseHandle(thread);
    }
    return TRUE;
}
