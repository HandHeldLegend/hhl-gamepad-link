// The gamepads in Link mode on this PC: each one's Link HID collection (HidLink.cs), opened as it
// appears. What says a gamepad is here is its reports: the host info goes to every one each second
// (the gamepad waits for it, and treats three silent seconds as the app gone), and one that stops
// reporting is away until it reports again. A gamepad disconnected here gets no host info until it
// has gone quiet (it went back to looking for a dongle); after that it is welcome again, when it is
// next started in dongle mode.

using HIDMaestro;

namespace HhlGamepadLink;

sealed class LinkHub : IDisposable
{
    const long HeartbeatMs = 1000;
    const long AwayAfterMs = 2000;  // No report this long: the gamepad is away (the link reports far faster)
    const long ScanEveryMs = 2000;
    const int LoopSleepMs = 5;
    const int PairSeconds = 45;     // Paging a gamepad that didn't pair on its own link

    // Advertising every 1 s, but a busy radio scans poorly: 10-45 s between sightings measured
    static readonly TimeSpan NearbyFor = TimeSpan.FromSeconds(90);
    static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(30);

    readonly HMContext _ctx;
    readonly GcAdapter _adapter;
    readonly Caller _caller = new();
    readonly Thread _thread;
    volatile bool _running = true;

    readonly object _lock = new();
    readonly Dictionary<string, LinkDevice> _devices = new();   // Open collections, by path
    readonly Dictionary<string, PadSession> _sessions = new();  // By gamepad (Link address)
    readonly HashSet<string> _refused = new();                  // Disconnected here, until they go quiet
    readonly Dictionary<string, byte> _modes = new();           // Picked here this run, by gamepad
    readonly HashSet<string> _pairing = new();                  // Being paired now
    HashSet<string> _windowsPaired = new();                     // Known gamepads Windows holds a pairing for

    public LinkHub(HMContext ctx, int rateHz)
    {
        _ctx = ctx;
        _adapter = new GcAdapter(ctx);
        RateHz = rateHz;
        _thread = new Thread(Run) { IsBackground = true, Name = "Link" };
        _thread.Start();
    }

    public int RateHz { get; private set; }

    public IReadOnlyList<PadEntry> Snapshot()
    {
        lock (_lock)
        {
            var entries = _sessions.Values.Where(s => s.Pad != null).Select(SessionEntry).ToList();
            // Gamepads advertising nearby that aren't here this run: linked here before (call them
            // over), or not (pair them)
            foreach (var (id, seen) in _caller.Nearby(NearbyFor))
                if (!_sessions.ContainsKey(id))
                    entries.Add(NearbyEntry(id, seen));
            return entries.OrderBy(e => e.UserName.Length > 0 ? e.UserName : e.Pad.Name)
                          .ThenBy(e => e.Pad.Id)
                          .ToList();
        }
    }

    // Pair a gamepad seen nearby (if it isn't in use): it pages this PC, which pairs it; then it
    // restarts in Link mode, which connects and hands over its call token. The card shows each step.
    public void Pair(string id)
    {
        lock (_lock)
            if (!_pairing.Add(id))
                return;
        _caller.SetStatus(id, "Pairing…");
        _ = Task.Run(() =>
        {
            string? error = Pairing.PairAsked(id, () => _caller.AskToPairAsync(id).GetAwaiter().GetResult(),
                                              PairSeconds, step => _caller.SetStatus(id, step));
            _caller.SetStatus(id, error ?? "Paired: connecting…");
            if (error != null)
                Log.Write($"Pairing {id}: {error}");
            lock (_lock)
                _pairing.Remove(id);
        });
    }

    // Call a gamepad over from wherever it is connected (it must have been in Link mode here once)
    public void Call(string id)
    {
        if (!Pairing.IsPaired(id))
        {
            Pair(id); // Its pairing went from Windows: set it up again
            return;
        }
        if (KnownPads.Get(id) is KnownPads.Pad known)
        {
            lock (_lock)
                _refused.Remove(id);
            _caller.Call(id, known.Token);
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

    // Drop a gamepad from the list, and its call token: it is called over no more
    public void Forget(string id)
    {
        lock (_lock)
        {
            _refused.Remove(id);
            if (_sessions.Remove(id, out var session))
                session.Close("Forgotten", CloseReason.Closed);
        }
        KnownPads.Forget(id);
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
            session.WaitForDevices(CloseWait);
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
        _caller.Dispose();
        _adapter.Dispose();
    }

    // A gamepad that has been here this run
    PadEntry SessionEntry(PadSession s)
    {
        bool here = s.State != SessionState.Closed;
        var known = KnownPads.Get(s.Id);
        return new PadEntry(s.Pad!, s, !here && s.Reason == CloseReason.WentAway, _refused.Contains(s.Id),
                            here ? null : _caller.Seen(s.Id, NearbyFor), _caller.CallStatus(s.Id),
                            known != null && _windowsPaired.Contains(s.Id), known?.UserName ?? "");
    }

    PadEntry NearbyEntry(string id, Caller.Sighting seen)
    {
        var known = KnownPads.Get(id);
        string name = seen.Name.Length > 0 ? seen.Name : known?.Name ?? "HOJA gamepad";
        string userName = seen.UserName.Length > 0 ? seen.UserName : known?.UserName ?? "";
        var pad = new PadIdentity(id, name, 0, 0, seen.Format);
        return new PadEntry(pad, null, false, false, seen, _caller.CallStatus(id),
                            known != null && _windowsPaired.Contains(id), userName);
    }

    void Run()
    {
        long nextScanMs = 0, nextBeatMs = 0;
        while (_running)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs >= nextScanMs)
            {
                try { Scan(); }
                catch (Exception e) { Log.Write($"Link scan: {e}"); }
                RefreshWindowsPaired();
                nextScanMs = nowMs + ScanEveryMs;
            }
            lock (_lock)
            {
                if (nowMs >= nextBeatMs)
                {
                    foreach (var s in _sessions.Values)
                        if (!_refused.Contains(s.Id))
                            Guard(s, s.Heartbeat);
                    nextBeatMs = nowMs + HeartbeatMs;
                }
                TickSessions();
                _adapter.Tick();
            }
            Thread.Sleep(LoopSleepMs);
        }
    }

    // A pairing removed in Windows' settings: the gamepad needs pairing again, not a call
    void RefreshWindowsPaired()
    {
        var paired = KnownPads.All().Where(k => Pairing.IsPaired(k.Id)).Select(k => k.Id).ToHashSet();
        lock (_lock)
            _windowsPaired = paired;
    }

    // Under _lock: releases quiet refused gamepads, and closes ones gone away
    void TickSessions()
    {
        // Released: once a gamepad disconnected here has gone quiet, it may come back
        _refused.RemoveWhere(id => _sessions.TryGetValue(id, out var r) && r.SilentMs > AwayAfterMs);
        foreach (var s in _sessions.Values)
        {
            Guard(s, s.Tick);
            if (s.State != SessionState.Closed && s.SilentMs > AwayAfterMs && s.Pad != null)
                s.Close("Gamepad went away", CloseReason.WentAway);
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
                Adopt(device);
            new Thread(() => ReadLoop(device))
            {
                IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = $"Link {device.Id}",
            }.Start();
        }
    }

    // Under _lock: a newly opened collection joins its gamepad's session, made for it the first time
    void Adopt(LinkDevice device)
    {
        _devices[device.Path] = device;
        if (!_sessions.TryGetValue(device.Id, out var session))
        {
            session = new PadSession(_ctx, _adapter, device.Id, RateHz);
            if (_modes.TryGetValue(device.Id, out byte format))
                session.SetMode(format);
            _sessions[device.Id] = session;
        }
        Guard(session, () => session.Attach(device));
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
                Guard(session, () => session.OnReport(report, arrivalUs));
            }
        }
    }

    // One gamepad's fault closes that gamepad, not the app
    static void Guard(PadSession session, Action work)
    {
        try { work(); }
        catch (Exception e)
        {
            Log.Write($"{session.Id}: {e}");
            session.Close($"Error: {e.Message}", CloseReason.Closed);
        }
    }
}
