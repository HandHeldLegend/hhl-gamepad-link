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
    volatile bool _searching = true; // What the caller was last told (Caller.Search)
    readonly Thread _thread;
    volatile bool _running = true;

    readonly object _lock = new();
    readonly Dictionary<string, LinkDevice> _devices = new();   // Open collections, by path
    readonly Dictionary<string, PadSession> _sessions = new();  // By gamepad (Link address)
    readonly HashSet<string> _refused = new();                  // Disconnected here, until they go quiet
    readonly HashSet<string> _pairing = new();                  // Being paired now
    HashSet<string> _windowsPaired = new();                     // Known gamepads Windows holds a pairing for

    public LinkHub(HMContext ctx, int reportIntervalUs)
    {
        _ctx = ctx;
        _adapter = new GcAdapter(ctx);
        ReportIntervalUs = reportIntervalUs;
        _caller.IsOpenHere = IsOpenHere;
        _thread = new Thread(Run) { IsBackground = true, Name = "Link" };
        _thread.Start();
    }

    public int ReportIntervalUs { get; private set; } // What the gamepads are asked for

    // The search for gamepads over LE (Caller.Search) costs a gamepad open here report rate, so
    // while one is, it only runs when asked for: the window sets this, every frame
    public volatile bool ScanRequested;

    public bool Searching => _searching;

    public IReadOnlyList<PadEntry> Snapshot()
    {
        lock (_lock)
        {
            var entries = _sessions.Values.Where(s => s.Pad != null).Select(SessionEntry).ToList();
            // Gamepads advertising nearby that aren't here this run: linked here before (call them
            // over), or not (pair them)
            var nearby = _caller.Nearby(NearbyFor);
            foreach (var (id, seen) in nearby)
                if (!_sessions.ContainsKey(id))
                    entries.Add(NearbyEntry(id, seen));
            // Known gamepads neither here nor nearby: listed as disconnected, so they can be forgotten
            foreach (var known in KnownPads.All())
                if (!_sessions.ContainsKey(known.Id) && !nearby.Any(n => n.Id == known.Id))
                    entries.Add(KnownEntry(known));
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
        Pairing.Remove(id); // As Windows' Remove device: pairing again sets it up afresh
    }

    // Put a gamepad in a mode for the rest of this run: now if it is here, and each time it comes back
    public void SetMode(string id, byte format)
    {
        KnownPads.SetMode(id, format);
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var session))
                session.SetMode(format);
        }
    }

    public void SetReportInterval(int intervalUs)
    {
        lock (_lock)
        {
            ReportIntervalUs = intervalUs;
            foreach (var session in _sessions.Values)
                session.SetReportInterval(intervalUs);
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
        // Its model as it advertises it: its mode's name (a console's, "Pro Controller") is not
        var pad = known != null ? s.Pad! with { Name = known.Name } : s.Pad!;
        return new PadEntry(pad, s, !here && s.Reason == CloseReason.WentAway, _refused.Contains(s.Id),
                            here ? null : _caller.Seen(s.Id, NearbyFor), _caller.CallStatus(s.Id),
                            known != null && _windowsPaired.Contains(s.Id), known?.UserName ?? "");
    }

    PadEntry KnownEntry(KnownPads.Pad known)
    {
        var pad = new PadIdentity(known.Id, known.Name, 0, 0, known.Mode, ModeForced: false);
        return new PadEntry(pad, null, Away: true, false, null, _caller.CallStatus(known.Id),
                            _windowsPaired.Contains(known.Id), known.UserName);
    }

    PadEntry NearbyEntry(string id, Caller.Sighting seen)
    {
        var known = KnownPads.Get(id);
        string name = seen.Name.Length > 0 ? seen.Name : known?.Name ?? "HOJA gamepad";
        string userName = seen.UserName.Length > 0 ? seen.UserName : known?.UserName ?? "";
        var pad = new PadIdentity(id, name, 0, 0, seen.Format, ModeForced: false);
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
                _caller.Search(_searching); // Again, should the last change not have taken
            }
            bool search = ScanRequested || !AnyOpenHere;
            if (search != _searching)
            {
                _searching = search;
                _caller.Search(search);
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
    bool IsOpenHere(string id)
    {
        lock (_lock)
            return _sessions.TryGetValue(id, out var s) && s.State != SessionState.Closed;
    }

    public bool AnyOpenHere
    {
        get
        {
            lock (_lock)
                return _sessions.Values.Any(s => s.State != SessionState.Closed);
        }
    }

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
            session = new PadSession(_ctx, _adapter, device.Id, ReportIntervalUs);
            // The mode it was last used in here: it follows, unless its mode was chosen on it
            if (KnownPads.Get(device.Id)?.Mode is byte mode && PadSession.IsPcMode(mode))
                session.SetMode(mode);
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
