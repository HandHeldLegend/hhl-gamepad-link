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
    // A gamepad seen advertising: where to reach it over LE, the mode it is in, and when
    public sealed record Sighting(ulong LeAddress, byte Format, DateTime Seen);

    readonly BluetoothLEAdvertisementWatcher _watcher;
    readonly object _lock = new();
    readonly Dictionary<string, Sighting> _seen = new(); // By Link address
    readonly Dictionary<string, string> _calls = new();  // Link address -> how its call went

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
        foreach (var section in e.Advertisement.DataSections)
        {
            // Service data, 128-bit UUID: [uuid LE][version][mode][Link address as shown]
            if (section.DataType != 0x21 || section.Data.Length < 24)
                continue;
            var data = new byte[section.Data.Length];
            DataReader.FromBuffer(section.Data).ReadBytes(data);
            if (UuidFromWire(data.AsSpan(0, 16)) != Protocol.CallService || data[16] != Protocol.CallVersion)
                continue;
            string id = Convert.ToHexString(data, 18, 6);
            lock (_lock)
                _seen[id] = new Sighting(e.BluetoothAddress, data[17], DateTime.UtcNow);
        }
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

    static async Task<string> CallAsync(ulong address, byte[] token)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device == null)
            return "Couldn't reach it";
        var services = await device.GetGattServicesForUuidAsync(Protocol.CallService, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            return "Couldn't reach it";
        var service = services.Services[0];
        try
        {
            var chars = await service.GetCharacteristicsForUuidAsync(Protocol.CallCommand, BluetoothCacheMode.Uncached);
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
                return "Couldn't reach it";
            var command = new byte[1 + Protocol.TokenLength];
            command[0] = Protocol.CallToLink;
            token.CopyTo(command, 1);
            var writer = new DataWriter();
            writer.WriteBytes(command);
            var result = await chars.Characteristics[0].WriteValueWithResultAsync(writer.DetachBuffer(),
                                                                                  GattWriteOption.WriteWithResponse);
            return result.Status == GattCommunicationStatus.Success ? "Called: coming over"
                 : result.ProtocolError == 0x03 ? "It refused the call (paired with another PC?)"
                 : "Call failed";
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
