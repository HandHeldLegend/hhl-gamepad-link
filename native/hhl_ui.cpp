// HHL Gamepad Link's window: a Win32 window drawn with Dear ImGui on Direct3D 11.
//
// The app (C#) owns the logic and calls the few functions below once per frame; everything about
// how the window looks lives here. Strings are UTF-8.

#include <windows.h>
#include <string>
#include <vector>
#include <shellapi.h>
#include <dwmapi.h>
#include <d3d11.h>

#include "imgui.h"
#include "imgui_internal.h" // RenderTextEllipsis
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

// ---- Look ----

static const ImVec4 kBackground = ImVec4(0.075f, 0.082f, 0.098f, 1.0f);
static const ImU32 kCard        = IM_COL32(29, 32, 38, 255);
static const ImU32 kCardBorder  = IM_COL32(44, 48, 56, 255);
static const ImU32 kAccent      = IM_COL32(64, 196, 214, 255);
static const ImU32 kText        = IM_COL32(232, 234, 238, 255);
static const ImU32 kTextDim     = IM_COL32(140, 146, 158, 255);
static const ImU32 kStatus[]    = {
    IM_COL32(88, 204, 120, 255),  // 0 open here
    IM_COL32(236, 186, 72, 255),  // 1 in use elsewhere
    IM_COL32(120, 126, 138, 255), // 2 free
    IM_COL32(232, 96, 96, 255),   // 3 problem
};

// ---- Window and device ----

static HWND g_hwnd = nullptr;
static ID3D11Device *g_device = nullptr;
static ID3D11DeviceContext *g_context = nullptr;
static IDXGISwapChain *g_swapchain = nullptr;
static ID3D11RenderTargetView *g_target = nullptr;
static UINT g_resize_w = 0, g_resize_h = 0;
static bool g_open = true;
static float g_scale = 1.0f;
static ImFont *g_font = nullptr;
static ImFont *g_font_bold = nullptr;

// Hidden to the notification area: an icon there brings the window back
static const UINT kTrayMessage = WM_APP + 1;
static const UINT kMenuShow = 1, kMenuQuit = 2;

// The app's own tray menu items (ui_tray_items), between Show and Quit, and the one picked
struct TrayItem
{
    std::wstring label;
    UINT id;
    int flags; // 1 checked, 2 separator, 4 starts a submenu (this item is its title), 8 ends one
};
static std::vector<TrayItem> g_tray_items;
static int g_tray_choice = 0;
static NOTIFYICONDATAW g_tray = {};
static bool g_hidden = false;
static bool g_close_to_tray = false; // The footer's "Close to tray": closing the window hides it

static void show_window()
{
    if (g_hidden)
        Shell_NotifyIconW(NIM_DELETE, &g_tray);
    g_hidden = false;
    ShowWindow(g_hwnd, SW_SHOW);
    ShowWindow(g_hwnd, SW_RESTORE);
    SetForegroundWindow(g_hwnd);
}

// The app icon (src/app.ico) at a given size: the first icon group in the .exe, whatever its ID
static BOOL CALLBACK first_icon_group(HMODULE, LPCWSTR, LPWSTR name, LONG_PTR found)
{
    *(LPWSTR *)found = name;
    return FALSE;
}

static HICON app_icon(int size)
{
    LPWSTR name = nullptr;
    EnumResourceNamesW(nullptr, RT_GROUP_ICON, first_icon_group, (LONG_PTR)&name);
    HICON icon = name ? (HICON)LoadImageW(GetModuleHandleW(nullptr), name, IMAGE_ICON, size, size, 0) : nullptr;
    return icon ? icon : LoadIconW(nullptr, IDI_APPLICATION);
}

static void hide_to_tray()
{
    if (g_hidden)
        return;
    g_tray.cbSize = sizeof(g_tray);
    g_tray.hWnd = g_hwnd;
    g_tray.uID = 1;
    g_tray.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    g_tray.uCallbackMessage = kTrayMessage;
    g_tray.hIcon = app_icon(GetSystemMetrics(SM_CXSMICON));
    lstrcpynW(g_tray.szTip, L"HHL Gamepad Link", ARRAYSIZE(g_tray.szTip));
    Shell_NotifyIconW(NIM_ADD, &g_tray);
    ShowWindow(g_hwnd, SW_HIDE);
    g_hidden = true;
}

static void tray_menu()
{
    HMENU menu = CreatePopupMenu();
    AppendMenuW(menu, MF_STRING, kMenuShow, L"Show HHL Gamepad Link");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);

    // The app's items, with their submenus
    std::vector<HMENU> open = {menu};
    for (const TrayItem &item : g_tray_items)
    {
        if (item.flags & 2)
            AppendMenuW(open.back(), MF_SEPARATOR, 0, nullptr);
        else if (item.flags & 4)
        {
            HMENU sub = CreatePopupMenu();
            AppendMenuW(open.back(), MF_POPUP, (UINT_PTR)sub, item.label.c_str());
            open.push_back(sub);
        }
        else if (item.flags & 8)
        {
            if (open.size() > 1)
                open.pop_back();
        }
        else
            AppendMenuW(open.back(), MF_STRING | ((item.flags & 1) ? MF_CHECKED : 0), item.id, item.label.c_str());
    }
    if (!g_tray_items.empty())
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, kMenuQuit, L"Quit");
    POINT at;
    GetCursorPos(&at);
    SetForegroundWindow(g_hwnd); // So the menu closes when clicking elsewhere
    UINT chosen = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, at.x, at.y, 0, g_hwnd, nullptr);
    DestroyMenu(menu);
    if (chosen == kMenuShow)
        show_window();
    else if (chosen == kMenuQuit)
        DestroyWindow(g_hwnd);
    else if (chosen != 0)
        g_tray_choice = (int)chosen;
}

static void create_target()
{
    ID3D11Texture2D *back = nullptr;
    g_swapchain->GetBuffer(0, IID_PPV_ARGS(&back));
    g_device->CreateRenderTargetView(back, nullptr, &g_target);
    back->Release();
}

static void release_target()
{
    if (g_target) { g_target->Release(); g_target = nullptr; }
}

static bool create_device(HWND hwnd)
{
    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 2;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hwnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
    D3D_FEATURE_LEVEL got;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, levels, 2,
                                               D3D11_SDK_VERSION, &sd, &g_swapchain, &g_device, &got, &g_context);
    if (hr == DXGI_ERROR_UNSUPPORTED) // No GPU driver: draw on the CPU instead
        hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, levels, 2,
                                           D3D11_SDK_VERSION, &sd, &g_swapchain, &g_device, &got, &g_context);
    if (FAILED(hr))
        return false;
    create_target();
    return true;
}

static LRESULT WINAPI wnd_proc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam)
{
    if (ImGui_ImplWin32_WndProcHandler(hwnd, msg, wparam, lparam))
        return true;

    switch (msg)
    {
    case WM_SIZE:
        if (wparam != SIZE_MINIMIZED)
        {
            g_resize_w = LOWORD(lparam);
            g_resize_h = HIWORD(lparam);
        }
        return 0;
    case WM_SYSCOMMAND:
        if ((wparam & 0xfff0) == SC_KEYMENU) // No Alt menu
            return 0;
        break;
    case kTrayMessage:
        if (LOWORD(lparam) == WM_LBUTTONUP || LOWORD(lparam) == WM_LBUTTONDBLCLK)
            show_window();
        else if (LOWORD(lparam) == WM_RBUTTONUP)
            tray_menu();
        return 0;
    case WM_CLOSE:
        if (g_close_to_tray)
        {
            hide_to_tray();
            return 0;
        }
        break;
    case WM_DESTROY:
        if (g_hidden)
            Shell_NotifyIconW(NIM_DELETE, &g_tray);
        g_open = false;
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wparam, lparam);
}

static void apply_style()
{
    ImGuiStyle &s = ImGui::GetStyle();
    s.WindowPadding = ImVec2(18, 16);
    s.FramePadding = ImVec2(14, 7);
    s.ItemSpacing = ImVec2(10, 10);
    s.FrameRounding = 7;
    s.WindowRounding = 0;
    s.PopupRounding = 10;
    s.WindowBorderSize = 0;
    s.FrameBorderSize = 0;

    ImVec4 *c = s.Colors;
    c[ImGuiCol_WindowBg] = kBackground;
    c[ImGuiCol_PopupBg] = ImVec4(0.12f, 0.13f, 0.15f, 1.0f);
    c[ImGuiCol_Text] = ImGui::ColorConvertU32ToFloat4(kText);
    c[ImGuiCol_TextDisabled] = ImGui::ColorConvertU32ToFloat4(kTextDim);
    c[ImGuiCol_Button] = ImVec4(0.17f, 0.19f, 0.22f, 1.0f);
    c[ImGuiCol_ButtonHovered] = ImVec4(0.22f, 0.25f, 0.29f, 1.0f);
    c[ImGuiCol_ButtonActive] = ImVec4(0.26f, 0.30f, 0.35f, 1.0f);
    c[ImGuiCol_ModalWindowDimBg] = ImVec4(0, 0, 0, 0.55f);
    c[ImGuiCol_Border] = ImGui::ColorConvertU32ToFloat4(kCardBorder);

    s.ScaleAllSizes(g_scale);
}

static void load_fonts()
{
    ImGuiIO &io = ImGui::GetIO();
    io.IniFilename = nullptr; // Nothing written next to the app
    io.LogFilename = nullptr;

    // Windows' own UI font; ImGui's built-in one if it is missing
    const float size = 17.0f * g_scale;
    g_font = io.Fonts->AddFontFromFileTTF("C:\\Windows\\Fonts\\segoeui.ttf", size);
    g_font_bold = io.Fonts->AddFontFromFileTTF("C:\\Windows\\Fonts\\seguisb.ttf", size * 1.15f);
    if (!g_font)
        g_font = io.Fonts->AddFontDefault();
    if (!g_font_bold)
        g_font_bold = g_font;
}

// ---- Exported to the app ----

extern "C" int ui_init(const wchar_t *title, int width, int height, int start_hidden)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    g_scale = GetDpiForSystem() / 96.0f;

    WNDCLASSEXW wc = { sizeof(wc), CS_CLASSDC, wnd_proc, 0, 0, GetModuleHandleW(nullptr), nullptr,
                       LoadCursorW(nullptr, IDC_ARROW), nullptr, nullptr, L"HhlGamepadLink", nullptr };
    wc.hIcon = app_icon(GetSystemMetrics(SM_CXICON));
    wc.hIconSm = app_icon(GetSystemMetrics(SM_CXSMICON));
    RegisterClassExW(&wc);
    g_hwnd = CreateWindowExW(0, wc.lpszClassName, title, WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
                             (int)(width * g_scale), (int)(height * g_scale), nullptr, nullptr, wc.hInstance, nullptr);
    if (!g_hwnd || !create_device(g_hwnd))
        return 0;

    BOOL dark = TRUE; // DWMWA_USE_IMMERSIVE_DARK_MODE: a title bar that matches
    DwmSetWindowAttribute(g_hwnd, 20, &dark, sizeof(dark));

    if (start_hidden)
        hide_to_tray();
    else
    {
        ShowWindow(g_hwnd, SW_SHOWDEFAULT);
        UpdateWindow(g_hwnd);
    }

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    load_fonts();
    apply_style();
    ImGui_ImplWin32_Init(g_hwnd);
    ImGui_ImplDX11_Init(g_device, g_context);
    return 1;
}

// Start a frame: 1 to draw, 2 while hidden in the tray (nothing to draw), 0 once closed
extern "C" int ui_begin()
{
    if (g_hidden)
        MsgWaitForMultipleObjects(0, nullptr, FALSE, 100, QS_ALLINPUT); // Idle: wake for messages

    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    if (!g_open)
        return 0;
    if (g_hidden)
        return 2;

    if (g_resize_w != 0 && g_resize_h != 0)
    {
        release_target();
        g_swapchain->ResizeBuffers(0, g_resize_w, g_resize_h, DXGI_FORMAT_UNKNOWN, 0);
        g_resize_w = g_resize_h = 0;
        create_target();
    }

    ImGui_ImplDX11_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();

    const ImGuiViewport *vp = ImGui::GetMainViewport();
    ImGui::SetNextWindowPos(vp->WorkPos);
    ImGui::SetNextWindowSize(vp->WorkSize);
    ImGui::Begin("##main", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove |
                                    ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoBringToFrontOnFocus);
    return 1;
}

// A row under the header: whether the app is looking for gamepads nearby, and with `can_toggle`
// a button to start or stop it. Returns 1 when the button was clicked.
extern "C" int ui_scan_bar(int scanning, int can_toggle, const char *note)
{
    int clicked = 0;
    if (can_toggle)
    {
        // Scanning shows as the lit state, like a selected choice in the header
        ImGui::PushStyleColor(ImGuiCol_Button, scanning ? kAccent : IM_COL32(36, 40, 47, 255));
        ImGui::PushStyleColor(ImGuiCol_ButtonHovered, scanning ? kAccent : IM_COL32(48, 53, 62, 255));
        ImGui::PushStyleColor(ImGuiCol_Text, scanning ? IM_COL32(10, 24, 28, 255) : kText);
        if (ImGui::Button(scanning ? "Stop scanning" : "Scan"))
            clicked = 1;
        ImGui::PopStyleColor(3);
        ImGui::SameLine(0, 12 * g_scale);
    }
    ImGui::PushStyleColor(ImGuiCol_Text, kTextDim);
    ImGui::AlignTextToFramePadding();
    ImGui::TextUnformatted(note);
    ImGui::PopStyleColor();
    ImGui::Dummy(ImVec2(0, 4 * g_scale));
    return clicked;
}

// The window is in front and active: the user is looking at it
extern "C" int ui_focused()
{
    return g_hwnd && GetForegroundWindow() == g_hwnd && !IsIconic(g_hwnd) ? 1 : 0;
}

extern "C" void ui_end()
{
    ImGui::End();
    ImGui::Render();
    const float clear[4] = { kBackground.x, kBackground.y, kBackground.z, 1.0f };
    g_context->OMSetRenderTargets(1, &g_target, nullptr);
    g_context->ClearRenderTargetView(g_target, clear);
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
    g_swapchain->Present(1, 0); // In step with the display: no busy loop
}

// The app's tray menu items: labels (UTF-8), the ID each returns and its flags (see TrayItem)
extern "C" void ui_tray_items(const char **labels, const int *ids, const int *flags, int count)
{
    g_tray_items.clear();
    for (int i = 0; i < count; i++)
    {
        int n = MultiByteToWideChar(CP_UTF8, 0, labels[i], -1, nullptr, 0);
        std::wstring label(n > 0 ? n - 1 : 0, L'\0');
        if (n > 1)
            MultiByteToWideChar(CP_UTF8, 0, labels[i], -1, label.data(), n);
        g_tray_items.push_back({label, (UINT)ids[i], flags[i]});
    }
}

// The ID of the tray item picked since the last call, or 0
extern "C" int ui_tray_take_choice()
{
    int chosen = g_tray_choice;
    g_tray_choice = 0;
    return chosen;
}

extern "C" void ui_shutdown()
{
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    release_target();
    if (g_swapchain) g_swapchain->Release();
    if (g_context) g_context->Release();
    if (g_device) g_device->Release();
    if (g_hwnd) DestroyWindow(g_hwnd);
}

// Title on the left, a labelled segmented control of choices on the right. Returns the chosen
// index, which differs from `selected` on the frame it was clicked.
extern "C" int ui_header(const char *title, const char *subtitle,
                                               const char *const *choices, int count, int selected)
{
    ImGui::PushFont(g_font_bold, g_font_bold->LegacySize);
    ImGui::AlignTextToFramePadding();
    ImGui::TextUnformatted(title);
    ImGui::PopFont();

    // The segmented control on the right, its label just before it
    const ImGuiStyle &st = ImGui::GetStyle();
    float width = 0;
    for (int i = 0; i < count; i++)
        width += ImGui::CalcTextSize(choices[i]).x + st.FramePadding.x * 2;
    const float right = ImGui::GetWindowContentRegionMax().x;
    const float label_gap = 12 * g_scale;

    ImGui::SameLine(right - width - label_gap - ImGui::CalcTextSize(subtitle).x);
    ImGui::PushStyleColor(ImGuiCol_Text, kTextDim);
    ImGui::AlignTextToFramePadding();
    ImGui::TextUnformatted(subtitle);
    ImGui::PopStyleColor();
    ImGui::SameLine(right - width);

    int chosen = selected;
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(0, 0));
    for (int i = 0; i < count; i++)
    {
        bool on = i == selected;
        ImGui::PushStyleColor(ImGuiCol_Button, on ? kAccent : IM_COL32(36, 40, 47, 255));
        ImGui::PushStyleColor(ImGuiCol_ButtonHovered, on ? kAccent : IM_COL32(48, 53, 62, 255));
        ImGui::PushStyleColor(ImGuiCol_Text, on ? IM_COL32(10, 24, 28, 255) : kText);
        ImGui::PushID(i);
        if (ImGui::Button(choices[i]))
            chosen = i;
        ImGui::PopID();
        ImGui::PopStyleColor(3);
        if (i + 1 < count)
            ImGui::SameLine();
    }
    ImGui::PopStyleVar();
    ImGui::Dummy(ImVec2(0, 4 * g_scale));
    return chosen;
}

// One gamepad: name, status and its button on the top row, the details across the card below
// (cut short with "…" if they don't fit). Returns 1 on the frame the button was clicked.
// button_kind: 0 plain, 1 accent.
// A gamepad's card. With mode_count > 0 a mode picker sits left of the button, showing
// modes[mode_selected]. Returns 1 when the button was clicked, plus (picked mode + 1) << 8 when
// a mode was picked.
extern "C" int ui_pad_card(const char *id, const char *name, const char *detail,
                           const char *status, int status_color, int signal_bars,
                           const char *button, int button_kind,
                           const char **modes, int mode_count, int mode_selected)
{
    ImGui::PushID(id);
    const ImGuiStyle &st = ImGui::GetStyle();
    const float pad = 16 * g_scale;
    const float line = ImGui::GetTextLineHeight();
    const ImVec2 start = ImGui::GetCursorScreenPos();
    const float width = ImGui::GetContentRegionAvail().x;
    ImDrawList *dl = ImGui::GetWindowDrawList();

    // Top row: as tall as the button
    const ImVec2 bsize(ImGui::CalcTextSize(button).x + st.FramePadding.x * 2 + 8 * g_scale, line + st.FramePadding.y * 2);
    const float row_y = start.y + pad;
    const float detail_y = row_y + bsize.y + 6 * g_scale;
    const float height = detail_y + line + pad - start.y;

    dl->AddRectFilled(start, ImVec2(start.x + width, start.y + height), kCard, 12 * g_scale);
    dl->AddRect(start, ImVec2(start.x + width, start.y + height), kCardBorder, 12 * g_scale);

    // Button on the right, the mode picker left of it, and the status left of those: at most 40%
    // of the card, cut short past that
    const float bx = start.x + width - pad - bsize.x;
    float mode_w = 0;
    for (int i = 0; i < mode_count; i++)
        mode_w = ImMax(mode_w, ImGui::CalcTextSize(modes[i]).x);
    if (mode_count > 0)
        mode_w += st.FramePadding.x * 2 + ImGui::GetFrameHeight();
    const float mx = bx - (mode_count > 0 ? mode_w + 8 * g_scale : 0);
    const float status_full = ImGui::CalcTextSize(status).x;
    const float status_w = ImMin(status_full, width * 0.4f);
    const float dot = 4.5f * g_scale;
    const float sx = mx - 18 * g_scale - status_w;
    const float sy = row_y + (bsize.y - line) * 0.5f;
    dl->AddCircleFilled(ImVec2(sx - dot * 2.2f, sy + line * 0.5f), dot, kStatus[status_color & 3]);
    ImGui::PushStyleColor(ImGuiCol_Text, status_color == 2 ? kTextDim : kText);
    ImGui::RenderTextEllipsis(dl, ImVec2(sx, sy), ImVec2(sx + status_w, sy + line), sx + status_w, status, nullptr, nullptr);
    ImGui::PopStyleColor();
    if (status_full > status_w && ImGui::IsMouseHoveringRect(ImVec2(sx, sy), ImVec2(sx + status_w, sy + line)))
        ImGui::SetTooltip("%s", status);

    // Name and signal bars on the left, clear of the status
    const float name_max = sx - dot * 4 - 20 * g_scale - 24 * g_scale; // Room for the bars
    ImGui::PushFont(g_font_bold, g_font_bold->LegacySize);
    const float name_line = ImGui::GetTextLineHeight();
    const float ny = row_y + (bsize.y - name_line) * 0.5f;
    const float name_width = ImMin(ImGui::CalcTextSize(name).x, name_max - (start.x + pad));
    ImGui::RenderTextEllipsis(dl, ImVec2(start.x + pad, ny), ImVec2(start.x + pad + name_width, ny + name_line),
                              start.x + pad + name_width, name, nullptr, nullptr);
    ImGui::PopFont();

    if (signal_bars > 0)
    {
        float x = start.x + pad + name_width + 12 * g_scale;
        float base = ny + name_line * 0.8f;
        for (int i = 0; i < 4; i++)
        {
            float h = (4 + i * 3) * g_scale;
            ImU32 col = i < signal_bars ? kStatus[signal_bars >= 3 ? 0 : signal_bars == 2 ? 1 : 3] : kCardBorder;
            dl->AddRectFilled(ImVec2(x + i * 5 * g_scale, base - h), ImVec2(x + i * 5 * g_scale + 3 * g_scale, base), col, 1);
        }
    }

    // Details across the card
    {
        const float x0 = start.x + pad, x1 = start.x + width - pad;
        ImGui::PushStyleColor(ImGuiCol_Text, kTextDim);
        ImGui::RenderTextEllipsis(dl, ImVec2(x0, detail_y), ImVec2(x1, detail_y + line), x1, detail, nullptr, nullptr);
        ImGui::PopStyleColor();
        if (ImGui::CalcTextSize(detail).x > x1 - x0 && ImGui::IsMouseHoveringRect(ImVec2(x0, detail_y), ImVec2(x1, detail_y + line)))
            ImGui::SetTooltip("%s", detail);
    }

    ImGui::SetCursorScreenPos(ImVec2(bx, row_y));
    if (button_kind == 1)
    {
        ImGui::PushStyleColor(ImGuiCol_Button, kAccent);
        ImGui::PushStyleColor(ImGuiCol_ButtonHovered, IM_COL32(96, 214, 230, 255));
        ImGui::PushStyleColor(ImGuiCol_ButtonActive, IM_COL32(48, 168, 186, 255));
        ImGui::PushStyleColor(ImGuiCol_Text, IM_COL32(10, 24, 28, 255));
    }
    bool clicked = ImGui::Button(button, bsize);
    if (button_kind == 1)
        ImGui::PopStyleColor(4);

    int picked = -1;
    if (mode_count > 0)
    {
        ImGui::SetCursorScreenPos(ImVec2(mx, row_y + (bsize.y - ImGui::GetFrameHeight()) * 0.5f));
        ImGui::SetNextItemWidth(mode_w);
        const bool known = mode_selected >= 0 && mode_selected < mode_count;
        if (ImGui::BeginCombo("##mode", known ? modes[mode_selected] : "Mode"))
        {
            for (int i = 0; i < mode_count; i++)
                if (ImGui::Selectable(modes[i], i == mode_selected))
                    picked = i;
            ImGui::EndCombo();
        }
    }

    ImGui::SetCursorScreenPos(ImVec2(start.x, start.y + height + 10 * g_scale));
    ImGui::Dummy(ImVec2(0, 0));
    ImGui::PopID();
    return (clicked ? 1 : 0) | (picked >= 0 ? (picked + 1) << 8 : 0);
}

extern "C" void ui_empty(const char *text)
{
    ImGui::Dummy(ImVec2(0, 40 * g_scale));
    const float w = ImGui::CalcTextSize(text).x;
    ImGui::SetCursorPosX((ImGui::GetWindowWidth() - w) * 0.5f);
    ImGui::PushStyleColor(ImGuiCol_Text, kTextDim);
    ImGui::TextUnformatted(text);
    ImGui::PopStyleColor();
}

// Footer: the message and the update badge, then "Start with Windows", "Close to tray" and
// "Drivers" (which opens the Drivers window, see ui_drivers). Returns a mask: 1 Drivers opened,
// 4 "Start with Windows" toggled (its new value in *start_with_windows), 8 the update badge
// clicked. *close_to_tray is the checkbox, and closing the window follows it.
// update_kind: 0 no badge, 1 up to date, 2 update available.
extern "C" int ui_footer(const char *message, int *start_with_windows, int *close_to_tray,
                         const char *update_text, int update_kind)
{
    const ImGuiStyle &st = ImGui::GetStyle();
    const float line = ImGui::GetFrameHeight();
    ImGui::SetCursorPosY(ImGui::GetWindowHeight() - line * 2 - st.ItemSpacing.y - st.WindowPadding.y);

    ImGui::PushStyleColor(ImGuiCol_Text, kTextDim);
    ImGui::AlignTextToFramePadding();
    ImGui::TextUnformatted(message);
    ImGui::PopStyleColor();

    int result = 0;

    // Update badge on the right of the message line: quiet when current, a link when not
    if (update_kind != 0)
    {
        const float w = ImGui::CalcTextSize(update_text).x + st.FramePadding.x * 2;
        ImGui::SameLine(ImGui::GetWindowContentRegionMax().x - w);
        const bool available = update_kind == 2;
        ImGui::PushStyleColor(ImGuiCol_Button, available ? IM_COL32(64, 196, 214, 40) : IM_COL32(0, 0, 0, 0));
        ImGui::PushStyleColor(ImGuiCol_ButtonHovered, available ? IM_COL32(64, 196, 214, 70) : IM_COL32(0, 0, 0, 0));
        ImGui::PushStyleColor(ImGuiCol_ButtonActive, available ? IM_COL32(64, 196, 214, 90) : IM_COL32(0, 0, 0, 0));
        ImGui::PushStyleColor(ImGuiCol_Text, available ? kAccent : kTextDim);
        if (ImGui::Button(update_text) && available)
            result |= 8;
        ImGui::PopStyleColor(4);
    }
    bool start = *start_with_windows != 0;
    ImGui::PushStyleColor(ImGuiCol_FrameBg, IM_COL32(36, 40, 47, 255));
    ImGui::PushStyleColor(ImGuiCol_FrameBgHovered, IM_COL32(48, 53, 62, 255));
    ImGui::PushStyleColor(ImGuiCol_CheckMark, kAccent);
    if (ImGui::Checkbox("Start with Windows", &start))
    {
        *start_with_windows = start ? 1 : 0;
        result |= 4;
    }
    ImGui::SameLine(0, 18 * g_scale);
    bool to_tray = *close_to_tray != 0;
    if (ImGui::Checkbox("Close to tray", &to_tray))
        *close_to_tray = to_tray ? 1 : 0;
    g_close_to_tray = to_tray;
    ImGui::PopStyleColor(3);

    const char *label = "Drivers";
    const float w = ImGui::CalcTextSize(label).x + st.FramePadding.x * 2;
    ImGui::SameLine(ImGui::GetWindowContentRegionMax().x - w);
    ImGui::PushStyleColor(ImGuiCol_Button, IM_COL32(0, 0, 0, 0));
    ImGui::PushStyleColor(ImGuiCol_Text, kTextDim);
    if (ImGui::Button(label))
    {
        ImGui::OpenPopup("Drivers");
        result |= 1;
    }
    ImGui::PopStyleColor(2);
    return result;
}

// The Drivers window, while it is open (the footer's Drivers button opens it): a row per driver
// with what it is for, whether it is installed, and Install or Remove. Remove asks first, in its
// row. busy[i] is 1 while that driver is being installed or removed. Returns 0, or 1 + row * 2
// for Install and 2 + row * 2 for Remove.
static int g_confirm_row = -1;

extern "C" int ui_drivers(const char **names, const char **details, const char **states, const int *installed,
                          const int *busy, const char **remove_warnings, int count, const char *note)
{
    int action = 0;
    const ImGuiStyle &st = ImGui::GetStyle();
    const float width = 440 * g_scale;
    ImGui::SetNextWindowPos(ImGui::GetMainViewport()->GetCenter(), ImGuiCond_Always, ImVec2(0.5f, 0.5f));
    ImGui::SetNextWindowSize(ImVec2(width + st.WindowPadding.x * 2, 0));
    if (!ImGui::BeginPopupModal("Drivers", nullptr, ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoMove))
    {
        g_confirm_row = -1;
        return 0;
    }

    bool any_busy = false;
    for (int i = 0; i < count; i++)
        any_busy |= busy[i] != 0;

    for (int i = 0; i < count; i++)
    {
        ImGui::PushID(i);
        if (i > 0)
        {
            ImGui::Dummy(ImVec2(0, 4 * g_scale));
            ImGui::Separator();
            ImGui::Dummy(ImVec2(0, 4 * g_scale));
        }

        // Name, and the button on the right of it
        const bool confirming = g_confirm_row == i;
        const char *button = installed[i] ? "Remove" : "Install";
        const float bw = ImGui::CalcTextSize(confirming ? "Remove" : button).x + st.FramePadding.x * 2 + 8 * g_scale;
        const float cancel_w = ImGui::CalcTextSize("Cancel").x + st.FramePadding.x * 2 + 8 * g_scale;
        const float row_x = ImGui::GetCursorPosX();

        ImGui::PushFont(g_font_bold, g_font_bold->LegacySize);
        ImGui::AlignTextToFramePadding();
        ImGui::TextUnformatted(names[i]);
        ImGui::PopFont();

        ImGui::SameLine(row_x + width - bw - (confirming ? cancel_w + st.ItemSpacing.x : 0));
        ImGui::BeginDisabled(any_busy);
        if (confirming)
        {
            ImGui::PushStyleColor(ImGuiCol_Button, kStatus[3]);
            if (ImGui::Button("Remove", ImVec2(bw, 0)))
            {
                action = 2 + i * 2;
                g_confirm_row = -1;
            }
            ImGui::PopStyleColor();
            ImGui::SameLine();
            if (ImGui::Button("Cancel", ImVec2(cancel_w, 0)))
                g_confirm_row = -1;
        }
        else if (ImGui::Button(button, ImVec2(bw, 0)))
        {
            if (installed[i])
                g_confirm_row = i;
            else
                action = 1 + i * 2;
        }
        ImGui::EndDisabled();

        // What it is for, then its state (or, while confirming, what removing it does)
        ImGui::PushTextWrapPos(row_x + width);
        ImGui::PushStyleColor(ImGuiCol_Text, kTextDim);
        ImGui::TextUnformatted(details[i]);
        ImGui::PopStyleColor();
        const ImU32 colour = busy[i] ? kAccent : installed[i] ? kStatus[0] : kTextDim;
        ImGui::PushStyleColor(ImGuiCol_Text, confirming ? kStatus[3] : colour);
        ImGui::TextUnformatted(confirming ? remove_warnings[i] : states[i]);
        ImGui::PopStyleColor();
        ImGui::PopTextWrapPos();
        ImGui::PopID();
    }

    ImGui::Dummy(ImVec2(0, 8 * g_scale));
    if (note && note[0])
    {
        ImGui::PushTextWrapPos(ImGui::GetCursorPosX() + width);
        ImGui::PushStyleColor(ImGuiCol_Text, kTextDim);
        ImGui::TextUnformatted(note);
        ImGui::PopStyleColor();
        ImGui::PopTextWrapPos();
        ImGui::Dummy(ImVec2(0, 4 * g_scale));
    }
    const float close_w = ImGui::CalcTextSize("Close").x + st.FramePadding.x * 2 + 8 * g_scale;
    ImGui::SetCursorPosX(ImGui::GetCursorPosX() + width - close_w);
    if (ImGui::Button("Close", ImVec2(close_w, 0)))
    {
        g_confirm_row = -1;
        ImGui::CloseCurrentPopup();
    }
    ImGui::EndPopup();
    return action;
}

