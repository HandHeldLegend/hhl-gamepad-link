// One gamepad opened on this PC: keeps it open, presents it as the device for its mode, and plays
// its samples to that device at the spacing the gamepad took them (see Playout).
//
// Threads: the network thread calls OnData and Tick; HIDMaestro raises output reports on its own
// thread (they are only queued); the report clock submits to the device; creating and removing the
// device run in the background, because each takes seconds.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using HIDMaestro;

namespace HhlGamepadWlan;

enum SessionState { Opening, Starting, Open, Closed }

// Why a session ended: only a gamepad that went away is opened again when it comes back
enum CloseReason { None, Closed, Taken, WentAway }

sealed class PadSession
{
    const long KeepAliveMs = 500;
    const long GamepadLostMs = 3000;
    const long OutResendMs = 8;

    // The driver hands the host the newest frame, so after a command reply the input waits briefly:
    // otherwise the next input can replace the reply before the host reads it
    const long ReplyHoldMs = 20;

    readonly HMContext _ctx;
    readonly Action<byte[], EndPoint> _send;
    readonly uint _session;
    readonly string _pcName;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public Beacon Pad { get; private set; }
    public EndPoint Address { get; private set; }
    public SessionState State { get; private set; } = SessionState.Opening;
    public string Status { get; private set; } = "Opening…";
    public CloseReason Reason { get; private set; } = CloseReason.None;

    int _rateHz;
    long _lastOpenSent = -KeepAliveMs, _lastPacket, _lastOutSent;

    // The device and what the report clock submits (under _submitLock)
    readonly object _submitLock = new();
    HMController? _device;
    byte _deviceFormat = 0xFF;
    HMProfile? _deviceProfile;
    readonly Playout<byte[]> _sinput = new();
    readonly Playout<HMGamepadState> _xinput = new();
    readonly ConcurrentQueue<byte[]> _oneShots = new();
    Thread? _pump;
    volatile bool _pumping;

    // Devices being created or removed in the background, so closing the app can wait for them
    readonly List<Task> _deviceWork = new();

    // SInput handshake: the gamepad's features, asked for once it streams, kept, and given to the
    // host whenever it asks, so SDL never waits on the network
    byte[]? _features;
    bool _featuresAsked;
    ushort _lastReliable;

    // Output reports and config commands to the gamepad, one at a time, resent until acked.
    // Config commands go first and are never dropped; output reports keep only the newest.
    readonly ConcurrentQueue<byte[]> _outputs = new();
    readonly ConcurrentQueue<byte[]> _config = new();
    byte[]? _outPending;
    uint _outPendingMagic;

    // The config app through ConfigServer: replies and the live view, as WebUSB packets
    public event Action<byte[]>? ConfigFromGamepad;
    ushort _lastReply;
    ushort _outToken, _outInFlight;

    // Rates shown in the list
    int _packets, _submitted;
    long _rateWindowStart;
    public int PacketsPerSecond { get; private set; }
    public int ReportsPerSecond { get; private set; }
    public double RttMs { get; private set; }
    public double BufferMs => (_deviceFormat == Protocol.FormatXInput ? _xinput.DelayUs : _sinput.DelayUs) / 1000.0;
    public double LatePercent => (_deviceFormat == Protocol.FormatXInput ? _xinput.LateShare : _sinput.LateShare) * 100;

    public PadSession(HMContext ctx, Beacon pad, EndPoint address, uint session, string pcName, int rateHz,
                      Action<byte[], EndPoint> send)
    {
        _ctx = ctx;
        Pad = pad;
        Address = address;
        _session = session;
        _pcName = pcName;
        _rateHz = rateHz;
        _send = send;
        _lastPacket = _clock.ElapsedMilliseconds;
    }

    // One sample per report: the gamepad sends at the report rate and each sample is played once
    static uint WlanPeriodUs(int rateHz) => (uint)(1_000_000 / rateHz);

    public void SetRate(int rateHz)
    {
        _rateHz = rateHz;
        _lastOpenSent = -KeepAliveMs; // Tell the gamepad now
    }

    public void OnBeacon(Beacon pad, EndPoint address)
    {
        Pad = pad;
        Address = address;

        // Another PC took it: its new owner's session is in the beacon
        if (State != SessionState.Opening && pad.OwnerSession != 0 && pad.OwnerSession != _session)
            Close($"Taken by {pad.OwnerName}", CloseReason.Taken, sendClose: false);
    }

    public void OnData(in DataPacket packet)
    {
        if (State == SessionState.Closed)
            return;

        _lastPacket = _clock.ElapsedMilliseconds;
        _packets++;
        RttMs = packet.RttUs / 1000.0;
        if (State == SessionState.Opening)
        {
            State = SessionState.Starting;
            Status = "Starting…";
        }

        if (_outPending != null && packet.OutAck == _outInFlight)
            _outPending = null;

        // The gamepad changed mode: its device goes, and the one for the new mode comes up
        if (_deviceFormat != 0xFF && packet.Format != _deviceFormat)
            RemoveDevice();

        if (packet.Format == Protocol.FormatXInput)
            OnXInput(packet);
        else if (packet.Format == Protocol.FormatSInput)
            OnSInput(packet);
        else
            Status = $"{Protocol.FormatName(packet.Format)} mode isn't supported over the LAN yet";
    }

    void OnXInput(in DataPacket packet)
    {
        if (_deviceFormat == 0xFF)
            StartDevice(_ctx.GetProfile("xbox-360-wired")!, Protocol.FormatXInput, firstReport: null);

        if (_deviceProfile != null && packet.Report.Length >= 14)
        {
            _xinput.Add(PeriodicClock.NowUs, packet.SentUs, Personas.XInput(_deviceProfile, packet.Report));
        }
    }

    void OnSInput(in DataPacket packet)
    {
        if (_features == null)
        {
            if (!_featuresAsked)
            {
                _outputs.Clear();
                _outPending = null;
                _outputs.Enqueue(Personas.FeatureRequest());
                _featuresAsked = true;
                Status = "Asking for its features…";
            }
            if (packet.ReliableToken != 0 && Personas.IsFeatureReply(packet.Report))
            {
                _features = packet.Report.ToArray();
                _lastReliable = packet.ReliableToken;
            }
        }

        if (_deviceFormat == 0xFF && _features != null)
            StartDevice(Personas.SInput(Pad), Protocol.FormatSInput, firstReport: _features);

        // A resent command reply goes to the host once; input is played at its own time
        if (packet.ReliableToken != 0 && packet.ReliableToken != _lastReliable)
        {
            _lastReliable = packet.ReliableToken;
            _oneShots.Enqueue(packet.Report.ToArray());
        }
        else if (packet.ReliableToken == 0 && packet.Report.Length > 0)
        {
            _sinput.Add(PeriodicClock.NowUs, packet.SentUs, packet.Report.ToArray());
        }
    }

    // Called from the network thread every pass: keep it open, send outputs, notice silence
    public void Tick()
    {
        if (State == SessionState.Closed)
            return;
        long now = _clock.ElapsedMilliseconds;

        if (now - _lastOpenSent >= KeepAliveMs)
        {
            _send(Protocol.Open(WlanPeriodUs(_rateHz), _session, _pcName), Address);
            _lastOpenSent = now;
        }

        if (now - _lastPacket >= GamepadLostMs)
        {
            Close("Gamepad went quiet", CloseReason.WentAway, sendClose: false);
            return;
        }

        if (_outPending == null && _config.TryDequeue(out var command))
        {
            _outPending = command;
            _outPendingMagic = Protocol.MagicConfig;
            _outToken = (ushort)(_outToken == ushort.MaxValue ? 1 : _outToken + 1);
            _outInFlight = _outToken;
            _lastOutSent = -OutResendMs;
        }
        else if (_outPending == null && _outputs.TryDequeue(out var next))
        {
            _outPending = next;
            _outPendingMagic = Protocol.MagicOut;
            _outToken = (ushort)(_outToken == ushort.MaxValue ? 1 : _outToken + 1);
            _outInFlight = _outToken;
            _lastOutSent = -OutResendMs;
        }
        if (_outPending != null && now - _lastOutSent >= OutResendMs)
        {
            _send(Protocol.Message(_outPendingMagic, _outInFlight, _outPending), Address);
            _lastOutSent = now;
        }

        if (now - _rateWindowStart >= 1000)
        {
            PacketsPerSecond = _packets;
            ReportsPerSecond = _submitted;
            _packets = _submitted = 0;
            _rateWindowStart = now;
        }
    }

    // ---- The config app ----

    public void SendConfig(byte[] packet)
    {
        if (State != SessionState.Closed)
            _config.Enqueue(packet);
    }

    // A config reply: the gamepad resends it until echoed, so pass each token on once
    public void OnConfigReply(ushort token, byte[] packet)
    {
        if (token == 0 || token == _lastReply)
            return;
        _lastReply = token;
        ConfigFromGamepad?.Invoke(packet);
    }

    public void OnLive(byte[] packet) => ConfigFromGamepad?.Invoke(packet);

    public void Close(string why, CloseReason reason, bool sendClose = true)
    {
        if (State == SessionState.Closed)
            return;
        if (sendClose)
            _send(Protocol.Close(_session), Address);
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
        Status = "Creating the controller…";

        // Each device gets its own ID: Steam can hold a removed device open, and reusing its ID
        // leaves the new one dead
        string identity = $"hhl-wlan-{Pad.Id}-{Environment.TickCount64:X}";
        Track(Task.Run(() =>
        {
            HMController created;
            try
            {
                created = _ctx.CreateController(profile, identity);
            }
            catch (Exception e)
            {
                Status = $"Couldn't create the controller: {e.Message}";
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
            _sinput.Clear();
            _xinput.Clear();
            _oneShots.Clear();
        }
        _pumping = false;
        _features = null;
        _featuresAsked = false;
        _outputs.Clear();
        _config.Clear();
        _outPending = null;

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
        if (packet.Source == HMOutputSource.XInput)
        {
            if (Personas.XInputRumble(packet.Data.Span) is byte[] rumble)
                QueueOutput(rumble);
            return;
        }
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
        QueueOutput(report);
    }

    void QueueOutput(byte[] report)
    {
        _outputs.Enqueue(report);
        while (_outputs.Count > 16 && _outputs.TryDequeue(out byte[]? dropped)) { } // A stalled link keeps the newest
    }

    // Command replies go to the host at once; samples when their time comes. The driver delivers
    // every other frame on this path, so each one is submitted twice.
    void StartPump()
    {
        _pumping = true;
        _pump = new Thread(() =>
        {
            using var timer = new PeriodicClock(1);
            long holdInputUntil = 0;
            while (_pumping)
            {
                long now = PeriodicClock.NowUs;
                long due = Math.Min(_sinput.NextDueUs(), _xinput.NextDueUs());
                long wake = Math.Max(Math.Min(due, now + 1_000), holdInputUntil); // Checks replies every 1 ms
                if (wake > now)
                    timer.WaitUntilUs(wake);
                now = PeriodicClock.NowUs;

                lock (_submitLock)
                {
                    if (_device == null)
                        continue;
                    if (_oneShots.TryDequeue(out var reply))
                    {
                        _device.SubmitRawExtendedReport(reply);
                        _device.SubmitRawExtendedReport(reply);
                        holdInputUntil = now + ReplyHoldMs * 1000;
                    }
                    else if (now >= holdInputUntil)
                    {
                        if (_xinput.TryTakeDue(now, out var state))
                        {
                            _device.SubmitState(state);
                            _device.SubmitState(state);
                            _submitted++;
                        }
                        else if (_sinput.TryTakeDue(now, out var input))
                        {
                            _device.SubmitRawExtendedReport(input);
                            _device.SubmitRawExtendedReport(input);
                            _submitted++;
                        }
                    }
                }
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = $"Playout {Pad.Name}" };
        _pump.Start();
    }
}
