// HHL Gamepad Link: HOJA gamepads connect to this PC over Bluetooth, as they would to a HOJA
// dongle, and are opened here as their own controllers. The app picks their mode and input profile,
// so switching mode needs no new pairing or reconnect.

using HIDMaestro;
using HhlGamepadLink;

const string AppName = "HHL Gamepad Link";
const int WindowWidth = 620, WindowHeight = 480;

// Input profiles: the report interval each asks the gamepads for. A PC polls Bluetooth in 1.25 ms
// slot pairs and an interval off them misses polls, so each is a whole number of slot pairs.
// Measured over Link mode: 3.75 ms gives ~255 reports/s, 2.5 ms ~372/s, both with a median delay
// under 0.5 ms (1.25 ms tops out at ~490/s with three times the delay). The cards show the rate.
string[] profileLabels = { "Power", "Balanced", "Performance" };
int[] profileIntervalsUs = { 7500, 3750, 2500 };
int profileIndex = 1; // Balanced each run: nothing is saved

// Scan pressed, while a gamepad is open here: until it is pressed again or the window loses focus
bool scanRequested = false;

// The modes a gamepad can be put in from here: the ones this app opens
byte[] modeFormats = { Protocol.FormatSInput, Protocol.FormatXInput, Protocol.FormatSlippi };
string[] modeLabels = { "SInput", "XInput", "Slippi" };
string[] modeTrayLabels = { "SInput (Steam)", "XInput", "Slippi (GameCube adapter)" };

// Tray menu item IDs: the input profiles, then a block per gamepad for its modes
const int TrayProfileBase = 100, TrayModeBase = 1000, TrayModesPerPad = 10;

// The tray menu as last given to the window, and the gamepads its mode items stand for
string trayKey = "";
var trayPads = new List<string>();

// Anything that would end the app goes to the log first
Log.CatchUnhandled();

// One copy per PC: each sweeps away the controllers a killed run left behind
using var single = new Mutex(initiallyOwned: true, @"Global\HhlGamepadLink", out bool firstCopy);
if (!firstCopy)
    return 0;

// --tray: started with Windows, so it starts hidden in the notification area
bool startHidden = args.Contains("--tray");
if (!NativeUi.Open(AppName, WindowWidth, WindowHeight, startHidden))
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
// Installed from the Drivers window
drivers.ControllerDriverInstalled += () => driverReady = Task.CompletedTask;

NativeUi.Frame frame;
while ((frame = NativeUi.BeginFrame()) != NativeUi.Frame.Closed)
{
    if (hub == null && driverReady.IsCompleted)
        StartHub();

    if (!NativeUi.Focused())
        scanRequested = false;
    if (hub != null)
        hub.ScanRequested = scanRequested;
    UpdateTray();
    ApplyTrayChoice(NativeUi.TakeTrayChoice());

    // Hidden in the tray: the gamepads keep running, there is just nothing to draw
    if (frame == NativeUi.Frame.Hidden)
        continue;

    int chosen = NativeUi.ChoiceHeader(AppName, "Input", profileLabels, profileIndex);
    if (chosen != profileIndex)
        SetProfile(chosen);
    if (hub != null)
        DrawScanRow(hub);

    var pads = hub?.Snapshot() ?? Array.Empty<PadEntry>();
    if (pads.Count == 0)
        NativeUi.Empty(hub == null
            ? "Starting…"
            : "Waiting for gamepads in Link mode. Pair one as “<name> Link” in Bluetooth settings.");

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

// The driver is ready (or failed): the gamepads can come
void StartHub()
{
    if (driverReady.IsFaulted)
    {
        message = $"The driver didn't install: {driverReady.Exception!.InnerException!.Message}";
        Log.Write($"Driver: {driverReady.Exception!.InnerException}");
        return;
    }
    hub = new LinkHub(ctx, profileIntervalsUs[profileIndex]);
    message = "Ready";
    try { config = new ConfigServer(hub); }
    catch (Exception e)
    {
        message = $"Ready (config app link unavailable: {e.Message})";
        Log.Write($"Config app link: {e}");
    }
}

// Whether the app looks for gamepads nearby. With none open here it always does (so a gamepad
// can be found and called); with one open, scanning costs it report rate, so it is the user's to
// turn on, and turns off when the window loses focus.
void DrawScanRow(LinkHub hub)
{
    bool open = hub.AnyOpenHere;
    bool scanning = hub.Searching;
    string note = !open ? "Looking for gamepads nearby"
                : scanning ? "Looking for gamepads nearby  ·  stop for full speed"
                : "Full speed  ·  scan to find other gamepads nearby";
    if (NativeUi.ScanBar(scanning, open, note))
        scanRequested = !scanning;
}

void SetProfile(int index)
{
    profileIndex = index;
    hub?.SetReportInterval(profileIntervalsUs[profileIndex]);
}

// The mode a session's gamepad is in (or is being put in), as an index into modeFormats
int ModeIndex(PadSession session) => Array.IndexOf(modeFormats, session.WantedFormat ?? session.Format);

// The tray menu's own items: the input profile, and each gamepad open here with its mode
void UpdateTray()
{
    var items = new List<NativeUi.TrayItem> { new("Input", 0, NativeUi.TraySubmenuStart) };
    for (int i = 0; i < profileLabels.Length; i++)
        items.Add(new(profileLabels[i], TrayProfileBase + i, i == profileIndex ? NativeUi.TrayChecked : 0));
    items.Add(new("", 0, NativeUi.TraySubmenuEnd));

    var pads = new List<string>();
    foreach (var e in hub?.Snapshot() ?? Array.Empty<PadEntry>())
    {
        if (e.Session is not { State: not SessionState.Closed } session)
            continue;
        int firstId = TrayModeBase + pads.Count * TrayModesPerPad;
        pads.Add(e.Pad.Id);
        items.Add(new(e.Pad.Name, 0, NativeUi.TraySubmenuStart));
        int current = ModeIndex(session);
        for (int m = 0; m < modeFormats.Length; m++)
            items.Add(new(modeTrayLabels[m], firstId + m, m == current ? NativeUi.TrayChecked : 0));
        items.Add(new("", 0, NativeUi.TraySubmenuEnd));
    }

    string key = string.Join("|", items);
    if (key == trayKey)
        return;
    trayKey = key;
    trayPads = pads;
    NativeUi.SetTrayItems(items);
}

// A pick from the tray menu: a gamepad's mode, or the input profile
void ApplyTrayChoice(int pick)
{
    int slot = (pick - TrayModeBase) / TrayModesPerPad;
    if (pick >= TrayModeBase && slot < trayPads.Count)
        hub?.SetMode(trayPads[slot], modeFormats[(pick - TrayModeBase) % TrayModesPerPad]);
    else if (pick >= TrayProfileBase && pick < TrayProfileBase + profileLabels.Length)
        SetProfile(pick - TrayProfileBase);
}

void DrawCard(PadEntry e)
{
    var session = e.Session;
    bool here = session is { State: not SessionState.Closed };
    // With a user name set in the config app, it is the title and the model goes on the details line
    string title = e.UserName.Length > 0 ? e.UserName : e.Pad.Name;
    var look = CardLook(e, here);

    // A gamepad open here gets a mode picker
    var (clicked, picked) = NativeUi.Card(e.Pad.Id, title, look.Detail, look.Status, look.Colour,
                                          look.Button, look.Accent, here ? modeLabels : null,
                                          here ? ModeIndex(session!) : -1);
    if (picked is int m)
        hub!.SetMode(e.Pad.Id, modeFormats[m]);
    if (!clicked)
        return;
    if (here)
        hub!.Close(e.Pad.Id);
    else if (e.Nearby != null && e.Known)
        hub!.Call(e.Pad.Id);
    else if (e.Nearby != null)
        hub!.Pair(e.Pad.Id);
    else
        hub!.Forget(e.Pad.Id);
}

// What a card says, and its button, for where its gamepad is
(string Status, NativeUi.Status Colour, string Button, bool Accent, string Detail)
    CardLook(PadEntry e, bool here)
{
    string mode = Protocol.FormatName(e.Pad.Format);
    string model = e.UserName.Length > 0 ? $"{e.Pad.Name}  ·  " : "";

    if (!here && e.Nearby != null && e.Known)
    {
        // Linked here before, connected somewhere else, and close by: it can be called over
        return ("Nearby", NativeUi.Status.Free, "Use here", true,
                $"{model}{mode} on another device  ·  {e.Call ?? "Use here calls it over"}");
    }
    if (!here && e.Nearby != null)
    {
        // Close by and not paired with this PC: pair it
        string hint = e.Nearby.Linked ? "linked to another PC; Pair adds this one" : "Pair sets it up here";
        return ("New", NativeUi.Status.Free, "Pair", true, $"{model}{mode}  ·  {e.Call ?? hint}");
    }
    if (e.Away)
    {
        return ("Disconnected", NativeUi.Status.Problem, "Forget", false,
                $"{mode}  ·  reconnects when it's back");
    }
    if (here)
    {
        var session = e.Session!;
        bool live = session.State == SessionState.Open;
        var colour = session.Problem != null ? NativeUi.Status.Problem : NativeUi.Status.OpenHere;
        return (live ? "Open here" : session.Status, colour, "Disconnect", false,
                OpenDetail(session, mode, live));
    }
    // Disconnected here: handed back. It looks for a dongle, and comes back here when it is next
    // started in dongle mode (a gamepad only calls this PC as it starts)
    return (e.Refused ? "Releasing…" : "Released", NativeUi.Status.Free, "Forget", false,
            $"{mode}  ·  back on a dongle; start it in dongle mode to use it here");
}

// An open gamepad's details line: its problem, or once it is live, its rates and buffer
string OpenDetail(PadSession session, string mode, bool live)
{
    if (session.Problem != null)
        return $"{mode}  ·  {session.Problem}";
    if (!live)
        return mode;
    string port = session.AdapterPort is int n ? $"  ·  adapter port {n}" : "";
    string output = session.AdapterPort == null ? $", {session.ReportsPerSecond} Hz out" : "";
    return $"{mode}{port}  ·  {session.PacketsPerSecond}/s in{output}  ·  " +
           $"{session.BufferMs:F0} ms buffer, {session.LatePercent:F1}% late";
}
