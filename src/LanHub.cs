// The network side: one socket hears every gamepad's beacon and the streams of the ones opened
// here. It keeps the list of gamepads on the network and the sessions this PC has open.

using System.Net;
using System.Net.Sockets;
using HIDMaestro;

namespace HhlGamepadWlan;

// A gamepad as the list shows it. Away: one this PC was using that has gone quiet or stopped beaconing.
sealed record PadEntry(Beacon Pad, DateTime LastSeen, PadSession? Session, bool Away);

sealed class LanHub : IDisposable
{
    const double ForgetAfterSeconds = 5; // No beacon for this long: off or out of range

    readonly HMContext _ctx;
    readonly GcAdapter _adapter;
    readonly Socket _socket;
    readonly Thread _thread;
    volatile bool _running = true;

    readonly object _lock = new();
    readonly Dictionary<string, (Beacon Pad, EndPoint Address, DateTime LastSeen)> _pads = new();
    readonly Dictionary<string, PadSession> _sessions = new();
    readonly Dictionary<IPAddress, PadSession> _byAddress = new();
    readonly Dictionary<string, byte> _modes = new(); // Picked here this run, by gamepad

    public uint Session { get; } = (uint)Random.Shared.Next(1, int.MaxValue); // This app run
    public string PcName { get; } = Environment.MachineName;
    public int RateHz { get; private set; }

    public LanHub(HMContext ctx, int rateHz)
    {
        _ctx = ctx;
        _adapter = new GcAdapter(ctx);
        RateHz = rateHz;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        _socket.Bind(new IPEndPoint(IPAddress.Any, Protocol.PcPort));
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "LAN" };
        _thread.Start();
    }

    public IReadOnlyList<PadEntry> Snapshot()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            return _pads
                .Select(p =>
                {
                    var session = _sessions.GetValueOrDefault(p.Key);
                    bool wentAway = session is { State: SessionState.Closed, Reason: CloseReason.WentAway };
                    return new PadEntry(p.Value.Pad, p.Value.LastSeen, session,
                                        wentAway || (now - p.Value.LastSeen).TotalSeconds > ForgetAfterSeconds);
                })
                .OrderBy(e => e.Pad.Title).ThenBy(e => e.Pad.Id)
                .ToList();
        }
    }

    // Open a free gamepad, or take one another PC has open
    public void Open(string id)
    {
        lock (_lock)
        {
            if (!_pads.TryGetValue(id, out var pad))
                return;
            if (_sessions.TryGetValue(id, out var existing) && existing.State != SessionState.Closed)
                return;
            var session = new PadSession(_ctx, _adapter, pad.Pad, pad.Address, Session, PcName, RateHz, Send);
            if (_modes.TryGetValue(id, out byte format))
                session.SetMode(format);
            _sessions[id] = session;
            _byAddress[((IPEndPoint)pad.Address).Address] = session;
        }
    }

    public void Close(string id)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var session))
                session.Close("Closed", CloseReason.Closed);
        }
    }

    // Drop a disconnected gamepad from the list, and stop opening it again when it is back
    public void Forget(string id)
    {
        lock (_lock)
        {
            _pads.Remove(id);
            if (_sessions.Remove(id, out var session))
                session.Close("Forgotten", CloseReason.Closed);
        }
    }

    // A gamepad open here that went away is opened again when it is back and free: not one closed
    // here, and not one another PC took
    void ReopenIfBack(Beacon pad)
    {
        if (_sessions.TryGetValue(pad.Id, out var last) && last.State == SessionState.Closed &&
            last.Reason == CloseReason.WentAway && pad.OwnerSession == 0)
            Open(pad.Id);
    }

    // Put a gamepad in a mode for the rest of this run: now if it is open here, and each time it is
    // opened again
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

    void Send(byte[] packet, EndPoint to)
    {
        try { _socket.SendTo(packet, to); }
        catch (SocketException) { }
    }

    void Run()
    {
        byte[] buf = new byte[256];
        while (_running)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n = 0;
            try
            {
                // Wake at least every 5 ms so sessions keep their timers even when nothing arrives
                if (_socket.Poll(5000, SelectMode.SelectRead))
                    n = _socket.ReceiveFrom(buf, ref from);
            }
            catch (SocketException) { } // A closed port answering an earlier send

            if (n > 0)
                Handle(buf.AsSpan(0, n), from);

            lock (_lock)
            {
                foreach (var session in _sessions.Values)
                    session.Tick();
                DropSilent();
                _adapter.Tick();
            }
        }
    }

    void Handle(ReadOnlySpan<byte> packet, EndPoint from)
    {
        if (DataPacket.TryParse(packet, out var data))
        {
            // The stream of a gamepad opened here goes to its session. A command reply is echoed
            // every time, as its receipt; the rest only now and then, for the round trip, since
            // the OPEN keepalive already tells the gamepad this PC is there. Echoing every input
            // would double the traffic on the WLAN.
            lock (_lock)
            {
                if (_byAddress.TryGetValue(((IPEndPoint)from).Address, out var session))
                {
                    if (data.ReliableToken != 0 || session.EchoDue())
                        Send(packet.ToArray(), from);
                    try { session.OnData(data); }
                    catch (Exception e)
                    {
                        // One gamepad's fault closes that gamepad, not the app
                        session.Close($"Error: {e.Message}", CloseReason.Closed);
                    }
                }
            }
            return;
        }

        // The config app's replies and live view from a gamepad opened here
        if (packet.Length == Protocol.OutSize)
        {
            uint magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet);
            if (magic == Protocol.MagicReply || magic == Protocol.MagicLive)
            {
                if (magic == Protocol.MagicReply)
                    Send(packet.ToArray(), from); // Receipt: the gamepad resends until it sees this
                ushort token = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(4));
                int len = Math.Min((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(6)), 64);
                byte[] message = packet.Slice(8, len).ToArray();
                lock (_lock)
                {
                    if (_byAddress.TryGetValue(((IPEndPoint)from).Address, out var owner))
                    {
                        if (magic == Protocol.MagicReply)
                            owner.OnConfigReply(token, message);
                        else
                            owner.OnLive(message);
                    }
                }
                return;
            }
        }

        if (Beacon.Parse(packet) is Beacon beacon)
        {
            // Beacons come from the gamepad's port; OPEN and CLOS go back to it there
            var address = new IPEndPoint(((IPEndPoint)from).Address, Protocol.GamepadPort);
            lock (_lock)
            {
                _pads[beacon.Id] = (beacon, address, DateTime.UtcNow);
                ReopenIfBack(beacon);
                if (_sessions.TryGetValue(beacon.Id, out var session))
                {
                    if (!((IPEndPoint)session.Address).Address.Equals(address.Address))
                        _byAddress[address.Address] = session; // It got a new address
                    session.OnBeacon(beacon, address);
                }
            }
        }
    }

    // Gamepads that stopped beaconing leave the list, except ones this PC was using: those stay as
    // disconnected, and are opened again when they are back
    void DropSilent()
    {
        var now = DateTime.UtcNow;
        foreach (var id in _pads.Where(p => (now - p.Value.LastSeen).TotalSeconds > ForgetAfterSeconds)
                                .Select(p => p.Key).ToList())
        {
            if (_sessions.TryGetValue(id, out var session) &&
                (session.State != SessionState.Closed || session.Reason == CloseReason.WentAway))
                session.Close("Gamepad went away", CloseReason.WentAway, sendClose: false);
            else
                _pads.Remove(id);
        }
        foreach (var gone in _byAddress.Where(a => a.Value.State == SessionState.Closed).Select(a => a.Key).ToList())
            _byAddress.Remove(gone);
    }

    // Close every gamepad open here and wait until their devices are gone: before a driver is removed
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

    // Release everything this PC has open, and wait until its controllers are gone
    public void Dispose()
    {
        PadSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            foreach (var session in sessions)
                session.Close("Closed", CloseReason.Closed);
        }
        _running = false;
        _thread.Join(500);
        _socket.Dispose();

        foreach (var session in sessions)
            session.WaitForDevices(TimeSpan.FromSeconds(30));
        _adapter.Dispose();
    }
}
