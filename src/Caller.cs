// Calling gamepads over to Link from wherever they are connected (dongle_link.h, "Calling it to
// Link"): a gamepad with a Link pairing advertises over Bluetooth LE in its direct Bluetooth modes,
// this app watches for that, and a call (the gamepad's token, written to its call service) brings it
// over. All user mode (WinRT).

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace HhlGamepadLink;

sealed class Caller : IDisposable
{
    // A gamepad seen advertising: where to reach it over LE, the mode it is in, whether it has a Link
    // pairing (with some PC), its names (from the scan response, once seen), and when
    public sealed record Sighting(ulong LeAddress, byte Format, bool Linked, string Name, string UserName, DateTime Seen);

    readonly BluetoothLEAdvertisementWatcher _watcher;
    readonly object _lock = new();
    readonly Dictionary<string, Sighting> _seen = new();    // By Link address
    readonly Dictionary<ulong, string> _byLe = new();      // LE address -> Link address (scan responses)
    readonly Dictionary<string, string> _calls = new();    // Link address -> how its call or pairing went

    public Caller()
    {
        // Active: the radio asks as well as listens, which finds a gamepad sooner
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += OnReceived;
        try { _watcher.Start(); }
        catch (Exception e) { Log.Write($"LE watcher: {e.Message}"); } // No LE radio: no calls
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        string? id = null;
        foreach (var section in e.Advertisement.DataSections)
        {
            // Service data, 128-bit UUID: [uuid LE][version][mode][flags][Link address as shown]
            if (section.DataType != 0x21 || section.Data.Length < 25)
                continue;
            var data = new byte[section.Data.Length];
            DataReader.FromBuffer(section.Data).ReadBytes(data);
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

        // The scan response comes on its own: the gamepad's name, and the user's (manufacturer data
        // of company 0xFFFF)
        string name = e.Advertisement.LocalName;
        string? user = null;
        foreach (var m in e.Advertisement.ManufacturerData)
        {
            if (m.CompanyId != 0xFFFF)
                continue;
            var bytes = new byte[m.Data.Length];
            DataReader.FromBuffer(m.Data).ReadBytes(bytes);
            user = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
        }
        if (name.Length == 0 && user == null)
            return;
        lock (_lock)
        {
            if (id == null && !_byLe.TryGetValue(e.BluetoothAddress, out id))
                return;
            if (_seen.TryGetValue(id, out var s))
            {
                _seen[id] = s with { Name = name.Length > 0 ? name : s.Name, UserName = user ?? s.UserName };
                if (user != null)
                    KnownPads.SetUserName(id, user);
            }
        }
    }

    // Every gamepad seen within the window
    public List<(string Id, Sighting Seen)> Nearby(TimeSpan within)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
            return _seen.Where(s => now - s.Value.Seen < within).Select(s => (s.Key, s.Value)).ToList();
    }

    public void SetStatus(string id, string status)
    {
        lock (_lock)
            _calls[id] = status;
    }

    // 128-bit UUIDs go little-endian on the air
    static Guid UuidFromWire(ReadOnlySpan<byte> le)
    {
        Span<byte> be = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            be[i] = le[15 - i];
        return new Guid(be, bigEndian: true);
    }

    // Where a gamepad was last seen, if within the last few seconds
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

    // Call a gamepad over: in the background, as it takes a few seconds
    public void Call(string id, byte[] token)
    {
        Sighting? seen = Seen(id, TimeSpan.FromSeconds(30));
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

    // Ask a gamepad to pair: it waits for its pairing combo. True when it took the request.
    public async Task<string?> AskToPairAsync(string id)
    {
        Sighting? seen = Seen(id, TimeSpan.FromSeconds(90));
        if (seen == null)
            return "Not nearby";
        try { return await WriteAsync(seen.LeAddress, new[] { Protocol.CallPair }); }
        catch (Exception e) { return $"Couldn't reach it: {e.Message}"; }
    }

    static async Task<string> CallAsync(ulong address, byte[] token)
    {
        var command = new byte[1 + Protocol.TokenLength];
        command[0] = Protocol.CallToLink;
        token.CopyTo(command, 1);
        string? error = await WriteAsync(address, command);
        return error ?? "Called: coming over";
    }

    // One write to a gamepad's call service; null when it took it. An LE connection can miss (the
    // gamepad's radio is also busy with its Classic link), so a few tries.
    static async Task<string?> WriteAsync(ulong address, byte[] command)
    {
        string? error = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            error = await WriteOnceAsync(address, command);
            if (error == null || error.StartsWith("It refused") || error.StartsWith("It can't"))
                return error;
            Log.Write($"Call write to {address:X12}, try {attempt + 1}: {error}");
            await Task.Delay(500);
        }
        return error;
    }

    static async Task<string?> WriteOnceAsync(ulong address, byte[] command)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device == null)
            return "Couldn't reach it (no LE device)";
        var services = await device.GetGattServicesForUuidAsync(Protocol.CallService, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            return $"Couldn't reach it (services: {services.Status}, {services.Services.Count})";
        var service = services.Services[0];
        try
        {
            var chars = await service.GetCharacteristicsForUuidAsync(Protocol.CallCommand, BluetoothCacheMode.Uncached);
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                return $"Couldn't reach it (characteristics: {chars.Status})";
            var writer = new DataWriter();
            writer.WriteBytes(command);
            var result = await chars.Characteristics[0].WriteValueWithResultAsync(writer.DetachBuffer(),
                                                                                  GattWriteOption.WriteWithResponse);
            return result.Status == GattCommunicationStatus.Success ? null
                 : result.ProtocolError == 0x03 ? (command[0] == Protocol.CallPair
                                                    ? "It refused to pair"
                                                    : "It refused the call (paired with another PC?)")
                 : $"It didn't answer ({result.Status})";
        }
        finally
        {
            service.Dispose();
        }
    }

    public void Dispose()
    {
        try { _watcher.Stop(); }
        catch (Exception) { }
    }
}
