// The Bluetooth side: the app's RFCOMM service, which gamepads call (they page this PC, as they
// would a HOJA dongle), and the gamepads connected to it. A connected gamepad is opened here at
// once; one closed here is turned away until it is opened again.

using HIDMaestro;

namespace HhlGamepadLink;

// A gamepad as the list shows it. Away: it was connected and has gone; it is opened again when back.
sealed record PadEntry(PadIdentity Pad, PadSession? Session, bool Away, bool Refused);

sealed class BluetoothHub : IDisposable
{
    readonly HMContext _ctx;
    readonly GcAdapter _adapter;
    readonly RfcommSocket _listener;
    readonly Thread _acceptThread, _tickThread;
    volatile bool _running = true;

    readonly object _lock = new();
    readonly Dictionary<string, PadSession> _sessions = new(); // Every gamepad seen this run
    readonly HashSet<string> _refused = new();                 // Closed here: turned away
    readonly Dictionary<string, byte> _modes = new();          // Picked here this run, by gamepad

    public int RateHz { get; private set; }
    public int Channel => _listener.Channel;
    public string? Problem { get; private set; }               // The SDP record couldn't be published

    public BluetoothHub(HMContext ctx, int rateHz)
    {
        _ctx = ctx;
        _adapter = new GcAdapter(ctx);
        RateHz = rateHz;
        _listener = RfcommSocket.Listen(Protocol.FixedChannel); // Throws without a Bluetooth radio
        try { _listener.Publish(Protocol.ServiceUuid, Protocol.ServiceName); }
        catch (BluetoothException e) { Problem = e.Message; }

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Bluetooth accept" };
        _acceptThread.Start();
        _tickThread = new Thread(TickLoop) { IsBackground = true, Name = "Sessions" };
        _tickThread.Start();
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

    static string Id(ulong address) => (address & 0xFFFFFFFFFFFF).ToString("X12");

    void AcceptLoop()
    {
        while (_running)
        {
            var link = _listener.Accept();
            if (link == null)
                return; // Closed
            string id = Id(link.Peer);
            PadSession session;
            lock (_lock)
            {
                if (_refused.Contains(id))
                {
                    link.Dispose();
                    continue;
                }
                if (_sessions.TryGetValue(id, out var old))
                    old.Close("Connected again", CloseReason.Closed); // A stale connection
                session = new PadSession(_ctx, _adapter, id, link, RateHz);
                if (_modes.TryGetValue(id, out byte format))
                    session.SetMode(format);
                _sessions[id] = session;
            }
            new Thread(() => ReadLoop(session, link)) { IsBackground = true, Priority = ThreadPriority.AboveNormal,
                                                        Name = $"Bluetooth {id}" }.Start();
        }
    }

    // One gamepad's frames, as they arrive
    void ReadLoop(PadSession session, RfcommSocket link)
    {
        var buffer = new byte[4096];
        int held = 0;
        while (_running)
        {
            int n = link.Receive(buffer.AsSpan(held));
            if (n == 0)
                break;
            held += n;
            long arrivalUs = PeriodicClock.NowUs;

            int at = 0;
            while (held - at >= 2 && held - at >= 2 + buffer[at + 1])
            {
                var payload = buffer.AsSpan(at + 2, buffer[at + 1]);
                lock (_lock)
                {
                    try { session.OnFrame(buffer[at], payload, arrivalUs); }
                    catch (Exception e)
                    {
                        // One gamepad's fault closes that gamepad, not the app
                        session.Close($"Error: {e.Message}", CloseReason.Closed);
                    }
                }
                at += 2 + buffer[at + 1];
            }
            held -= at;
            buffer.AsSpan(at, held).CopyTo(buffer);
        }
        lock (_lock)
            session.Close("Gamepad went away", CloseReason.WentAway);
        link.Dispose();
    }

    void TickLoop()
    {
        while (_running)
        {
            Thread.Sleep(5);
            lock (_lock)
            {
                foreach (var session in _sessions.Values)
                    session.Tick();
                _adapter.Tick();
            }
        }
    }

    // Disconnect a gamepad and turn it away until it is opened again
    public void Close(string id)
    {
        lock (_lock)
        {
            _refused.Add(id);
            if (_sessions.TryGetValue(id, out var session))
                session.Close("Closed", CloseReason.Closed);
        }
    }

    // Let a gamepad closed here connect again: it does when it next calls
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

    // Put a gamepad in a mode for the rest of this run: now if it is connected, and each time it
    // connects again
    public void SetMode(string id, byte format)
    {
        lock (_lock)
        {
            _modes[id] = format;
            if (_sessions.TryGetValue(id, out var session) && session.State != SessionState.Closed)
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

    // Close every gamepad connected here and wait until their devices are gone: before a driver
    // is removed
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
        try { _listener.Unpublish(Protocol.ServiceUuid, Protocol.ServiceName); }
        catch (BluetoothException) { }
        _listener.Dispose(); // Ends the accept loop
        CloseAll();
        _tickThread.Join(500);
        _adapter.Dispose();
    }
}
