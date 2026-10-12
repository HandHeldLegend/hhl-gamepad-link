// HHL Gamepad Link: HOJA gamepads connect to this PC over Bluetooth, as they would to a HOJA
// dongle, and are opened here as their own controllers. The app picks their mode and report rate,
// so switching mode needs no new pairing or reconnect.

using HIDMaestro;
using HhlGamepadLink;

int[] rates = { 125, 250, 500 };
string[] rateLabels = rates.Select(r => $"{r} Hz").ToArray();
int rateIndex = 1; // 250 Hz each run: nothing is saved

// The modes a gamepad can be put in from here: the ones this app opens
byte[] modeFormats = { Protocol.FormatSInput, Protocol.FormatXInput, Protocol.FormatSlippi };
string[] modeLabels = { "SInput", "XInput", "Slippi" };
string[] modeTrayLabels = { "SInput (Steam)", "XInput", "Slippi (GameCube adapter)" };

// Tray menu item IDs: the report rates, then ten per gamepad for its modes
const int TrayRateBase = 100, TrayModeBase = 1000;

// The tray menu as last given to the window, and the gamepads its mode items stand for
string trayKey = "";
var trayPads = new List<string>();

// One copy per PC: each sweeps away the controllers a killed run left behind
using var single = new Mutex(initiallyOwned: true, @"Global\HhlGamepadLink", out bool firstCopy);
if (!firstCopy)
    return 0;

// --tray: started with Windows, so it starts hidden in the notification area
bool startHidden = args.Contains("--tray");
if (!NativeUi.Open("HHL Gamepad Link", 620, 480, startHidden))
    return 1;
Autostart.AdoptOldTask();
bool startWithWindows = Autostart.IsOn();
Settings.Load();
bool closeToTray = Settings.CloseToTray; // Kept between runs (Settings)
if (startWithWindows)
    Autostart.Set(true); // Points it at this .exe, wherever it now is and whatever it is called

using var stopUpdates = new CancellationTokenSource();
Updates.Start(stopUpdates.Token);

using var ctx = new HMContext();
ctx.LoadDefaultProfiles();

// Clear what a killed run left behind, then get the driver ready. That takes a moment the first
// time: the window stays responsive meanwhile.
string message = "Getting the controller driver ready…";
var driverReady = Task.Run(() =>
{
    // Not while another HIDMaestro app has controllers up: the sweep removes every app's
    if (!HMContext.OtherAppHasControllers())
        HMContext.RemoveAllVirtualControllers(preserveInstall: true);
    ctx.InstallDriver();
});
LinkHub? hub = null;
ConfigServer? config = null;
var drivers = new Drivers(ctx, () => hub);
drivers.ControllerDriverInstalled += () => driverReady = Task.CompletedTask; // Installed from the Drivers window

NativeUi.Frame frame;
while ((frame = NativeUi.BeginFrame()) != NativeUi.Frame.Closed)
{
    if (hub == null && driverReady.IsCompleted)
    {
        if (driverReady.IsFaulted)
            message = $"The driver didn't install: {driverReady.Exception!.InnerException!.Message}";
        else
        {
            hub = new LinkHub(ctx, rates[rateIndex]);
            message = "Ready";
            try { config = new ConfigServer(hub); }
            catch (Exception e) { message = $"Ready (config app link unavailable: {e.Message})"; }
        }
    }

    UpdateTray();
    int pick = NativeUi.TakeTrayChoice();
    if (pick >= TrayModeBase && (pick - TrayModeBase) / 10 < trayPads.Count)
        hub?.SetMode(trayPads[(pick - TrayModeBase) / 10], modeFormats[(pick - TrayModeBase) % 10]);
    else if (pick >= TrayRateBase && pick < TrayRateBase + rates.Length)
    {
        rateIndex = pick - TrayRateBase;
        hub?.SetRate(rates[rateIndex]);
    }

    // Hidden in the tray: the gamepads keep running, there is just nothing to draw
    if (frame == NativeUi.Frame.Hidden)
        continue;

    int chosen = NativeUi.RateHeader("HHL Gamepad Link", "Report rate", rateLabels, rateIndex);
    if (chosen != rateIndex)
    {
        rateIndex = chosen;
        hub?.SetRate(rates[rateIndex]);
    }

    var pads = hub?.Snapshot() ?? Array.Empty<PadEntry>();
    if (pads.Count == 0)
        NativeUi.Empty(hub == null ? "Starting…" : "Waiting for gamepads in Link mode. Pair one as “<name> Link” in Bluetooth settings.");

    foreach (var pad in pads)
        DrawCard(pad);

    string updateText = Updates.Status == Updates.State.Available
        ? $"Update available · v{Updates.Latest}"
        : $"Up to date · v{Updates.Current}";
    var action = NativeUi.FooterBar(Drivers.Working ?? message, ref startWithWindows, ref closeToTray,
                                    Updates.Status, updateText);
    if (action.HasFlag(NativeUi.FooterAction.OpenDrivers))
        drivers.Refresh();
    if (NativeUi.DriversWindow(drivers.Rows, drivers.Note) is var (row, remove))
        drivers.Act((Drivers.Which)row, remove);
    NativeUi.End();

    Settings.SetCloseToTray(closeToTray);
    if (action.HasFlag(NativeUi.FooterAction.OpenUpdate))
        Updates.OpenReleasesPage();
    if (action.HasFlag(NativeUi.FooterAction.StartWithWindowsChanged))
    {
        if (!Autostart.Set(startWithWindows))
            message = "Couldn't change Start with Windows";
        startWithWindows = Autostart.IsOn();
    }
}

// The window goes first; then every controller made here is removed before the app exits
NativeUi.Shutdown();
config?.Dispose();
hub?.Dispose();
ctx.Dispose();
return 0;

// The mode a session's gamepad is in (or is being put in), as an index into modeFormats
int ModeIndex(PadSession session) => Array.IndexOf(modeFormats, session.WantedFormat ?? session.Format);

// The tray menu's own items: the report rate, and each gamepad open here with its mode
void UpdateTray()
{
    var items = new List<NativeUi.TrayItem> { new("Report rate", 0, 4) };
    for (int i = 0; i < rates.Length; i++)
        items.Add(new(rateLabels[i], TrayRateBase + i, i == rateIndex ? 1 : 0));
    items.Add(new("", 0, 8));

    var pads = new List<string>();
    foreach (var e in hub?.Snapshot() ?? Array.Empty<PadEntry>())
    {
        if (e.Session is not { State: not SessionState.Closed } session)
            continue;
        int slot = pads.Count;
        pads.Add(e.Pad.Id);
        items.Add(new(e.Pad.Title, 0, 4));
        int current = ModeIndex(session);
        for (int m = 0; m < modeFormats.Length; m++)
            items.Add(new(modeTrayLabels[m], TrayModeBase + slot * 10 + m, m == current ? 1 : 0));
        items.Add(new("", 0, 8));
    }

    string key = string.Join("|", items);
    if (key == trayKey)
        return;
    trayKey = key;
    trayPads = pads;
    NativeUi.SetTrayItems(items);
}

void DrawCard(PadEntry e)
{
    var session = e.Session;
    bool here = session is { State: not SessionState.Closed };
    string mode = Protocol.FormatName(e.Pad.Format);

    string status, button, detail;
    NativeUi.Status colour;
    bool accent;
    if (e.Away)
    {
        status = "Disconnected";
        colour = NativeUi.Status.Problem;
        button = "Forget";
        accent = false;
        detail = $"{mode}  ·  reconnects when it's back";
    }
    else if (here)
    {
        bool live = session!.State == SessionState.Open;
        status = live ? "Open here" : session.Status;
        colour = session.Problem != null ? NativeUi.Status.Problem : NativeUi.Status.OpenHere;
        button = "Disconnect";
        accent = false;
        string port = session.AdapterPort is int n ? $"  ·  adapter port {n}" : "";
        detail = session.Problem != null ? $"{mode}  ·  {session.Problem}"
            : live ? $"{mode}{port}  ·  {session.PacketsPerSecond}/s in" +
                     (session.AdapterPort == null ? $", {session.ReportsPerSecond} Hz out" : "") + "  ·  " +
                     $"{session.BufferMs:F0} ms buffer, {session.LatePercent:F1}% late"
            : mode;
    }
    else
    {
        // Disconnected here: turned away until opened again
        status = "Closed";
        colour = NativeUi.Status.Free;
        button = "Open";
        accent = true;
        detail = $"{mode}  ·  connects when it next calls";
    }

    // A gamepad open here gets a mode picker
    var (clicked, picked) = NativeUi.Card(e.Pad.Id, e.Pad.Title, detail, status, colour, 0, button, accent,
        here ? modeLabels : null, here ? ModeIndex(session!) : -1);
    if (picked is int m)
        hub!.SetMode(e.Pad.Id, modeFormats[m]);
    if (clicked)
    {
        if (e.Away)
            hub!.Forget(e.Pad.Id);
        else if (here)
            hub!.Close(e.Pad.Id);
        else
            hub!.Allow(e.Pad.Id);
    }
}
