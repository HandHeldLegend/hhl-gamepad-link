// The window's drawing calls, implemented in native/hhl_ui.cpp and linked into the .exe

using System.Runtime.InteropServices;

namespace HhlGamepadLink;

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
                                       int signalBars, string button, int buttonKind,
                                       string[] modes, int modeCount, int modeSelected);

    [LibraryImport(Lib, EntryPoint = "ui_tray_items", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void TrayItems(string[] labels, int[] ids, int[] flags, int count);

    [LibraryImport(Lib, EntryPoint = "ui_tray_take_choice")]
    public static partial int TakeTrayChoice();

    [LibraryImport(Lib, EntryPoint = "ui_empty", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void Empty(string text);

    [LibraryImport(Lib, EntryPoint = "ui_footer", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Footer(string message, ref int startWithWindows, ref int closeToTray,
                                      string updateText, int updateKind);

    [LibraryImport(Lib, EntryPoint = "ui_drivers", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DriversWindow(string[] names, string[] details, string[] states, int[] installed,
                                             int[] busy, string[] removeWarnings, int count, string note);

    public enum Status { OpenHere = 0, Elsewhere = 1, Free = 2, Problem = 3 }

    public enum Frame { Closed = 0, Draw = 1, Hidden = 2 }

    [Flags]
    public enum FooterAction { None = 0, OpenDrivers = 1, StartWithWindowsChanged = 4, OpenUpdate = 8 }

    // A row of the Drivers window
    public sealed record DriverRow(string Name, string Detail, string State, bool Installed, bool Busy, string RemoveWarning);

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

    // A gamepad's card, with a mode picker when modes are given. Returns whether its button was
    // clicked, and the mode picked, if one was.
    public static (bool Clicked, int? Mode) Card(string id, string name, string detail, string status, Status colour,
                                                int signalBars, string button, bool accent,
                                                string[]? modes = null, int modeSelected = -1)
    {
        modes ??= Array.Empty<string>();
        int result = PadCard(id, name, detail, status, (int)colour, signalBars, button, accent ? 1 : 0,
                             modes, modes.Length, modeSelected);
        return ((result & 1) != 0, (result >> 8) > 0 ? (result >> 8) - 1 : null);
    }

    // A tray menu item. Flags: 1 checked, 2 separator, 4 starts a submenu (titled with this item),
    // 8 ends it.
    public sealed record TrayItem(string Label, int Id, int Flags);

    public static void SetTrayItems(IReadOnlyList<TrayItem> items) =>
        TrayItems(items.Select(i => i.Label).ToArray(), items.Select(i => i.Id).ToArray(),
                  items.Select(i => i.Flags).ToArray(), items.Count);

    public static FooterAction FooterBar(string message, ref bool startWithWindows, ref bool closeToTray,
                                         Updates.State update, string updateText)
    {
        int start = startWithWindows ? 1 : 0, tray = closeToTray ? 1 : 0;
        var action = (FooterAction)Footer(message, ref start, ref tray, updateText, (int)update);
        startWithWindows = start != 0;
        closeToTray = tray != 0;
        return action;
    }

    // Draws the Drivers window while it is open. Returns the row whose Install or Remove was
    // pressed, if any.
    public static (int Row, bool Remove)? DriversWindow(IReadOnlyList<DriverRow> rows, string note)
    {
        int action = DriversWindow(rows.Select(r => r.Name).ToArray(), rows.Select(r => r.Detail).ToArray(),
                                   rows.Select(r => r.State).ToArray(), rows.Select(r => r.Installed ? 1 : 0).ToArray(),
                                   rows.Select(r => r.Busy ? 1 : 0).ToArray(), rows.Select(r => r.RemoveWarning).ToArray(),
                                   rows.Count, note);
        return action == 0 ? null : ((action - 1) / 2, (action - 1) % 2 == 1);
    }
}
