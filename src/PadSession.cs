// One gamepad in Link mode on this PC: tells it what this PC wants (mode and report rate, every
// second, which also tells it the app is here), presents it as the device for its mode, and plays
// its samples to that device at the spacing the gamepad took them (see Playout). In Slippi mode the
// device is a port on the GameCube adapter that every Slippi gamepad here shares (see GcAdapter).
//
// Threads: the device's reader thread calls OnReport and the hub's thread Heartbeat and Tick (all
// under the hub's lock); HIDMaestro raises output reports on its own thread (they go straight out);
// the report clock submits to the device; creating and removing the device run in the background,
// because each takes seconds.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using HIDMaestro;

namespace HhlGamepadLink;

enum SessionState { Opening, Starting, Open, Closed }

// Why a session ended: only a gamepad that went away is opened again when it comes back
enum CloseReason { None, Closed, WentAway }

sealed class PadSession
{
    // XInput mode's device: a wired Xbox 360 controller as the USB device it is, on the USB/IP
    // transport, so Windows' own xusb22 drives it and every API sees one controller. HIDMaestro's
    // Xbox profiles pair a HID device with an XInput companion, and Steam listed those as two pads
    // (XInput and GameInput each reported it). The gamepad's XInput report is the 360's USB report,
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

    readonly HMContext _ctx;
    readonly GcAdapter _adapter;
    LinkDevice? _link; // The gamepad's Link collection; the hub opens it
    long _lastReportMs = Environment.TickCount64;
    public long SilentMs => Environment.TickCount64 - _lastReportMs;

    public string Id { get; }                     // The gamepad's Bluetooth address
    public PadIdentity? Pad { get; private set; } // Its identity, once it has said who it is
    public SessionState State { get; private set; } = SessionState.Opening;
    public string Status { get; private set; } = "Opening…"; // A few words: it fits beside the button
    public string? Problem { get; private set; }               // Why it couldn't open, for the details line
    public CloseReason Reason { get; private set; } = CloseReason.None;

    int _rateHz;

    // The device and what the report clock submits (under _submitLock)
    readonly object _submitLock = new();
    HMController? _device;
    byte _deviceFormat = 0xFF;
    HMProfile? _deviceProfile;
    readonly Playout<byte[]> _reports = new(); // The gamepad's input reports, as the device takes them
    bool _doubleSubmit; // HIDMaestro's HID driver hands the host every other frame; USB/IP devices don't
    readonly ConcurrentQueue<byte[]> _oneShots = new();
    GcAdapter.Port? _gcPort;
    Thread? _pump;
    volatile bool _pumping;

    // Devices being created or removed in the background, so closing the app can wait for them
    readonly List<Task> _deviceWork = new();

    // SInput handshake: the gamepad's features, asked for once it streams, kept, and given to the
    // host whenever it asks, so SDL never waits on the radio
    byte[]? _features;
    bool _featuresAsked;

    // The config app through ConfigServer: replies and the live view, as WebUSB packets
    public event Action<byte[]>? ConfigFromGamepad;

    // The gamepad's clock (32-bit microseconds), unwrapped
    long _sampledBase;
    uint _lastSampled;
    bool _haveSampled;

    // Rates shown in the list
    int _packets, _submitted;
    long _rateWindowStart;
    public int PacketsPerSecond { get; private set; }
    public int ReportsPerSecond { get; private set; }
    public int? AdapterPort => _gcPort?.Number;
    public double BufferMs => Timing.DelayUs / 1000.0;
    public double LatePercent => Timing.LateShare * 100;

    (long DelayUs, double LateShare) Timing => _deviceFormat switch
    {
        Protocol.FormatSlippi when _gcPort != null => (_gcPort.Playout.DelayUs, _gcPort.Playout.LateShare),
        _ => (_reports.DelayUs, _reports.LateShare),
    };

    public PadSession(HMContext ctx, GcAdapter adapter, string id, int rateHz)
    {
        _ctx = ctx;
        _adapter = adapter;
        Id = id;
        _rateHz = rateHz;
    }

    public void Attach(LinkDevice link)
    {
        _link = link;
        SendHost(); // The gamepad waits for it before it says who it is
    }

    // Every second: the gamepad (re)starts on it, and goes back to a dongle without it
    public void Heartbeat() => SendHost();

    // The mode this PC wants the gamepad in, for this run (0 for none): it follows unless its mode
    // was chosen on the gamepad itself
    byte _wantedFormat;

    public byte Format => Pad?.Format ?? 0xFF; // The mode its reports are in
    public byte? WantedFormat => _wantedFormat == 0 ? null : _wantedFormat;

    public void SetMode(byte format)
    {
        _wantedFormat = format;
        SendHost();
    }

    public void SetRate(int rateHz)
    {
        _rateHz = rateHz;
        SendHost();
    }

    void SendHost() => Send(Protocol.LaneMessage, Protocol.Host(_wantedFormat, Updates.CurrentNumber, _rateHz));

    // A disconnected gamepad's writes fail: its silence says it is away, not this. The hub stops the
    // host info to a gamepad disconnected here.
    void Send(byte lane, ReadOnlySpan<byte> payload) => _link?.Send(lane, payload);

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
                OnInput(BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(3)), report.AsSpan(7), arrivalUs);
            break;
        case Protocol.ReportMessage:
            int len = Math.Min(report[2], report.Length - 3);
            var payload = report.AsSpan(3, len);
            if (report[1] == Protocol.LaneBulk && State != SessionState.Closed)
                ConfigFromGamepad?.Invoke(payload.ToArray()); // The config app's live view
            else if (report[1] == Protocol.LaneMessage && len >= 1)
                OnMessage(payload[0], payload.Slice(1));
            break;
        }
    }

    void OnMessage(byte type, ReadOnlySpan<byte> p)
    {
        switch (type)
        {
        case Protocol.MsgWake:
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
            if (_deviceFormat != 0xFF && pad.Format != _deviceFormat)
                RemoveDevice();
            Pad = pad;
            if (State == SessionState.Opening)
            {
                State = SessionState.Starting;
                Status = "Starting…";
            }
            Send(Protocol.LaneStatus, Protocol.Status(1)); // Adopted, as player 1
            if (pad.Format is not (Protocol.FormatSInput or Protocol.FormatXInput or Protocol.FormatSlippi))
                Fail("Can't open", $"{Protocol.FormatName(pad.Format)} mode isn't supported on a PC");
            break;
        case Protocol.MsgReport:
            OnCommandReply(p.Slice(0, Math.Min(p.Length, 64)));
            break;
        case Protocol.MsgConfig:
            ConfigFromGamepad?.Invoke(p.ToArray());
            break;
        }
    }

    // A live input report: [length][report], sampled at the gamepad's time
    void OnInput(uint sampled, ReadOnlySpan<byte> lengthAndReport, long arrivalUs)
    {
        int full = lengthAndReport[0];
        if (Pad == null || full > 64 || lengthAndReport.Length - 1 < full)
            return;
        var report = lengthAndReport.Slice(1, full).ToArray();

        if (_haveSampled && sampled < _lastSampled && _lastSampled - sampled > 0x80000000u)
            _sampledBase += 1L << 32;
        _lastSampled = sampled;
        _haveSampled = true;
        long sampledUs = _sampledBase + sampled;
        _packets++;

        switch (Format)
        {
        case Protocol.FormatXInput:
            if (_deviceFormat == 0xFF)
                StartDevice(_ctx.GetProfile(XInputProfile)!, Protocol.FormatXInput, firstReport: null);
            if (_deviceProfile != null && report.Length >= XInputReportSize)
                _reports.Add(arrivalUs, sampledUs, report[..XInputReportSize]);
            break;
        case Protocol.FormatSInput:
            OnSInput(report, arrivalUs, sampledUs);
            break;
        case Protocol.FormatSlippi:
            if (_deviceFormat == 0xFF)
            {
                _deviceFormat = Protocol.FormatSlippi;
                _gcPort = _adapter.Join(Id, SendOutput);
                if (_gcPort == null)
                    Fail("No port free", "All four GameCube adapter ports are in use");
                else
                    Status = "Creating the adapter…";
            }
            _gcPort?.Add(arrivalUs, sampledUs, report);
            break;
        }
    }

    void OnSInput(byte[] report, long arrivalUs, long sampledUs)
    {
        if (_features == null && !_featuresAsked)
        {
            SendOutput(Personas.FeatureRequest());
            _featuresAsked = true;
            Status = "Asking for its features…";
        }
        if (_deviceFormat == 0xFF && _features != null)
            StartDevice(Personas.SInput(Pad!), Protocol.FormatSInput, firstReport: _features);
        if (report.Length > 0)
            _reports.Add(arrivalUs, sampledUs, report);
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

    // Called from the session thread every few ms: status and rates
    public void Tick()
    {
        if (State == SessionState.Closed)
            return;
        long now = Environment.TickCount64;
        UpdateSlippiStatus();
        if (now - _rateWindowStart >= 1000)
        {
            PacketsPerSecond = _packets;
            ReportsPerSecond = _submitted;
            _packets = _submitted = 0;
            _rateWindowStart = now;
        }
    }

    // ---- The config app ----

    // The link is reliable and in order: config commands go straight out
    public void SendConfig(byte[] packet) => Send(Protocol.LaneMessage, Protocol.Message(Protocol.MsgConfig, packet));

    public void Close(string why, CloseReason reason)
    {
        if (State == SessionState.Closed)
            return;
        State = SessionState.Closed;
        Status = why;
        Reason = reason;
        RemoveDevice();
    }

    // ---- The virtual device ----

    void StartDevice(HMProfile profile, byte format, byte[]? firstReport)
    {
        _deviceFormat = format;
        _deviceProfile = profile;
        _doubleSubmit = !profile.RequiresUsbipBackend;
        Status = "Creating the controller…";

        // Each device gets its own ID: Steam can hold a removed device open, and reusing its ID
        // leaves the new one dead
        string identity = $"hhl-link-{Id}-{Environment.TickCount64:X}";
        Track(Task.Run(() =>
        {
            HMController created;
            try
            {
                // A USB device (the Xbox 360) needs the USB/IP transport: the first time on a PC it
                // installs, and USB devices blink once
                if (profile.RequiresUsbipBackend && !HMContext.IsUsbipBackendAvailable)
                    Status = "Installing USB/IP…";
                if (profile.RequiresUsbipBackend)
                    Drivers.EnsureUsbIp();
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
        }));
    }

    void RemoveDevice()
    {
        HMController? gone;
        lock (_submitLock)
        {
            gone = _device;
            _device = null;
            _deviceFormat = 0xFF;
            _deviceProfile = null;
            _reports.Clear();
            _oneShots.Clear();
        }
        _pumping = false;
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

    // Wait until this session's devices are made and gone: the app calls it when closing
    public void WaitForDevices(TimeSpan limit)
    {
        Task[] pending;
        lock (_deviceWork)
            pending = _deviceWork.ToArray();
        Task.WaitAll(pending, limit);
        RemoveDevice(); // One that finished being made while closing
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
            return;
        }
        SendOutput(report);
    }

    // Output reports (rumble, LEDs, SInput commands) go straight to the gamepad
    void SendOutput(byte[] report) => Send(Protocol.LaneMessage, Protocol.Message(Protocol.MsgOutput, report));

    // Command replies go to the host at once; samples when their time comes. The driver delivers
    // every other frame on this path, so each one is submitted twice.
    void StartPump()
    {
        _pumping = true;
        _pump = new Thread(() =>
        {
            using var timer = new PeriodicClock(1);
            long holdInputUntil = 0;
            long quietUntil = _deviceFormat == Protocol.FormatXInput
                ? PeriodicClock.NowUs + XInputQuietStartMs * 1000
                : 0;
            while (_pumping)
            {
                long now = PeriodicClock.NowUs;
                long due = _reports.NextDueUs();
                long wake = Math.Max(Math.Min(due, now + 1_000), holdInputUntil); // Checks replies every 1 ms
                if (wake > now)
                    timer.WaitUntilUs(wake);
                now = PeriodicClock.NowUs;

                lock (_submitLock)
                {
                    if (_device == null)
                        continue;
                    if (now < quietUntil)
                    {
                        while (_reports.TryTakeDue(now, out _)) { } // Dropped, not saved for later
                        continue;
                    }
                    if (_oneShots.TryDequeue(out var reply))
                    {
                        _device.SubmitRawExtendedReport(reply);
                        if (_doubleSubmit)
                            _device.SubmitRawExtendedReport(reply);
                        holdInputUntil = now + ReplyHoldMs * 1000;
                    }
                    else if (now >= holdInputUntil)
                    {
                        if (_reports.TryTakeDue(now, out var input))
                        {
                            _device.SubmitRawExtendedReport(input);
                            if (_doubleSubmit)
                                _device.SubmitRawExtendedReport(input);
                            _submitted++;
                        }
                    }
                }
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = $"Playout {Id}" };
        _pump.Start();
    }
}
