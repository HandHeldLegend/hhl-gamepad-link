// The GameCube adapter that gamepads in Slippi mode share: one virtual Wii U GameCube Controller
// Adapter, as Dolphin and Slippi read it, with each gamepad on a port of its own.
//
// Each gamepad sends the report of an adapter with itself on port 1. Its port's 9 bytes (status and
// input) go through its own playout buffer, and the adapter's clock builds one report from every
// port's newest sample. Dolphin's rumble report has a byte per port, and each goes to its gamepad.
// A port is unplugged the moment its gamepad closes. One that goes quiet first lets go of its
// buttons and sticks, then unplugs, so a dropped gamepad never holds an input.
//
// The adapter is a USB device on the USB/IP transport (HIDMaestro's hhl-gamecube-adapter profile),
// because Dolphin opens it through libusb. It comes up with the first gamepad and stays a while
// after the last one leaves, so a gamepad that drops out and comes back doesn't take the adapter
// away from Dolphin.

using HIDMaestro;

namespace HhlGamepadLink;

sealed class GcAdapter : IDisposable
{
    public const int PortCount = 4;
    const int PortBytes = 9;               // Status, then 8 bytes of input
    const byte ReportInput = 0x21, ReportRumble = 0x11;
    const byte StatusEmpty = 0x04;         // Powered, no controller
    const long ReportPeriodUs = 1_000;     // 1000 Hz, as an overclocked adapter
    const long KeepWhenEmptyMs = 10_000;
    const long QuietNeutralUs = 100_000;   // No input this long: buttons up, sticks centred
    const long QuietUnplugUs = 1_000_000;  // No input this long: no controller on the port
    const string ProfileId = "hhl-gamecube-adapter";
    const string Identity = "hhl-link-gamecube-adapter";
    static readonly TimeSpan RemoveWait = TimeSpan.FromSeconds(30);

    public sealed class Port
    {
        internal byte[] Current = Empty();
        internal byte Rumble;
        internal long LastInputUs = long.MinValue / 2; // When its newest input arrived
        internal readonly Action<byte[]> SendToGamepad;
        readonly GcAdapter _adapter;

        internal Port(GcAdapter adapter, int number, Action<byte[]> sendToGamepad)
        {
            _adapter = adapter;
            Number = number;
            SendToGamepad = sendToGamepad;
        }

        public int Number { get; }  // 1 to 4, as Dolphin numbers them
        public bool Ready => _adapter._device != null;
        public bool Installing => _adapter._installing;
        public string? Problem => _adapter._problem;

        // A report from the gamepad: an adapter's input report with the gamepad on port 1
        public void Add(long arrivalUs, ReadOnlySpan<byte> report)
        {
            if (report.Length >= 1 + PortBytes && report[0] == ReportInput)
            {
                Volatile.Write(ref Current, report.Slice(1, PortBytes).ToArray());
                Volatile.Write(ref LastInputUs, arrivalUs);
            }
        }

        public void Leave() => _adapter.Leave(this);
    }

    readonly HMContext _ctx;
    readonly object _lock = new();
    readonly Port?[] _ports = new Port?[PortCount];
    readonly Dictionary<string, int> _lastPort = new(); // A gamepad gets its port back when it returns
    readonly List<Task> _deviceWork = new();

    volatile HMController? _device;
    volatile string? _problem;
    volatile bool _installing;
    bool _creating;
    long _emptySinceMs = -1;

    public GcAdapter(HMContext ctx) => _ctx = ctx;

    // A port for this gamepad, or null when all four are taken
    public Port? Join(string padId, Action<byte[]> sendToGamepad)
    {
        lock (_lock)
        {
            int index = _lastPort.TryGetValue(padId, out int last) && _ports[last] == null
                ? last
                : Array.IndexOf(_ports, null);
            if (index < 0)
                return null;

            var port = new Port(this, index + 1, sendToGamepad);
            _ports[index] = port;
            _lastPort[padId] = index;
            _emptySinceMs = -1;
            if (_device == null && !_creating)
                CreateDevice();
            return port;
        }
    }

    // From the hub's thread: the adapter goes once it has been empty a while
    public void Tick()
    {
        lock (_lock)
        {
            if (_emptySinceMs >= 0 && Environment.TickCount64 - _emptySinceMs >= KeepWhenEmptyMs)
            {
                _emptySinceMs = -1;
                RemoveDevice();
            }
        }
    }

    public void Dispose() => RemoveNow();

    // Remove the adapter and wait until it is gone: when the app closes, or a driver is removed
    public void RemoveNow()
    {
        lock (_lock)
            RemoveDevice();
        WaitForDeviceWork();
        lock (_lock)
            RemoveDevice(); // One that finished being made while closing
        WaitForDeviceWork();
    }

    // A port's controller at rest: its status, nothing pressed, sticks centred, triggers released
    static byte[] Neutral(byte status) => new byte[] { status, 0, 0, 0x80, 0x80, 0x80, 0x80, 0, 0 };

    // No controller. The input is a resting one, not zeros: Dolphin keeps reading a port during
    // netplay, where it doesn't report the controller gone, and zeros are sticks held down-left
    static byte[] Empty() => Neutral(StatusEmpty);

    void Leave(Port port)
    {
        lock (_lock)
        {
            int index = port.Number - 1;
            if (_ports[index] != port)
                return;
            _ports[index] = null;
            if (_ports.All(p => p == null))
                _emptySinceMs = Environment.TickCount64;
        }
    }

    // --- The device ---

    void CreateDevice()
    {
        _creating = true;
        _problem = null;
        Track(Task.Run(() =>
        {
            HMController? created = null;
            try
            {
                var profile = _ctx.GetProfile(ProfileId)
                    ?? throw new InvalidOperationException("this build has no GameCube adapter profile");

                // The first time on a PC: the USB/IP transport, and USB devices blink once
                _installing = !HMContext.IsUsbipBackendAvailable;
                Drivers.EnsureUsbIp();
                _installing = false;
                created = _ctx.CreateController(profile, Identity);
            }
            catch (Exception e)
            {
                _installing = false;
                _problem = $"Couldn't create the GameCube adapter: {e.Message}";
            }

            lock (_lock)
            {
                _creating = false;
                if (created == null)
                    return;
                if (_emptySinceMs >= 0 && _ports.All(p => p == null))
                {
                    Track(Task.Run(created.Dispose)); // Everyone left while it was being made
                    return;
                }
                created.OutputReceived += OnOutput;
                _device = created;
                StartClock(created);
            }
        }));
    }

    void RemoveDevice()
    {
        var gone = _device;
        _device = null; // Its clock stops, and submits no more once this lock is released
        if (gone != null)
        {
            gone.OutputReceived -= OnOutput;
            Track(Task.Run(gone.Dispose));
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

    void WaitForDeviceWork()
    {
        Task[] pending;
        lock (_deviceWork)
            pending = _deviceWork.ToArray();
        Task.WaitAll(pending, RemoveWait);
    }

    // Dolphin's output: 0x13 starts the adapter, 0x11 sets each port's rumble
    void OnOutput(HMController controller, HMOutputPacket packet)
    {
        if (packet.Source != HMOutputSource.HidOutput || packet.ReportId != ReportRumble)
            return;
        var rumble = packet.Data.Span;
        lock (_lock)
        {
            for (int i = 0; i < PortCount && i < rumble.Length; i++)
            {
                var port = _ports[i];
                if (port == null || port.Rumble == rumble[i])
                    continue;
                port.Rumble = rumble[i];
                port.SendToGamepad(new byte[] { ReportRumble, rumble[i] }); // The gamepad's own port 1
            }
        }
    }

    // One report every millisecond, from each port's newest sample that is due, while this is the
    // device
    void StartClock(HMController device) =>
        new Thread(() => RunClock(device))
        {
            IsBackground = true, Priority = ThreadPriority.Highest, Name = "GameCube adapter",
        }.Start();

    void RunClock(HMController device)
    {
        using var timer = new PeriodicClock();
        var report = new byte[1 + PortCount * PortBytes];
        report[0] = ReportInput;
        long nextUs = PeriodicClock.NowUs;
        while (_device == device)
        {
            nextUs += ReportPeriodUs;
            long nowUs = PeriodicClock.NowUs;
            if (nextUs < nowUs)
                nextUs = nowUs; // Behind (the PC was busy): carry on from now
            timer.WaitUntilUs(nextUs);
            nowUs = PeriodicClock.NowUs;

            lock (_lock)
            {
                for (int i = 0; i < PortCount; i++)
                    PortData(_ports[i], nowUs).CopyTo(report, 1 + i * PortBytes);

                // Under the lock removal takes, so the device can't be disposed mid-submit: its
                // memory goes with it, and writing there ends the app
                if (_device != device)
                    break;
                device.SubmitRawExtendedReport(report);
            }
        }
    }

    // A port's part of the report: its newest input, or at rest once its gamepad goes quiet
    static byte[] PortData(Port? port, long nowUs)
    {
        if (port == null)
            return Empty();
        byte[] current = Volatile.Read(ref port.Current);
        long quietUs = nowUs - Volatile.Read(ref port.LastInputUs);
        return quietUs >= QuietUnplugUs ? Empty()
             : quietUs >= QuietNeutralUs ? Neutral(current[0])
             : current;
    }
}
