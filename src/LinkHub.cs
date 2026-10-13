// The gamepads in Link mode on this PC: each one's Link HID collection (HidLink.cs), opened as it
// appears. What says a gamepad is here is its reports: the host info goes to every one each second
// (the gamepad waits for it, and treats three silent seconds as the app gone), and one that stops
// reporting is away until it reports again. A gamepad disconnected here gets no host info until it
// has gone quiet (it went back to looking for a dongle); after that it is welcome again, when it is
// next started in dongle mode.

using HIDMaestro;

namespace HhlGamepadLink;

// A gamepad as the list shows it. Away: it was here and has gone quiet. Refused: disconnected here.
// Nearby: not here, but advertising (connected elsewhere). Known: it gave this app its call token,
// so it can be called over; otherwise it can be paired. Call says how the last call or pairing went.
sealed record PadEntry(PadIdentity Pad, PadSession? Session, bool Away, bool Refused, Caller.Sighting? Nearby,
                       string? Call, bool Known = true, string UserName = "");

sealed class LinkHub : IDisposable
{
    const long HeartbeatMs = 1000;
    const long AwayAfterMs = 2000; // No report this long: the gamepad is away (the link reports far faster)

    static readonly TimeSpan NearbyFor = TimeSpan.FromSeconds(90); // Advertising every 1 s, but a busy radio scans poorly (10-45 s between sightings measured)

    readonly HMContext _ctx;
    readonly GcAdapter _adapter;
    readonly Caller _caller = new();
    readonly Thread _thread;
    volatile bool _running = true;

    readonly object _lock = new();
    readonly Dictionary<string, LinkDevice> _devices = new();   // Open collections, by path
    readonly Dictionary<string, PadSession> _sessions = new();  // By gamepad (Link address)
    readonly HashSet<string> _refused = new();
    readonly Dictionary<string, byte> _modes = new();           // Picked here this run, by gamepad
    HashSet<string> _windowsPaired = new();                     // Known gamepads Windows holds a Link pairing for

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
            var entries = _sessions.Values
                .Where(s => s.Pad != null)
                .Select(s =>
                {
                    bool here = s.State != SessionState.Closed;
                    return new PadEntry(s.Pad!, s, s.State == SessionState.Closed && s.Reason == CloseReason.WentAway,
                                        _refused.Contains(s.Id), here ? null : _caller.Seen(s.Id, NearbyFor),
                                        _caller.CallStatus(s.Id));
                })
                .ToList();
            entries = entries.Select(e => KnownPads.Get(e.Pad.Id) is KnownPads.Pad k
                                          ? e with { UserName = k.UserName, Known = _windowsPaired.Contains(k.Id) }
                                          : e with { Known = false }).ToList();
            // Gamepads advertising nearby that aren't here this run: linked here before (call them
            // over), or not (pair them)
            foreach (var (id, seen) in _caller.Nearby(NearbyFor))
            {
                if (_sessions.ContainsKey(id))
                    continue;
                var known = KnownPads.Get(id);
                string name = seen.Name.Length > 0 ? seen.Name : known?.Name ?? "HOJA gamepad";
                var pad = new PadIdentity(id, name, "", 0, 0, seen.Format, 0, false);
                entries.Add(new PadEntry(pad, null, false, false, seen, _caller.CallStatus(id),
                                         known != null && _windowsPaired.Contains(id),
                                         seen.UserName.Length > 0 ? seen.UserName : known?.UserName ?? ""));
            }
            return entries.OrderBy(e => e.UserName.Length > 0 ? e.UserName : e.Pad.Title).ThenBy(e => e.Pad.Id).ToList();
        }
    }

    // Pair a gamepad seen nearby: it restarts findable for Link mode (if it isn't in use), and this
    // app pairs it (then Link mode connects, and hands over its call token)
    public void Pair(string id)
    {
        if (_caller.CallStatus(id) is string s && s.StartsWith("Pairing"))
            return;
        _caller.SetStatus(id, "Pairing…");
        _ = Task.Run(async () =>
        {
            string? error = await _caller.AskToPairAsync(id);
            if (error != null)
            {
                _caller.SetStatus(id, error);
                return;
            }
            using var cancel = new CancellationTokenSource();
            error = Pairing.Pair(id, 45, cancel.Token);
            _caller.SetStatus(id, error ?? "Paired: connecting…");
            if (error != null)
                Log.Write($"Pairing {id}: {error}");
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

    void Run()
    {
        long nextScan = 0, nextBeat = 0;
        while (_running)
        {
            long now = Environment.TickCount64;
            if (now >= nextScan)
            {
                try { Scan(); }
                catch (Exception e) { Log.Write($"Link scan: {e}"); }
                // A pairing removed in Windows' settings: the gamepad needs pairing again, not a call
                var paired = KnownPads.All().Where(k => Pairing.IsPaired(k.Id)).Select(k => k.Id).ToHashSet();
                lock (_lock)
                    _windowsPaired = paired;
                nextScan = now + 2000;
            }
            lock (_lock)
            {
                if (now >= nextBeat)
                {
                    foreach (var s in _sessions.Values)
                        if (!_refused.Contains(s.Id))
                            Guard(s, s.Heartbeat);
                    nextBeat = now + HeartbeatMs;
                }
                // Released: once a gamepad disconnected here has gone quiet, it may come back
                _refused.RemoveWhere(id => _sessions.TryGetValue(id, out var r) && r.SilentMs > AwayAfterMs);
                foreach (var s in _sessions.Values)
                {
                    Guard(s, s.Tick);
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
                Guard(session, () => session.Attach(device));
            }
            new Thread(() => ReadLoop(device)) { IsBackground = true, Priority = ThreadPriority.AboveNormal,
                                                 Name = $"Link {device.Id}" }.Start();
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
        _caller.Dispose();
        _adapter.Dispose();
    }
}
