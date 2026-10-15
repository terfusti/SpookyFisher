#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "Overlay.h"
#include "FontAtlas.h"
#include "imgui.h"
#include "backends/imgui_impl_win32.h"
#include <algorithm>
#include <cfloat>
#include <cmath>
#include <cwchar>
#include <deque>
#include <mutex>
#include <string>
#include <stdexcept>
#include <vector>

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND, UINT, WPARAM, LPARAM);

namespace {
    static_assert(sizeof(ImDrawVert) == 20 && sizeof(ImDrawIdx) == 2, "XNA bridge vertex/index layout changed");
    static_assert(sizeof(OverlayCommand) == 40, "Managed draw command layout changed");
    static_assert(sizeof(OverlayFishingState) == 36, "Managed fishing state layout changed");
    constexpr size_t MaxLines = 2048;
    std::mutex logGate;
    std::deque<std::string> lines;
    std::uint64_t logRevision = 0, snapshotRevision = ~0ULL;
    std::vector<std::string> snapshot;
    HWND gameWindow = nullptr;
    WNDPROC previousProc = nullptr;
    HHOOK messageHook = nullptr;
    ImGuiContext* context = nullptr;
    float fontDensity = 0;
    float uiScale = 1;
    int fontRevision = 0;
    bool reportDisplay = true;
    bool visible = false, releaseGuard = false, autoScroll = true, suspended = false, insertDown = false;
    int pendingActions = 0;
    int selectedPage = 0;
    OverlayFishingState fishing{};
    std::string fishingStatus = "Enter a world to begin.", fishingRod = "No rod equipped";
    constexpr int CaptureMouse = 1, CaptureKeyboard = 2;
    const ImVec4 orange(1.0f, 0.43f, 0.12f, 1.0f);
    ImGuiTextFilter filter;
    std::vector<ImDrawVert> vertices;
    std::vector<ImDrawIdx> indices;
    std::vector<OverlayCommand> commands;

    void BuildFont(float density) {
        auto& io = ImGui::GetIO();
        io.Fonts->Clear();
        if (BuildHintedFontAtlas(*io.Fonts, std::round(18 * uiScale * density))) {
            io.Fonts->SetTexID(1);
            io.FontGlobalScale = 1 / density;
            fontDensity = density;
            ++fontRevision;
            return;
        }
        ImFontConfig config;
        config.SizePixels = std::round(18 * uiScale * density);
        config.OversampleH = config.OversampleV = 1;
        config.PixelSnapH = true;
        config.RasterizerMultiply = 1.10f;
        // Rasterize at framebuffer density, then keep layout/input in client coordinates.
        wchar_t windowsPath[MAX_PATH]{};
        if (GetWindowsDirectoryW(windowsPath, MAX_PATH)) {
            const std::wstring fontPath = std::wstring(windowsPath) + L"\\Fonts\\segoeui.ttf";
            if (GetFileAttributesW(fontPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
                const int size = WideCharToMultiByte(CP_UTF8, 0, fontPath.c_str(), -1, nullptr, 0, nullptr, nullptr);
                std::string utf8(size, '\0');
                WideCharToMultiByte(CP_UTF8, 0, fontPath.c_str(), -1, utf8.data(), size, nullptr, nullptr);
                static const ImWchar ranges[] = { 0x20, 0xFF, 0x400, 0x52F, 0x2000, 0x206F, 0 };
                io.Fonts->AddFontFromFileTTF(utf8.c_str(), config.SizePixels, &config, ranges);
            }
        }
        if (io.Fonts->Fonts.empty()) io.Fonts->AddFontDefault(&config);
        if (!io.Fonts->Build()) throw std::runtime_error("Font atlas build failed");
        io.Fonts->SetTexID(1);
        io.FontGlobalScale = 1 / density;
        fontDensity = density;
        ++fontRevision;
    }

    int CaptureInput() {
        if (releaseGuard && !(GetAsyncKeyState(VK_LBUTTON) & 0x8000) &&
            !(GetAsyncKeyState(VK_RBUTTON) & 0x8000) && !(GetAsyncKeyState(VK_MBUTTON) & 0x8000) &&
            !(GetAsyncKeyState(VK_XBUTTON1) & 0x8000) && !(GetAsyncKeyState(VK_XBUTTON2) & 0x8000)) releaseGuard = false;
        const auto& io = ImGui::GetIO();
        return ((releaseGuard || (visible && io.WantCaptureMouse)) ? CaptureMouse : 0) |
            ((visible && io.WantCaptureKeyboard) ? CaptureKeyboard : 0);
    }

    void SetVisible(bool value) {
        if (visible && !value) {
            const auto& io = ImGui::GetIO();
            for (int i = 0; i < 5; ++i)
                if (io.MouseDown[i] && io.MouseDownOwned[i]) releaseGuard = true;
        }
        visible = value;
    }

    bool ShouldCapture(UINT message) {
        const int capture = CaptureInput();
        if (message == WM_KEYDOWN || message == WM_KEYUP || message == WM_CHAR || message == WM_UNICHAR)
            return (capture & CaptureKeyboard) != 0;
        return (capture & CaptureMouse) != 0;
    }

    bool IsInputMessage(UINT message) {
        return message == WM_KEYDOWN || message == WM_KEYUP || message == WM_CHAR || message == WM_UNICHAR ||
            (message >= WM_LBUTTONDOWN && message <= WM_MBUTTONDBLCLK) ||
            message == WM_XBUTTONDOWN || message == WM_XBUTTONUP || message == WM_XBUTTONDBLCLK ||
            message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL;
    }

    bool HandleInsert(UINT message, WPARAM key, LPARAM flags) {
        if (key != VK_INSERT || (message != WM_KEYDOWN && message != WM_KEYUP)) return false;
        if (message == WM_KEYDOWN) {
            if (!insertDown && !(flags & (1LL << 30))) SetVisible(!visible);
            insertDown = true;
        }
        else insertDown = false;
        return true;
    }

    LRESULT CALLBACK MessageHook(int code, WPARAM removed, LPARAM parameter) {
        if (code >= 0 && removed == PM_REMOVE && context && !suspended) {
            auto* message = reinterpret_cast<MSG*>(parameter);
            if (message->hwnd == gameWindow || IsChild(gameWindow, message->hwnd)) {
                // XNA/WinForms can consume keys before WndProc. Observe them before preprocessing.
                ImGui::SetCurrentContext(context);
                if (HandleInsert(message->message, message->wParam, message->lParam)) message->message = WM_NULL;
                else if (IsInputMessage(message->message) && ShouldCapture(message->message)) {
                    // Preserve WM_CHAR generation before hiding the key from WinForms' preprocessing.
                    if (message->message == WM_KEYDOWN) TranslateMessage(message);
                    ImGui_ImplWin32_WndProcHandler(message->hwnd, message->message, message->wParam, message->lParam);
                    message->message = WM_NULL;
                }
            }
        }
        return CallNextHookEx(messageHook, code, removed, parameter);
    }

    LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
        // XNA's window, Draw, and Update run on the same thread. Never call managed code here.
        if (suspended) return CallWindowProcW(previousProc, window, message, wParam, lParam);
        ImGui::SetCurrentContext(context);
        if (HandleInsert(message, wParam, lParam)) return 0;
        const LRESULT handled = ImGui_ImplWin32_WndProcHandler(window, message, wParam, lParam);
        if (ShouldCapture(message)) {
            switch (message) {
            case WM_KEYDOWN: case WM_KEYUP: case WM_CHAR: case WM_UNICHAR:
            case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_LBUTTONDBLCLK:
            case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_RBUTTONDBLCLK:
            case WM_MBUTTONDOWN: case WM_MBUTTONUP: case WM_MBUTTONDBLCLK:
            case WM_XBUTTONDOWN: case WM_XBUTTONUP: case WM_XBUTTONDBLCLK:
            case WM_MOUSEWHEEL: case WM_MOUSEHWHEEL:
                return 0;
            case WM_SETCURSOR:
                if (handled) return handled;
            }
        }
        return CallWindowProcW(previousProc, window, message, wParam, lParam);
    }

    void DrawConsole() {
        if (ImGui::Button("Clear")) {
            std::lock_guard lock(logGate);
            lines.clear();
            ++logRevision;
        }
        ImGui::SameLine();
        const bool copy = ImGui::Button("Copy");
        ImGui::SameLine();
        ImGui::Checkbox("Auto-scroll", &autoScroll);
        filter.Draw("Filter", -70 * uiScale);
        ImGui::Separator();

        // Release the producer lock before drawing or touching the clipboard.
        {
            std::lock_guard lock(logGate);
            if (snapshotRevision != logRevision) {
                snapshot.assign(lines.begin(), lines.end());
                snapshotRevision = logRevision;
            }
        }
        std::vector<const char*> filtered;
        for (const auto& line : snapshot)
            if (filter.PassFilter(line.c_str())) filtered.push_back(line.c_str());
        if (copy) {
            std::string text;
            for (const char* line : filtered) { text += line; text += '\n'; }
            ImGui::SetClipboardText(text.c_str());
        }
        if (ImGui::BeginChild("Log", ImVec2(0, 0), ImGuiChildFlags_None,
            ImGuiWindowFlags_HorizontalScrollbar)) {
            const bool atBottom = ImGui::GetScrollY() >= ImGui::GetScrollMaxY() - 1;
            ImGuiListClipper clipper;
            clipper.Begin(static_cast<int>(filtered.size()));
            while (clipper.Step())
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; ++i)
                    ImGui::TextUnformatted(filtered[i]);
            if (autoScroll && atBottom) ImGui::SetScrollHereY(1.0f);
        }
        ImGui::EndChild();
    }

    void ApplyHalloweenTheme() {
        ImGui::StyleColorsDark();
        auto& style = ImGui::GetStyle();
        style.WindowPadding = ImVec2(18, 14);
        style.FramePadding = ImVec2(12, 7);
        style.ItemSpacing = ImVec2(12, 10);
        style.ItemInnerSpacing = ImVec2(8, 6);
        style.WindowRounding = 12;
        style.ChildRounding = 8;
        style.FrameRounding = 6;
        style.PopupRounding = 6;
        style.ScrollbarRounding = 8;
        style.GrabRounding = 4;
        style.TabRounding = 6;
        style.WindowBorderSize = style.ChildBorderSize = 1;
        style.FrameBorderSize = 0;
        auto* c = style.Colors;
        c[ImGuiCol_Text] = ImVec4(.94f, .91f, .86f, 1);
        c[ImGuiCol_TextDisabled] = ImVec4(.59f, .56f, .52f, 1);
        c[ImGuiCol_WindowBg] = ImVec4(.045f, .041f, .038f, .98f);
        c[ImGuiCol_ChildBg] = ImVec4(.075f, .066f, .058f, 1);
        c[ImGuiCol_PopupBg] = ImVec4(.09f, .075f, .06f, 1);
        c[ImGuiCol_Border] = ImVec4(.24f, .17f, .11f, 1);
        c[ImGuiCol_BorderShadow] = ImVec4(0, 0, 0, 0);
        c[ImGuiCol_FrameBg] = ImVec4(.12f, .10f, .08f, 1);
        c[ImGuiCol_FrameBgHovered] = ImVec4(.23f, .14f, .08f, 1);
        c[ImGuiCol_FrameBgActive] = ImVec4(.32f, .18f, .08f, 1);
        c[ImGuiCol_TitleBg] = c[ImGuiCol_TitleBgActive] = ImVec4(.09f, .065f, .045f, 1);
        c[ImGuiCol_TitleBgCollapsed] = c[ImGuiCol_TitleBg];
        c[ImGuiCol_MenuBarBg] = c[ImGuiCol_ChildBg];
        c[ImGuiCol_ScrollbarBg] = ImVec4(.035f, .03f, .025f, 1);
        c[ImGuiCol_ScrollbarGrab] = ImVec4(.30f, .21f, .14f, 1);
        c[ImGuiCol_ScrollbarGrabHovered] = ImVec4(.50f, .28f, .12f, 1);
        c[ImGuiCol_ScrollbarGrabActive] = orange;
        c[ImGuiCol_CheckMark] = c[ImGuiCol_SliderGrab] = c[ImGuiCol_SliderGrabActive] = orange;
        c[ImGuiCol_Button] = ImVec4(.73f, .27f, .055f, 1);
        c[ImGuiCol_ButtonHovered] = ImVec4(.95f, .38f, .08f, 1);
        c[ImGuiCol_ButtonActive] = ImVec4(.60f, .21f, .035f, 1);
        c[ImGuiCol_Header] = ImVec4(.28f, .15f, .07f, 1);
        c[ImGuiCol_HeaderHovered] = ImVec4(.40f, .20f, .08f, 1);
        c[ImGuiCol_HeaderActive] = ImVec4(.53f, .25f, .08f, 1);
        c[ImGuiCol_Separator] = c[ImGuiCol_Border];
        c[ImGuiCol_SeparatorHovered] = c[ImGuiCol_SeparatorActive] = orange;
        c[ImGuiCol_ResizeGrip] = ImVec4(1, .43f, .12f, .20f);
        c[ImGuiCol_ResizeGripHovered] = ImVec4(1, .43f, .12f, .65f);
        c[ImGuiCol_ResizeGripActive] = orange;
        c[ImGuiCol_Tab] = c[ImGuiCol_ChildBg];
        c[ImGuiCol_TabHovered] = c[ImGuiCol_HeaderHovered];
        c[ImGuiCol_TabSelected] = ImVec4(.26f, .13f, .055f, 1);
        c[ImGuiCol_TabSelectedOverline] = orange;
        c[ImGuiCol_TabDimmed] = c[ImGuiCol_Tab];
        c[ImGuiCol_TabDimmedSelected] = c[ImGuiCol_TabSelected];
        c[ImGuiCol_TabDimmedSelectedOverline] = c[ImGuiCol_Border];
        c[ImGuiCol_TextSelectedBg] = ImVec4(1, .43f, .12f, .30f);
        c[ImGuiCol_NavCursor] = orange;
        c[ImGuiCol_PlotLines] = c[ImGuiCol_PlotHistogram] = orange;
        style.ScaleAllSizes(uiScale);
    }

    bool NavigationButton(const char* label, bool active) {
        ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 15 * uiScale);
        ImGui::PushStyleVar(ImGuiStyleVar_ButtonTextAlign, ImVec2(.5f, .5f));
        ImGui::PushStyleColor(ImGuiCol_Button, active ? ImVec4(.32f, .15f, .065f, 1) : ImVec4(.10f, .085f, .07f, 1));
        ImGui::PushStyleColor(ImGuiCol_ButtonHovered, ImVec4(.43f, .20f, .07f, 1));
        ImGui::SetCursorPosX((ImGui::GetWindowWidth() - 112 * uiScale) * .5f);
        const bool pressed = ImGui::Button(label, ImVec2(112 * uiScale, 30 * uiScale));
        ImGui::PopStyleColor(2);
        ImGui::PopStyleVar(2);
        return pressed;
    }

    void DrawFishing() {
        const float s = uiScale;
        ImGui::Spacing();
        if (ImGui::BeginChild("FishingStatus", ImVec2(0, 86*s), ImGuiChildFlags_Borders)) {
            ImGui::TextColored(orange, fishing.Enabled == 2 ? "AUTO FISHING  /  WAITING FOR YOUR CAST" :
                fishing.Enabled ? "AUTO FISHING  /  ON" : "AUTO FISHING  /  OFF");
            ImGui::TextWrapped("%s", fishingStatus.c_str());
        }
        ImGui::EndChild();
        ImGui::BeginDisabled(!fishing.Enabled && !fishing.CanStart);
        if (ImGui::Button(fishing.Enabled == 2 ? "Cancel start" : fishing.Enabled ? "Stop fishing" : "Start fishing", ImVec2(160*s, 0)))
            pendingActions |= fishing.Enabled ? 2 : 1;
        ImGui::EndDisabled();
        ImGui::SameLine();
        ImGui::TextDisabled("F6  Instant toggle at cursor");
        ImGui::Spacing();
        if (ImGui::BeginTable("FishingDetails", 2, ImGuiTableFlags_SizingStretchProp)) {
            ImGui::TableSetupColumn("Label", ImGuiTableColumnFlags_WidthFixed, 150*s);
            const auto row = [](const char* label) {
                ImGui::TableNextRow(); ImGui::TableNextColumn();
                ImGui::TextDisabled("%s", label); ImGui::TableNextColumn();
            };
            row("Equipped rod"); ImGui::TextWrapped("%s", fishingRod.c_str());
            row("Bait power"); ImGui::Text("%d%%", fishing.BaitPower);
            row("Active bobbers"); ImGui::Text("%d", fishing.Bobbers);
            row("Retrievals / session"); ImGui::Text("%d", fishing.Retrievals);
            row("Failed casts"); ImGui::Text("%d / 3", fishing.FailedCasts);
            row("Saved world aim");
            if (fishing.HasAim) ImGui::Text("%.0f, %.0f", fishing.AimX, fishing.AimY);
            else ImGui::TextDisabled("Set by your first cast");
            ImGui::EndTable();
        }
        ImGui::Spacing();
        ImGui::Separator();
        ImGui::TextWrapped("Click Start, then cast once into the water. Auto fishing saves that aim and takes over. You can leave this menu open.");
    }

    void DrawMenu() {
        ImGui::SetNextWindowPos(ImVec2(48 * uiScale, 48 * uiScale), ImGuiCond_FirstUseEver);
        ImGui::SetNextWindowSize(ImVec2(940 * uiScale, 500 * uiScale), ImGuiCond_FirstUseEver);
        ImGui::SetNextWindowSizeConstraints(ImVec2(760 * uiScale, 420 * uiScale), ImVec2(FLT_MAX, FLT_MAX));
        bool open = visible;
        ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(8 * uiScale, 3 * uiScale));
        const bool expanded = ImGui::Begin("Spooky Fisher", &open, ImGuiWindowFlags_NoCollapse);
        ImGui::PopStyleVar();
        if (expanded) {
            const float footer = 40 * uiScale;
            ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(0, 0, 0, 0));
            const bool navigation = ImGui::BeginChild("Navigation", ImVec2(132 * uiScale, -footer));
            ImGui::PopStyleColor();
            if (navigation) {
                ImGui::Dummy(ImVec2(0, 2 * uiScale));
                if (NavigationButton("Fishing", selectedPage == 0)) selectedPage = 0;
                if (NavigationButton("Console", selectedPage == 1)) selectedPage = 1;
                ImGui::SetCursorPosY(std::max(ImGui::GetCursorPosY(), ImGui::GetWindowHeight() - 36 * uiScale));
                if (NavigationButton("Detach", false)) pendingActions |= 4;
            }
            ImGui::EndChild();
            ImGui::SameLine();
            if (ImGui::BeginChild("PageContent", ImVec2(0, -footer))) {
                if (selectedPage == 0) DrawFishing();
                else DrawConsole();
            }
            ImGui::EndChild();
            ImGui::Separator();
            ImGui::TextDisabled("Insert  Menu    F6  Fishing    F7  Detach     |     Mouse passes through outside the panel");
        }
        ImGui::End();
        SetVisible(open);
    }
}

void AppendOverlayLog(const wchar_t* source, const wchar_t* message) noexcept {
    try {
        if (!message) return;
        SYSTEMTIME now;
        GetLocalTime(&now);
        wchar_t prefix[96];
        swprintf_s(prefix, L"[%02u:%02u:%02u] [%ls] ", now.wHour, now.wMinute, now.wSecond, source);
        // Bound individual writes as well as retained lines.
        std::wstring text = std::wstring(prefix) + std::wstring(message, wcsnlen_s(message, 4096));
        const int length = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        std::string utf8(length, '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), utf8.data(), length, nullptr, nullptr);
        std::lock_guard lock(logGate);
        ++logRevision;
        // Store physical lines so clipping and filtering have predictable row heights.
        size_t start = 0;
        while (start < utf8.size()) {
            const size_t end = utf8.find_first_of("\r\n", start);
            if (end != start) lines.emplace_back(utf8.substr(start, end - start));
            if (lines.size() > MaxLines) lines.pop_front();
            if (end == std::string::npos) break;
            start = end + 1;
        }
    }
    catch (...) { /* Logging must never interrupt the game or a loader failure. */ }
}

extern "C" void __cdecl SF_Log(const wchar_t* message) {
    AppendOverlayLog(L"Managed", message);
}

extern "C" int __cdecl SF_UiInitialize(HWND window) {
    if (context) return window == gameWindow;
    if (!IsWindow(window) || GetWindowThreadProcessId(window, nullptr) != GetCurrentThreadId()) return 0;
    try {
        IMGUI_CHECKVERSION();
        context = ImGui::CreateContext();
        auto& io = ImGui::GetIO();
        io.IniFilename = nullptr;
        io.LogFilename = nullptr;
        io.BackendRendererName = "SpookyFisher_XNA";
        io.BackendFlags |= ImGuiBackendFlags_RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        uiScale = std::clamp(ImGui_ImplWin32_GetDpiScaleForHwnd(window), 1.0f, 4.0f);
        BuildFont(1);
        ApplyHalloweenTheme();
        if (!ImGui_ImplWin32_Init(window)) throw std::runtime_error("Win32 backend initialization failed");
        SetLastError(0);
        previousProc = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(window, GWLP_WNDPROC,
            reinterpret_cast<LONG_PTR>(&WindowProc)));
        if (!previousProc) throw std::runtime_error("Window procedure installation failed");
        gameWindow = window;
        messageHook = SetWindowsHookExW(WH_GETMESSAGE, MessageHook, nullptr, GetCurrentThreadId());
        if (!messageHook) throw std::runtime_error("Message hook installation failed");
        suspended = false;
        return 1;
    }
    catch (...) {
        if (previousProc && gameWindow) SetWindowLongPtrW(gameWindow, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(previousProc));
        previousProc = nullptr;
        gameWindow = nullptr;
        if (context) {
            if (ImGui::GetIO().BackendPlatformUserData) ImGui_ImplWin32_Shutdown();
            ImGui::DestroyContext(context);
            context = nullptr;
        }
        AppendOverlayLog(L"UI", L"Menu initialization failed.");
        return 0;
    }
}

extern "C" int __cdecl SF_UiShutdown() {
    if (!context) return 1;
    // A deferred detach must leave Terraria interactive and stop accumulating input events.
    visible = releaseGuard = false;
    suspended = true;
    if (messageHook) {
        if (!UnhookWindowsHookEx(messageHook)) return 0;
        messageHook = nullptr;
    }
    if (IsWindow(gameWindow)) {
        if (GetWindowThreadProcessId(gameWindow, nullptr) != GetCurrentThreadId()) return 0;
        // Another hook may chain through ours. Keep the DLL resident until it can be safely removed.
        if (reinterpret_cast<WNDPROC>(GetWindowLongPtrW(gameWindow, GWLP_WNDPROC)) != WindowProc) return 0;
        SetLastError(0);
        if (!SetWindowLongPtrW(gameWindow, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(previousProc)) && GetLastError()) return 0;
    }
    ImGui::SetCurrentContext(context);
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext(context);
    context = nullptr;
    gameWindow = nullptr;
    previousProc = nullptr;
    visible = releaseGuard = false;
    insertDown = false;
    pendingActions = 0;
    selectedPage = 0;
    fishing = {};
    fishingStatus = "Enter a world to begin.";
    fishingRod = "No rod equipped";
    filter.Clear();
    fontDensity = 0;
    reportDisplay = true;
    vertices.clear(); indices.clear(); commands.clear();
    return 1;
}

extern "C" int __cdecl SF_UiCaptureInput() {
    if (!context || suspended) return 0;
    ImGui::SetCurrentContext(context);
    return CaptureInput();
}

extern "C" int __cdecl SF_UiVisible() { return context && !suspended && visible; }

extern "C" int __cdecl SF_UiTakeActions() {
    const int result = pendingActions;
    pendingActions = 0;
    return result;
}

extern "C" void __cdecl SF_UiSetFishing(const OverlayFishingState* state, const wchar_t* status, const wchar_t* rod) {
    if (!state) return;
    fishing = *state;
    const auto utf8 = [](const wchar_t* value) {
        if (!value) return std::string();
        const int length = static_cast<int>(wcsnlen_s(value, 2048));
        const int size = WideCharToMultiByte(CP_UTF8, 0, value, length, nullptr, 0, nullptr, nullptr);
        std::string result(size, '\0');
        WideCharToMultiByte(CP_UTF8, 0, value, length, result.data(), size, nullptr, nullptr);
        return result;
    };
    fishingStatus = utf8(status);
    fishingRod = utf8(rod);
}

extern "C" int __cdecl SF_UiConfigureDisplay(int width, int height) {
    if (!context || width <= 0 || height <= 0) return 0;
    try {
        RECT client{};
        if (!GetClientRect(gameWindow, &client)) return 0;
        if (client.right <= 0 || client.bottom <= 0) return fontRevision;
        const float ratio = std::max(static_cast<float>(width) / client.right, static_cast<float>(height) / client.bottom);
        // Quantize to avoid rebuilding the atlas for insignificant resize rounding differences.
        const float density = std::clamp(std::ceil(ratio * 4) / 4, 1.0f, 4.0f);
        ImGui::SetCurrentContext(context);
        if (std::abs(density - fontDensity) > 0.01f) {
            BuildFont(density);
            reportDisplay = true;
        }
        if (reportDisplay) {
            wchar_t message[160];
            swprintf_s(message, L"Backbuffer %dx%d; client %ldx%ld; UI scale %.2fx; font density %.2fx.", width, height, client.right, client.bottom, uiScale, density);
            AppendOverlayLog(L"Display", message);
            reportDisplay = false;
        }
        return fontRevision;
    }
    catch (...) {
        AppendOverlayLog(L"UI", L"Could not update font pixel density.");
        return 0;
    }
}

extern "C" int __cdecl SF_UiFont(const void** pixels, int* width, int* height) {
    if (!context || !pixels || !width || !height) return 0;
    ImGui::SetCurrentContext(context);
    unsigned char* data;
    ImGui::GetIO().Fonts->GetTexDataAsRGBA32(&data, width, height);
    ImGui::GetIO().Fonts->SetTexID(1);
    *pixels = data;
    return 1;
}

extern "C" int __cdecl SF_UiFrame(int width, int height, OverlayFrame* frame) {
    if (!context || !frame || width <= 0 || height <= 0) return 0;
    *frame = {};
    if (suspended) return 1;
    try {
        ImGui::SetCurrentContext(context);
        DWORD foregroundProcess = 0;
        GetWindowThreadProcessId(GetForegroundWindow(), &foregroundProcess);
        const bool down = foregroundProcess == GetCurrentProcessId() && (GetAsyncKeyState(VK_INSERT) & 0x8000);
        if (down && !insertDown) SetVisible(!visible);
        insertDown = down;
        ImGui_ImplWin32_NewFrame();
        auto& io = ImGui::GetIO();
        if (io.DisplaySize.x <= 0 || io.DisplaySize.y <= 0) return 1;
        io.DisplayFramebufferScale = ImVec2(width / io.DisplaySize.x, height / io.DisplaySize.y);
        ImGui::NewFrame();
        io.MouseDrawCursor = visible && io.WantCaptureMouse;
        if (visible) DrawMenu();
        ImGui::Render();
        const auto* draw = ImGui::GetDrawData();
        vertices.clear(); indices.clear(); commands.clear();
        vertices.reserve(draw->TotalVtxCount);
        indices.reserve(draw->TotalIdxCount);
        for (const auto* list : draw->CmdLists) {
            const int vertexOffset = static_cast<int>(vertices.size());
            const int indexOffset = static_cast<int>(indices.size());
            vertices.insert(vertices.end(), list->VtxBuffer.begin(), list->VtxBuffer.end());
            indices.insert(indices.end(), list->IdxBuffer.begin(), list->IdxBuffer.end());
            for (const auto& command : list->CmdBuffer) {
                // The menu uses only ImGui widgets; no foreign renderer callbacks are accepted.
                if (command.UserCallback) continue;
                commands.push_back({ command.ClipRect.x, command.ClipRect.y, command.ClipRect.z, command.ClipRect.w,
                    static_cast<int>(command.ElemCount), indexOffset + static_cast<int>(command.IdxOffset),
                    vertexOffset + static_cast<int>(command.VtxOffset),
                    std::min(65536, list->VtxBuffer.Size - static_cast<int>(command.VtxOffset)), command.GetTexID() });
            }
        }
        *frame = { static_cast<int>(vertices.size()), static_cast<int>(indices.size()), static_cast<int>(commands.size()), 0,
            vertices.data(), indices.data(), commands.data(), draw->DisplaySize.x, draw->DisplaySize.y };
        return 1;
    }
    catch (...) {
        AppendOverlayLog(L"UI", L"Menu frame failed.");
        return 0;
    }
}
