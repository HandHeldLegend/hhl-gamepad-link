// One gamepad in Link mode on this PC: tells it what this PC wants (mode and report rate, every
// second, which also tells it the app is here), presents it as the device for its mode, and gives
// its reports to that device as they arrive: no buffer, for the least delay. In Slippi mode the
// device is a port on the GameCube adapter that every Slippi gamepad here shares (see GcAdapter).
//
// Threads: the device's reader thread calls OnReport and the hub's thread Heartbeat and Tick (all
// under the hub's lock); HIDMaestro raises output reports on its own thread (they go straight out);
// the pump submits to the device; creating and removing the device run in the background, because
// each takes seconds.

using System.Collections.Concurrent;
using HIDMaestro;

namespace HhlGamepadLink;

sealed class PadSession
{
    // XInput mode's device: a wired Xbox 360 controller as the USB device it is, on the USB/IP
    // transport, so Windows' own xusb22 drives it and every API sees one controller. HIDMaestro's
    // Xbox profiles pair a HID device with an XInput companion, which Steam lists as two pads
    // (XInput and GameInput each report it). The gamepad's XInput report is the 360's USB report,
    // and Windows' rumble and LED reports are what its XInput mode takes, so both pass through.
    const string XInputProfile = "hhl-xbox-360-usb";
    const int XInputReportSize = 20;

    // A new Xbox 360 sends no input for this long. Steam (SDL) sees a controller through both
    // XInput and GameInput and keeps one only when GameInput has it first; a wired 360 counts for
    // XInput once it reports, so this lets GameInput register it before XInput sees it.
    const long XInputQuietStartMs = 3000;

    // The driver hands the host the newest frame, so after a command reply the input waits briefly:
    // otherwise the next input can replace the reply before the host reads it
    const long ReplyHoldMs = 20;

    const int PumpIdleMs = 100;      // The pump wakes on its own this often, with nothing to do
    const int MaxReportBytes = 64;   // A gamepad report: live input or a command reply
    const long RateWindowMs = 1000;

    readonly HMContext _ctx;
    readonly GcAdapter _adapter;
    LinkDevice? _link;      // The gamepad's Link collection; the hub opens it
    int _reportIntervalUs;
    byte _wantedFormat;     // The mode this PC wants it in for this run, 0 for none
    long _lastReportMs = Environment.TickCount64;

    // The device and what the pump submits (under _submitLock)
    readonly object _submitLock = new();
    HMController? _device;
    byte _deviceFormat = Protocol.FormatNone;
    HMProfile? _deviceProfile;
    readonly ConcurrentQueue<byte[]> _inputs = new();   // Its input reports, until the pump takes them
    readonly ConcurrentQueue<byte[]> _oneShots = new(); // Command replies, each given to the host once
    readonly AutoResetEvent _wake = new(false);         // A report or reply came: the pump runs
    bool _doubleSubmit; // HIDMaestro's HID driver hands the host every other frame; USB/IP devices don't
    GcAdapter.Port? _gcPort;
    volatile bool _pumping;

    // Devices being created or removed in the background, so closing the app can wait for them
    readonly List<Task> _deviceWork = new();

    // SInput handshake: the gamepad's features, asked for once it streams, kept, and given to the
    // host whenever it asks, so SDL never waits on the radio
    byte[]? _features;
    bool _featuresAsked;

    // Rates shown in the list
    int _packets, _submitted;
    long _rateWindowStartMs;

    public PadSession(HMContext ctx, GcAdapter adapter, string id, int reportIntervalUs)
    {
        _ctx = ctx;
        _adapter = adapter;
        Id = id;
        _reportIntervalUs = reportIntervalUs;
    }

    public string Id { get; }                     // The gamepad's Bluetooth address
    public PadIdentity? Pad { get; private set; } // Its identity, once it has said who it is
    public SessionState State { get; private set; } = SessionState.Opening;
    public string Status { get; private set; } = "Opening…"; // A few words: it fits beside the button
    public string? Problem { get; private set; }               // Why it couldn't open, for the details line
    public CloseReason Reason { get; private set; } = CloseReason.None;
    public long SilentMs => Environment.TickCount64 - _lastReportMs;

    public byte Format => Pad?.Format ?? Protocol.FormatNone; // The mode its reports are in
    // The mode this PC wants it in: it follows unless its mode was chosen on the gamepad itself
    public byte? WantedFormat => _wantedFormat == 0 ? null : _wantedFormat;

    public int PacketsPerSecond { get; private set; }
    public int ReportsPerSecond { get; private set; }
    public int? AdapterPort => _gcPort?.Number;

    // The config app through ConfigServer: replies and the live view, as WebUSB packets
    public event Action<byte[]>? ConfigFromGamepad;

    public void Attach(LinkDevice link)
    {
        _link = link;
        SendHost(); // The gamepad waits for it before it says who it is
    }

    // Every second: the gamepad (re)starts on it, and goes back to a dongle without it
    public void Heartbeat() => SendHost();

    public void SetMode(byte format)
    {
        _wantedFormat = format;
        SendHost();
    }

    public void SetReportInterval(int intervalUs)
    {
        _reportIntervalUs = intervalUs;
        SendHost();
    }

    void SendHost() =>
        Send(Protocol.LaneMessage, Protocol.Host(_wantedFormat, Updates.CurrentNumber, _reportIntervalUs));

    // A disconnected gamepad's writes fail: its silence says it is away, not this. The hub stops the
    // host info to a gamepad disconnected here.
    void Send(byte lane, ReadOnlySpan<byte> payload) => _link?.Send(lane, payload);

    // The link is reliable and in order: config commands go straight out
    public void SendConfig(byte[] packet) =>
        Send(Protocol.LaneMessage, Protocol.Message(Protocol.MsgConfig, packet));

    // From the hub's thread every few ms: status and rates
    public void Tick()
    {
        if (State == SessionState.Closed)
            return;
        long nowMs = Environment.TickCount64;
        UpdateSlippiStatus();
        if (nowMs - _rateWindowStartMs >= RateWindowMs)
        {
            PacketsPerSecond = _packets;
            ReportsPerSecond = _submitted;
            _packets = _submitted = 0;
            _rateWindowStartMs = nowMs;
        }
    }

    public void Close(string why, CloseReason reason)
    {
        if (State == SessionState.Closed)
            return;
        State = SessionState.Closed;
        Status = why;
        Reason = reason;
        RemoveDevice();
    }

    // Wait until this session's devices are made and gone: the app calls it when closing
    public void WaitForDevices(TimeSpan limit)
    {
        WaitForDeviceWork(limit);
        RemoveDevice(); // One that finished being made while closing
        WaitForDeviceWork(limit);
    }

    // --- From the gamepad ---

    public void OnReport(byte[] report, long arrivalUs)
    {
        if (report.Length < 3)
            return;
        _lastReportMs = Environment.TickCount64;
        switch (report[0])
        {
            case Protocol.ReportLive:
                // [seq u16][sampled_us u32][length u8][report]
                if (report.Length >= 8 && Pad != null && State != SessionState.Closed)
                    OnInput(report.AsSpan(7), arrivalUs);
                break;

            case Protocol.ReportMessage:
                int length = Math.Min(report[2], report.Length - 3);
                var payload = report.AsSpan(3, length);
                if (report[1] == Protocol.LaneBulk && State != SessionState.Closed)
                    ConfigFromGamepad?.Invoke(payload.ToArray()); // The config app's live view
                else if (report[1] == Protocol.LaneMessage && length >= 1)
                    OnMessage(payload[0], payload.Slice(1));
                break;
        }
    }

    void OnMessage(byte type, ReadOnlySpan<byte> p)
    {
        switch (type)
        {
            case Protocol.MsgWake:
                OnWake(p);
                break;

            case Protocol.MsgReport:
                OnCommandReply(p.Slice(0, Math.Min(p.Length, MaxReportBytes)));
                break;

            case Protocol.MsgToken:
                // Kept, so this app can call the gamepad over from wherever it is connected
                if (p.Length >= Protocol.TokenLength)
                    KnownPads.Remember(Id, p.Slice(0, Protocol.TokenLength).ToArray(), Pad?.Name ?? Id);
                break;

            case Protocol.MsgConfig:
                ConfigFromGamepad?.Invoke(p.ToArray());
                break;
        }
    }

    // The gamepad says who it is: after the host info, and again after a mode change
    void OnWake(ReadOnlySpan<byte> p)
    {
        if (PadIdentity.Parse(Id, p) is not PadIdentity pad)
        {
            Fail("Can't open", "The gamepad's firmware speaks another link version");
            return;
        }
        if (State == SessionState.Closed)
        {
            // Back (it went away, or was released and started again): open it again
            State = SessionState.Opening;
            Reason = CloseReason.None;
            Problem = null;
        }
        // A new mode: its device goes, and the one for the new mode comes up
        if (_deviceFormat != Protocol.FormatNone && pad.Format != _deviceFormat)
            RemoveDevice();
        Pad = pad;
        if (State == SessionState.Opening)
        {
            State = SessionState.Starting;
            Status = "Starting…";
        }
        Send(Protocol.LaneStatus, Protocol.Status(1)); // Adopted, as player 1
        if (IsPcMode(pad.Format))
            KnownPads.SetMode(Id, pad.Format); // Asked for again next time
        else
            LeaveConsoleMode(pad);
    }

    public static bool IsPcMode(byte format) =>
        format is Protocol.FormatSInput or Protocol.FormatXInput or Protocol.FormatSlippi;

    // A console mode (Switch, Wii...) has no device on a PC: ask it for SInput, or the mode picked
    // for it here. It follows unless its mode was chosen on the gamepad itself.
    void LeaveConsoleMode(PadIdentity pad)
    {
        string mode = Protocol.FormatName(pad.Format);
        if (pad.ModeForced)
        {
            Fail("Can't open", $"{mode} mode was chosen on the gamepad: start it in a PC mode " +
                               "(SInput, XInput or Slippi)");
            return;
        }
        if (!IsPcMode(_wantedFormat))
            SetMode(Protocol.FormatSInput);
        Status = $"Switching from {mode} to {Protocol.FormatName(_wantedFormat)}…";
    }

    // A live input report: [length][report]
    void OnInput(ReadOnlySpan<byte> lengthAndReport, long arrivalUs)
    {
        int length = lengthAndReport[0];
        if (Pad == null || length > MaxReportBytes || lengthAndReport.Length - 1 < length)
            return;
        var report = lengthAndReport.Slice(1, length).ToArray();
        _packets++;

        switch (Format)
        {
            case Protocol.FormatXInput:
                OnXInput(report);
                break;

            case Protocol.FormatSInput:
                OnSInput(report);
                break;

            case Protocol.FormatSlippi:
                OnSlippi(report, arrivalUs);
                break;
        }
    }

    void OnXInput(byte[] report)
    {
        if (_deviceFormat == Protocol.FormatNone)
            StartDevice(_ctx.GetProfile(XInputProfile)!, Protocol.FormatXInput, firstReport: null);
        if (_deviceProfile != null && report.Length >= XInputReportSize)
            GiveInput(report[..XInputReportSize]);
    }

    void OnSInput(byte[] report)
    {
        if (_features == null && !_featuresAsked)
        {
            SendOutput(Personas.FeatureRequest());
            _featuresAsked = true;
            Status = "Asking for its features…";
        }
        if (_deviceFormat == Protocol.FormatNone && _features != null)
            StartDevice(Personas.SInput(Pad!), Protocol.FormatSInput, firstReport: _features);
        if (report.Length > 0)
            GiveInput(report);
    }

    void GiveInput(byte[] report)
    {
        _inputs.Enqueue(report);
        _wake.Set();
    }

    void OnSlippi(byte[] report, long arrivalUs)
    {
        if (_deviceFormat == Protocol.FormatNone)
        {
            _deviceFormat = Protocol.FormatSlippi;
            _gcPort = _adapter.Join(Id, SendOutput);
            if (_gcPort == null)
                Fail("No port free", "All four GameCube adapter ports are in use");
            else
                Status = "Creating the adapter…";
        }
        _gcPort?.Add(arrivalUs, report);
    }

    // An input report that had to arrive: a command reply, given to the host once
    void OnCommandReply(ReadOnlySpan<byte> report)
    {
        if (Format == Protocol.FormatSInput && _features == null && Personas.IsFeatureReply(report))
        {
            _features = report.ToArray(); // StartDevice hands it to the host first
            return;
        }
        _oneShots.Enqueue(report.ToArray());
        _wake.Set();
    }

    // A port on the adapter is open once the adapter is there
    void UpdateSlippiStatus()
    {
        if (_gcPort == null)
            return;
        if (_gcPort.Installing)
            Status = "Installing USB/IP…";
        else if (_gcPort.Ready && State != SessionState.Open)
        {
            State = SessionState.Open;
            Problem = null;
        }
        else if (!_gcPort.Ready && _gcPort.Problem is string problem)
            Fail("Couldn't open", problem);
    }

    void Fail(string status, string problem)
    {
        Status = status;
        Problem = problem;
    }

    // --- The virtual device ---

    void StartDevice(HMProfile profile, byte format, byte[]? firstReport)
    {
        _deviceFormat = format;
        _deviceProfile = profile;
        _doubleSubmit = !profile.RequiresUsbipBackend;
        Status = "Creating the controller…";

        // Each device gets its own ID: Steam can hold a removed device open, and reusing its ID
        // leaves the new one dead
        string identity = $"hhl-link-{Id}-{Environment.TickCount64:X}";
        Track(Task.Run(() => CreateDevice(profile, format, identity, firstReport)));
    }

    // In the background: it takes seconds
    void CreateDevice(HMProfile profile, byte format, string identity, byte[]? firstReport)
    {
        HMController created;
        try
        {
            // A USB device (the Xbox 360) needs the USB/IP transport: the first time on a PC it
            // installs, and USB devices blink once
            if (profile.RequiresUsbipBackend)
            {
                if (!HMContext.IsUsbipBackendAvailable)
                    Status = "Installing USB/IP…";
                Drivers.EnsureUsbIp();
            }
            Status = "Creating the controller…";
            created = _ctx.CreateController(profile, identity);
        }
        catch (Exception e)
        {
            Fail("Couldn't open", $"Couldn't create the controller: {e.Message}");
            return;
        }

        created.OutputReceived += OnOutput;
        lock (_submitLock)
        {
            if (State == SessionState.Closed || _deviceFormat != format)
            {
                // Closed or switched mode while it was being made
                created.OutputReceived -= OnOutput;
                created.Dispose();
                return;
            }
            _device = created;
            if (firstReport != null)
                _oneShots.Enqueue(firstReport); // Already there when the host first reads
        }

        State = SessionState.Open;
        Status = $"Open as {(format == Protocol.FormatXInput ? "Xbox 360" : "SInput")}";
        StartPump();
    }

    void RemoveDevice()
    {
        HMController? gone;
        lock (_submitLock)
        {
            gone = _device;
            _device = null;
            _deviceFormat = Protocol.FormatNone;
            _deviceProfile = null;
            _inputs.Clear();
            _oneShots.Clear();
        }
        _pumping = false;
        _wake.Set();
        _gcPort?.Leave();
        _gcPort = null;
        Problem = null;
        _features = null;
        _featuresAsked = false;

        if (gone != null)
        {
            gone.OutputReceived -= OnOutput;
            Track(Task.Run(gone.Dispose)); // Takes a few seconds
        }
    }

    void Track(Task work)
    {
        lock (_deviceWork)
        {
            _deviceWork.RemoveAll(t => t.IsCompleted);
            _deviceWork.Add(work);
        }
    }

    void WaitForDeviceWork(TimeSpan limit)
    {
        Task[] pending;
        lock (_deviceWork)
            pending = _deviceWork.ToArray();
        Task.WaitAll(pending, limit);
    }

    void OnOutput(HMController controller, HMOutputPacket packet)
    {
        if (packet.Source != HMOutputSource.HidOutput)
            return;

        var report = new byte[1 + packet.Data.Length];
        report[0] = packet.ReportId;
        packet.Data.Span.CopyTo(report.AsSpan(1));

        if (Personas.IsFeatureRequest(report) && _features != null)
        {
            _oneShots.Enqueue(_features); // Answered from the handshake
            _wake.Set();
            return;
        }
        SendOutput(report);
    }

    // Output reports (rumble, LEDs, SInput commands) go straight to the gamepad
    void SendOutput(byte[] report) =>
        Send(Protocol.LaneMessage, Protocol.Message(Protocol.MsgOutput, report));

    void StartPump()
    {
        _pumping = true;
        new Thread(Pump) { IsBackground = true, Priority = ThreadPriority.Highest, Name = $"Pump {Id}" }
            .Start();
    }

    // Woken as a report or reply arrives, the pump gives it to the device at once. Of several input
    // reports waiting, only the newest goes: the host would read the newest anyway, and on USB/IP a
    // waiting stale report would only hold the new one back.
    void Pump()
    {
        long holdInputUntilUs = 0;
        long quietUntilUs = _deviceFormat == Protocol.FormatXInput
            ? PeriodicClock.NowUs + XInputQuietStartMs * 1000
            : 0;
        while (_pumping)
        {
            long nowUs = PeriodicClock.NowUs;
            long heldUs = holdInputUntilUs - nowUs;
            _wake.WaitOne(heldUs > 0 ? (int)((heldUs + 999) / 1000) : PumpIdleMs);
            nowUs = PeriodicClock.NowUs;

            lock (_submitLock)
            {
                if (_device == null)
                    continue;
                if (nowUs < quietUntilUs)
                {
                    _inputs.Clear(); // Dropped, not saved for later
                    continue;
                }
                if (_oneShots.TryDequeue(out var reply))
                {
                    Submit(_device, reply);
                    holdInputUntilUs = nowUs + ReplyHoldMs * 1000;
                    if (!_oneShots.IsEmpty)
                        _wake.Set();
                    continue;
                }
                if (TakeNewestInput() is byte[] input)
                {
                    if (nowUs < holdInputUntilUs)
                        _inputs.Enqueue(input); // Waits out the reply's hold
                    else
                    {
                        Submit(_device, input);
                        _submitted++;
                    }
                }
            }
        }
    }

    byte[]? TakeNewestInput()
    {
        byte[]? newest = null;
        while (_inputs.TryDequeue(out var input))
            newest = input;
        return newest;
    }

    // Under _submitLock. HIDMaestro's HID driver hands the host every other frame, so there each
    // report goes in twice.
    void Submit(HMController device, byte[] report)
    {
        device.SubmitRawExtendedReport(report);
        if (_doubleSubmit)
            device.SubmitRawExtendedReport(report);
    }
}
