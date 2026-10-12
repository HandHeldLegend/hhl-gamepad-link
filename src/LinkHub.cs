// The gamepads in Link mode on this PC: each one's Link HID collection (HidLink.cs), opened as it
// appears. Windows keeps a paired gamepad's collection present while the gamepad is away, so what
// says a gamepad is here is its reports: the host info goes to every one each second (the gamepad
// waits for it, and treats three silent seconds as the app gone), and one that stops reporting is
// away until it reports again. A gamepad disconnected here gets no host info: within a few seconds
// it goes back to looking for a dongle.

using HIDMaestro;

namespace HhlGamepadLink;

// A gamepad as the list shows it. Away: it was here and has gone quiet. Refused: disconnected here.
sealed record PadEntry(PadIdentity Pad, PadSession? Session, bool Away, bool Refused);

sealed class LinkHub : IDisposable
{
    const long HeartbeatMs = 1000;
    const long AwayAfterMs = 2000; // No report this long: the gamepad is away (the link reports far faster)

    readonly HMContext _ctx;
    readonly GcAdapter _adapter;
    readonly Thread _thread;
    volatile bool _running = true;

    readonly object _lock = new();
    readonly Dictionary<string, LinkDevice> _devices = new();   // Open collections, by path
    readonly Dictionary<string, PadSession> _sessions = new();  // By gamepad (Link address)
    readonly HashSet<string> _refused = new();
    readonly Dictionary<string, byte> _modes = new();           // Picked here this run, by gamepad

    public int RateHz { get; private set; }

    public LinkHub(HMContext ctx, int rateHz)
    {
        _ctx = ctx;
        _adapter = new GcAdapter(ctx);
        RateHz = rateHz;
        _thread = new Thread(Run) { IsBackground = true, Name = "Link" };
        _thread.Start();
    }

    public IReadOnlyList<PadEntry> Snapshot()
    {
        lock (_lock)
        {
            return _sessions.Values
                .Where(s => s.Pad != null)
                .Select(s => new PadEntry(s.Pad!, s, s.State == SessionState.Closed && s.Reason == CloseReason.WentAway,
                                          _refused.Contains(s.Id)))
                .OrderBy(e => e.Pad.Title).ThenBy(e => e.Pad.Id)
                .ToList();
        }
    }

    void Run()
    {
        long nextScan = 0, nextBeat = 0;
        while (_running)
        {
            long now = Environment.TickCount64;
            if (now >= nextScan)
            {
                Scan();
                nextScan = now + 1000;
            }
            lock (_lock)
            {
                if (now >= nextBeat)
                {
                    foreach (var s in _sessions.Values)
                        if (!_refused.Contains(s.Id))
                            s.Heartbeat();
                    nextBeat = now + HeartbeatMs;
                }
                foreach (var s in _sessions.Values)
                {
                    s.Tick();
                    if (s.State != SessionState.Closed && s.SilentMs > AwayAfterMs && s.Pad != null)
                        s.Close("Gamepad went away", CloseReason.WentAway);
                }
                _adapter.Tick();
            }
            Thread.Sleep(5);
        }
    }

    // New Link collections: open each and give it a session; gone ones close
    void Scan()
    {
        var present = LinkDevice.FindPaths();
        lock (_lock)
        {
            foreach (var path in _devices.Keys.Except(present).ToList())
            {
                _devices.Remove(path, out var gone);
                gone!.Dispose();
            }
        }
        foreach (var path in present)
        {
            lock (_lock)
                if (_devices.ContainsKey(path))
                    continue;
            var device = LinkDevice.Open(path);
            if (device == null)
                continue;
            lock (_lock)
            {
                _devices[path] = device;
                if (!_sessions.TryGetValue(device.Id, out var session))
                {
                    session = new PadSession(_ctx, _adapter, device.Id, RateHz);
                    if (_modes.TryGetValue(device.Id, out byte format))
                        session.SetMode(format);
                    _sessions[device.Id] = session;
                }
                session.Attach(device);
            }
            new Thread(() => ReadLoop(device)) { IsBackground = true, Priority = ThreadPriority.AboveNormal,
                                                 Name = $"Link {device.Id}" }.Start();
        }
    }

    void ReadLoop(LinkDevice device)
    {
        while (_running && !device.Closed)
        {
            var report = device.Read();
            if (report == null)
                break;
            long arrivalUs = PeriodicClock.NowUs;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(device.Id, out var session) || _refused.Contains(device.Id))
                    continue;
                try { session.OnReport(report, arrivalUs); }
                catch (Exception e)
                {
                    // One gamepad's fault closes that gamepad, not the app
                    session.Close($"Error: {e.Message}", CloseReason.Closed);
                }
            }
        }
    }

    // Disconnect a gamepad: no more host info, so it goes back to looking for a dongle
    public void Close(string id)
    {
        lock (_lock)
        {
            _refused.Add(id);
            if (_sessions.TryGetValue(id, out var session))
                session.Close("Closed", CloseReason.Closed);
        }
    }

    // Let a gamepad disconnected here connect again: it does when it next calls this PC
    public void Allow(string id)
    {
        lock (_lock)
            _refused.Remove(id);
    }

    // Drop a gamepad that went away from the list
    public void Forget(string id)
    {
        lock (_lock)
        {
            _refused.Remove(id);
            if (_sessions.Remove(id, out var session))
                session.Close("Forgotten", CloseReason.Closed);
        }
    }

    // Put a gamepad in a mode for the rest of this run: now if it is here, and each time it comes back
    public void SetMode(string id, byte format)
    {
        lock (_lock)
        {
            _modes[id] = format;
            if (_sessions.TryGetValue(id, out var session))
                session.SetMode(format);
        }
    }

    public void SetRate(int rateHz)
    {
        lock (_lock)
        {
            RateHz = rateHz;
            foreach (var session in _sessions.Values)
                session.SetRate(rateHz);
        }
    }

    // Close every gamepad here and wait until their devices are gone: before a driver is removed
    public void CloseAll()
    {
        PadSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            foreach (var session in sessions)
                session.Close("Closed", CloseReason.Closed);
        }
        foreach (var session in sessions)
            session.WaitForDevices(TimeSpan.FromSeconds(30));
        _adapter.RemoveNow();
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(500);
        CloseAll();
        lock (_lock)
        {
            foreach (var d in _devices.Values)
                d.Dispose();
            _devices.Clear();
        }
        _adapter.Dispose();
    }
}
