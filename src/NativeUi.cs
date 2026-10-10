// The window's drawing calls, implemented in native/hhl_ui.cpp and linked into the .exe

using System.Runtime.InteropServices;

namespace HhlGamepadWlan;

static partial class NativeUi
{
    const string Lib = "hhl_ui";

    [LibraryImport(Lib, EntryPoint = "ui_init", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Init(string title, int width, int height, int startHidden);

    [LibraryImport(Lib, EntryPoint = "ui_begin")]
    private static partial int Begin();

    [LibraryImport(Lib, EntryPoint = "ui_end")]
    public static partial void End();

    [LibraryImport(Lib, EntryPoint = "ui_shutdown")]
    public static partial void Shutdown();

    [LibraryImport(Lib, EntryPoint = "ui_header", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Header(string title, string subtitle, IntPtr[] rates, int count, int selected);

    [LibraryImport(Lib, EntryPoint = "ui_pad_card", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PadCard(string id, string name, string detail, string status, int statusColor,
                                       int signalBars, string button, int buttonKind);

    [LibraryImport(Lib, EntryPoint = "ui_empty", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void Empty(string text);

    [LibraryImport(Lib, EntryPoint = "ui_footer", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Footer(string message, string confirmText, ref int startWithWindows,
                                      string updateText, int updateKind);

    [LibraryImport(Lib, EntryPoint = "ui_hide_to_tray")]
    public static partial void HideToTray();

    public enum Status { OpenHere = 0, Elsewhere = 1, Free = 2, Problem = 3 }

    public enum Frame { Closed = 0, Draw = 1, Hidden = 2 }

    [Flags]
    public enum FooterAction { None = 0, RemoveDriver = 1, HideToTray = 2, StartWithWindowsChanged = 4, OpenUpdate = 8 }

    public static bool Open(string title, int width, int height, bool startHidden) =>
        Init(title, width, height, startHidden ? 1 : 0) != 0;

    public static Frame BeginFrame() => (Frame)Begin();

    // The rate labels live for the whole run, as native UTF-8 strings
    static IntPtr[]? _rateLabels;

    public static int RateHeader(string title, string subtitle, string[] rates, int selected)
    {
        _rateLabels ??= rates.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        return Header(title, subtitle, _rateLabels, rates.Length, selected);
    }

    public static bool Card(string id, string name, string detail, string status, Status colour, int signalBars,
                            string button, bool accent) =>
        PadCard(id, name, detail, status, (int)colour, signalBars, button, accent ? 1 : 0) != 0;

    public static FooterAction FooterBar(string message, string confirmText, ref bool startWithWindows,
                                         Updates.State update, string updateText)
    {
        int start = startWithWindows ? 1 : 0;
        var action = (FooterAction)Footer(message, confirmText, ref start, updateText, (int)update);
        startWithWindows = start != 0;
        return action;
    }
}
