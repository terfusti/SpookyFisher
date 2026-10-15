#pragma once
#include <Windows.h>
#include <cstdint>

// This ABI contains plain data only; XNA owns all graphics resources and rendering.
struct OverlayCommand {
    float Left, Top, Right, Bottom;
    int ElementCount, IndexOffset, VertexOffset, VertexCount;
    std::uint64_t TextureId;
};

struct OverlayFrame {
    int VertexCount, IndexCount, CommandCount, Reserved;
    const void* Vertices;
    const void* Indices;
    const OverlayCommand* Commands;
    float Width, Height;
};

// Copied on the game thread; no Terraria objects or retained managed pointers.
struct OverlayFishingState {
    // Enabled: 0 = off, 1 = fishing, 2 = waiting for the first manual cast.
    int Enabled, CanStart, HasAim, Bobbers, BaitPower, FailedCasts, Retrievals;
    float AimX, AimY;
};

void AppendOverlayLog(const wchar_t* source, const wchar_t* message) noexcept;

extern "C" {
    int __cdecl SF_UiInitialize(HWND window);
    int __cdecl SF_UiShutdown();
    int __cdecl SF_UiCaptureInput();
    int __cdecl SF_UiVisible();
    int __cdecl SF_UiTakeActions();
    void __cdecl SF_UiSetFishing(const OverlayFishingState* state, const wchar_t* status, const wchar_t* rod);
    int __cdecl SF_UiConfigureDisplay(int width, int height);
    int __cdecl SF_UiFont(const void** pixels, int* width, int* height);
    int __cdecl SF_UiFrame(int width, int height, OverlayFrame* frame);
    void __cdecl SF_Log(const wchar_t* message);
}
