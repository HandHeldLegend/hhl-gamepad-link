// Calling gamepads over to Link from wherever they are connected (dongle_link.h, "Calling it to
// Link"): a gamepad advertises over Bluetooth LE in its direct Bluetooth modes, this app watches
// for that, and a call (the gamepad's token, written to its call service) brings it over. All user
// mode (WinRT).
//
// Advertisements are slow to catch (once a second, and Windows' background scan misses most), so a
// gamepad connected to this PC over Classic is also read directly: its LE address is its Classic
// one, and its call service's info characteristic says the same as its advertisement.

using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace HhlGamepadLink;

sealed class Caller : IDisposable
{
    // A gamepad seen advertising: where to reach it over LE, the mode it is in, whether it has a
    // Link pairing (with some PC), its names (from the scan response, once seen), and when
    public sealed record Sighting(ulong LeAddress, byte Format, bool Linked, string Name, string UserName,
                                  DateTime Seen);

    const byte AdServiceData128 = 0x21;      // AD type: service data with a 128-bit UUID
    const int CallAdvertBytes = 25;          // [uuid, 16][version][mode][flags][Link address, 6]
    const ushort UserNameCompany = 0xFFFF;   // Its manufacturer data in the scan response: the user's name
    const int CallInfoMinBytes = 11;         // [version][mode][flags][Link address, 6] and two name lengths
    const byte AttWriteNotPermitted = 0x03;  // The gamepad refusing a call or a pairing request
    const int WriteTries = 3;
    const int WriteRetryMs = 500;
    const int DirectReadEveryMs = 5000;
    static readonly TimeSpan NotHojaFor = TimeSpan.FromMinutes(5); // A device without the call service
    static readonly TimeSpan CallWithin = TimeSpan.FromSeconds(30);
    static readonly TimeSpan PairWithin = TimeSpan.FromSeconds(90);

    readonly BluetoothLEAdvertisementWatcher _watcher;
    readonly object _lock = new();
    readonly Dictionary<string, Sighting> _seen = new();    // By Link address
    readonly Dictionary<ulong, string> _byLe = new();      // LE address -> Link address (scan responses)
    readonly Dictionary<string, string> _calls = new();    // Link address -> how its call or pairing went
    readonly Dictionary<ulong, DateTime> _notHoja = new(); // Connected devices without the call service
    readonly CancellationTokenSource _stop = new();
    volatile bool _searching = true;

    public Caller()
    {
        _ = Task.Run(DirectLoop);
        // Active: the radio asks as well as listens, which finds a gamepad sooner
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += OnReceived;
        Search(true);
    }

    // A gamepad open here in Link mode, by Link address: never read over LE (it has no LE there)
    public Func<string, bool> IsOpenHere { get; set; } = _ => false;

    // Whether to look for gamepads over LE: the watcher, and reading the ones connected over Classic.
    // Both take the PC radio's airtime from a gamepad open here in Link mode. Measured on Windows:
    // scanning cost it 13% of its report rate, trying an LE connection half.
    public void Search(bool on)
    {
        _searching = on;
        try
        {
            var status = _watcher.Status;
            if (on && status is BluetoothLEAdvertisementWatcherStatus.Created
                               or BluetoothLEAdvertisementWatcherStatus.Stopped)
                _watcher.Start();
            else if (!on && status == BluetoothLEAdvertisementWatcherStatus.Started)
                _watcher.Stop();
        }
        catch (Exception e) { Log.Write($"LE watcher: {e.Message}"); } // No LE radio: no calls
    }

    // Every gamepad seen within the window
    public List<(string Id, Sighting Seen)> Nearby(TimeSpan within)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
            return _seen.Where(s => now - s.Value.Seen < within).Select(s => (s.Key, s.Value)).ToList();
    }

    // Where a gamepad was last seen, if within the window
    public Sighting? Seen(string id, TimeSpan within)
    {
        lock (_lock)
            return _seen.TryGetValue(id, out var s) && DateTime.UtcNow - s.Seen < within ? s : null;
    }

    public string? CallStatus(string id)
    {
        lock (_lock)
            return _calls.GetValueOrDefault(id);
    }

    public void SetStatus(string id, string status)
    {
        lock (_lock)
            _calls[id] = status;
    }

    // Call a gamepad over: in the background, as it takes a few seconds
    public void Call(string id, byte[] token)
    {
        Sighting? seen = Seen(id, CallWithin);
        lock (_lock)
        {
            if (_calls.GetValueOrDefault(id) == "Calling…")
                return;
            _calls[id] = seen == null ? "Not nearby" : "Calling…";
        }
        if (seen == null)
            return;
        _ = Task.Run(async () =>
        {
            string result;
            try { result = await CallAsync(seen.LeAddress, token); }
            catch (Exception e) { result = $"Call failed: {e.Message}"; }
            lock (_lock)
                _calls[id] = result;
        });
    }

    // Ask a gamepad to pair with this PC: it pages here. Null when it took the request, else what
    // went wrong.
    public async Task<string?> AskToPairAsync(string id)
    {
        Sighting? seen = Seen(id, PairWithin);
        if (seen == null)
            return "Not nearby";
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null)
                return "No Bluetooth on this PC";
            // [CallPair][this PC's address, as shown]
            var command = new byte[7];
            command[0] = Protocol.CallPair;
            for (int i = 0; i < 6; i++)
                command[1 + i] = (byte)(adapter.BluetoothAddress >> (8 * (5 - i)));
            return await WriteAsync(seen.LeAddress, command);
        }
        catch (Exception e) { return $"Couldn't reach it: {e.Message}"; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _watcher.Stop(); }
        catch (Exception) { }
    }

    // --- Advertisements ---

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        string? id = ReadCallAdvert(e);
        ReadScanResponse(e, id);
    }

    // Service data: [call service uuid, LE][version][mode][flags][Link address as shown]. Returns the
    // Link address when it is a gamepad's.
    string? ReadCallAdvert(BluetoothLEAdvertisementReceivedEventArgs e)
    {
        string? id = null;
        foreach (var section in e.Advertisement.DataSections)
        {
            if (section.DataType != AdServiceData128 || section.Data.Length < CallAdvertBytes)
                continue;
            byte[] data = Bytes(section.Data);
            if (UuidFromWire(data.AsSpan(0, 16)) != Protocol.CallService || data[16] != Protocol.CallVersion)
                continue;
            id = Convert.ToHexString(data, 19, 6);
            lock (_lock)
            {
                var old = _seen.GetValueOrDefault(id);
                _seen[id] = new Sighting(e.BluetoothAddress, data[17], (data[18] & Protocol.CallLinked) != 0,
                                         old?.Name ?? "", old?.UserName ?? "", DateTime.UtcNow);
                _byLe[e.BluetoothAddress] = id;
            }
        }
        return id;
    }

    // The scan response comes on its own: the gamepad's name, and the user's
    void ReadScanResponse(BluetoothLEAdvertisementReceivedEventArgs e, string? id)
    {
        string name = e.Advertisement.LocalName;
        string? user = null;
        foreach (var m in e.Advertisement.ManufacturerData)
            if (m.CompanyId == UserNameCompany)
                user = Utf8(Bytes(m.Data));
        if (name.Length == 0 && user == null)
            return;
        lock (_lock)
        {
            if (id == null && !_byLe.TryGetValue(e.BluetoothAddress, out id))
                return;
            if (!_seen.TryGetValue(id, out var s))
                return;
            _seen[id] = s with { Name = name.Length > 0 ? name : s.Name, UserName = user ?? s.UserName };
            KnownPads.SetNames(id, name.Length > 0 ? name : null, user);
        }
    }

    // --- Gamepads connected over Classic ---

    // Every few seconds: the devices connected to this PC over Classic, read through the call service
    async Task DirectLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_searching)
                    await ReadConnectedAsync();
            }
            catch (Exception e)
            {
                Log.Write($"Direct read: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
            }
            try { await Task.Delay(DirectReadEveryMs, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task ReadConnectedAsync()
    {
        var connected = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        foreach (var info in connected)
        {
            using var classic = await BluetoothDevice.FromIdAsync(info.Id);
            if (classic == null)
                continue;
            ulong address = classic.BluetoothAddress;
            if (!_searching || IsOpenHere(address.ToString("X12")))
                continue;
            lock (_lock)
                if (_notHoja.TryGetValue(address, out var until) && DateTime.UtcNow < until)
                    continue;
            if (!await ReadInfoAsync(address))
                lock (_lock)
                    _notHoja[address] = DateTime.UtcNow + NotHojaFor;
        }
    }

    // [version][mode][flags][Link address, 6][name length][name][user name length][user name].
    // False when the device has no call service.
    async Task<bool> ReadInfoAsync(ulong address)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device == null)
            return false;
        var services = await device.GetGattServicesForUuidAsync(Protocol.CallService,
                                                                BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            return false;
        using var service = services.Services[0];
        var chars = await service.GetCharacteristicsForUuidAsync(Protocol.CallInfo,
                                                                 BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
            return true; // A gamepad with older firmware: its advertisements still find it
        var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
        if (read.Status != GattCommunicationStatus.Success)
            return true;
        byte[] info = Bytes(read.Value);
        if (info.Length < CallInfoMinBytes || info[0] != Protocol.CallVersion)
            return true;

        string id = Convert.ToHexString(info, 3, 6);
        int at = 9;
        string name = LengthPrefixed(info, ref at), user = LengthPrefixed(info, ref at);
        lock (_lock)
        {
            _seen[id] = new Sighting(address, info[1], (info[2] & Protocol.CallLinked) != 0, name, user,
                                     DateTime.UtcNow);
            _byLe[address] = id;
        }
        KnownPads.SetNames(id, name.Length > 0 ? name : null, user);
        return true;
    }

    // --- Writes to the call service ---

    static async Task<string> CallAsync(ulong address, byte[] token)
    {
        var command = new byte[1 + Protocol.TokenLength];
        command[0] = Protocol.CallToLink;
        token.CopyTo(command, 1);
        string? error = await WriteAsync(address, command);
        return error ?? "Called: coming over";
    }

    // One write to a gamepad's call service; null when it took it. An LE connection can miss (the
    // gamepad's radio is also busy with its Classic link), so a few tries; a refusal is final.
    static async Task<string?> WriteAsync(ulong address, byte[] command)
    {
        string? error = null;
        for (int attempt = 0; attempt < WriteTries; attempt++)
        {
            (error, bool refused) = await WriteOnceAsync(address, command);
            if (error == null || refused)
                return error;
            Log.Write($"Call write to {address:X12}, try {attempt + 1}: {error}");
            await Task.Delay(WriteRetryMs);
        }
        return error;
    }

    static async Task<(string? Error, bool Refused)> WriteOnceAsync(ulong address, byte[] command)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device == null)
            return ("Couldn't reach it (no LE device)", false);
        var services = await device.GetGattServicesForUuidAsync(Protocol.CallService,
                                                                BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            return ($"Couldn't reach it (services: {services.Status}, {services.Services.Count})", false);
        using var service = services.Services[0];
        var chars = await service.GetCharacteristicsForUuidAsync(Protocol.CallCommand,
                                                                 BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
            return ($"Couldn't reach it (characteristics: {chars.Status})", false);

        var writer = new DataWriter();
        writer.WriteBytes(command);
        var result = await chars.Characteristics[0].WriteValueWithResultAsync(
            writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
        if (result.Status == GattCommunicationStatus.Success)
            return (null, false);
        if (result.ProtocolError != AttWriteNotPermitted)
            return ($"It didn't answer ({result.Status})", false);
        string refusal = command[0] == Protocol.CallPair
            ? "It's in use by another device: pair it while it isn't connected"
            : "It refused the call (paired with another PC?)";
        return (refusal, true);
    }

    // --- Helpers ---

    static byte[] Bytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }

    static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();

    // A [length][text] field at `at`, which moves past it; empty when the data ends first
    static string LengthPrefixed(byte[] b, ref int at)
    {
        if (at >= b.Length)
            return "";
        int length = Math.Min(b[at], b.Length - at - 1);
        string text = Utf8(b.AsSpan(at + 1, length));
        at += 1 + length;
        return text;
    }

    // 128-bit UUIDs go little-endian on the air
    static Guid UuidFromWire(ReadOnlySpan<byte> le)
    {
        Span<byte> be = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            be[i] = le[15 - i];
        return new Guid(be, bigEndian: true);
    }
}
