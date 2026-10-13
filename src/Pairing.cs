// Pairing a gamepad's Link mode from the app, as Windows' Add device would: find "<name> Link"
// (an inquiry), pair it (Just Works, accepted here), then turn on its HID service, which makes
// Windows connect to it. Windows' own Bluetooth API (bthprops), user mode.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HhlGamepadLink;

static unsafe partial class Pairing
{
    // Whether Windows holds a pairing for this address (as shown). Removed in Settings, it doesn't, and
    // the gamepad can't connect to this PC however it is called.
    public static bool IsPaired(string address)
    {
        var info = new DeviceInfo { dwSize = (uint)sizeof(DeviceInfo), Address = Convert.ToUInt64(address, 16) };
        return BluetoothGetDeviceInfo(0, ref info) == 0 && info.fAuthenticated != 0;
    }

    // Pair the device at this address (as shown, "6AF4B0E017F2"), searching for up to `seconds`.
    // Returns null when paired and its HID service is on, else what went wrong.
    public static string? Pair(string address, int seconds, CancellationToken cancel)
    {
        ulong addr = Convert.ToUInt64(address, 16);
        var info = new DeviceInfo { dwSize = (uint)sizeof(DeviceInfo), Address = addr };

        // A pairing left from before (the gamepad forgot it, or a new descriptor) can't be reused
        if (BluetoothGetDeviceInfo(0, ref info) == 0 && info.fAuthenticated != 0)
            BluetoothRemoveDevice(&addr);

        // The search can answer from Windows' cache before the gamepad is up in pairing, so a failed
        // attempt is tried again until the time is up
        var until = DateTime.UtcNow.AddSeconds(seconds);
        uint r = 0;
        while (true)
        {
            int left = (int)(until - DateTime.UtcNow).TotalSeconds;
            if (left <= 0 || cancel.IsCancellationRequested)
                return r == 0 ? "It didn't show up to pair" : $"Pairing failed (error {r})";
            if (!Find(addr, left, cancel, out info))
                return "It didn't show up to pair";

            nint registration;
            r = BluetoothRegisterForAuthenticationEx(ref info, out registration, &OnAuthentication, 0);
            if (r != 0)
                return $"Couldn't take the pairing request (error {r})";
            try
            {
                r = BluetoothAuthenticateDeviceEx(0, 0, ref info, 0, MitmNotRequiredGeneralBonding);
            }
            finally
            {
                BluetoothUnregisterAuthentication(registration);
            }
            if (r == 0)
                break;
            Thread.Sleep(1000);
        }

        Guid hid = HidService;
        r = BluetoothSetServiceState(0, ref info, ref hid, ServiceEnable);
        return r == 0 ? null : $"Paired, but its HID service didn't turn on (error {r})";
    }

    // Inquiry rounds (about 5 s each) until the device answers
    static bool Find(ulong addr, int seconds, CancellationToken cancel, out DeviceInfo found)
    {
        found = default;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !cancel.IsCancellationRequested)
        {
            var search = new SearchParams
            {
                dwSize = (uint)sizeof(SearchParams),
                fReturnAuthenticated = 1, fReturnRemembered = 1, fReturnUnknown = 1, fReturnConnected = 1,
                fIssueInquiry = 1, cTimeoutMultiplier = 4, // 4 x 1.28 s
            };
            var info = new DeviceInfo { dwSize = (uint)sizeof(DeviceInfo) };
            nint h = BluetoothFindFirstDevice(ref search, ref info);
            if (h == 0)
                continue;
            try
            {
                do
                {
                    if (info.Address == addr)
                    {
                        found = info;
                        return true;
                    }
                    info.dwSize = (uint)sizeof(DeviceInfo);
                } while (BluetoothFindNextDevice(h, ref info) != 0);
            }
            finally
            {
                BluetoothFindDeviceClose(h);
            }
        }
        return false;
    }

    // Just Works: accept. Only devices this app is pairing reach here (registered per device).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int OnAuthentication(nint context, AuthParams* p)
    {
        var response = new AuthResponse
        {
            bthAddressRemote = p->deviceInfo.Address,
            authMethod = p->authenticationMethod,
            negativeResponse = 0,
        };
        *(uint*)response.value = p->numericValue;
        BluetoothSendAuthenticationResponseEx(0, &response);
        return 1;
    }

    const uint MitmNotRequiredGeneralBonding = 4, ServiceEnable = 1;
    static readonly Guid HidService = new("00001124-0000-1000-8000-00805f9b34fb");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DeviceInfo
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected, fRemembered, fAuthenticated;
        public fixed ushort stLastSeen[8];
        public fixed ushort stLastUsed[8];
        public fixed char szName[248];
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SearchParams
    {
        public uint dwSize;
        public int fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
        public byte cTimeoutMultiplier;
        public nint hRadio;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AuthParams
    {
        public DeviceInfo deviceInfo;
        public int authenticationMethod, ioCapability, authenticationRequirements;
        public uint numericValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AuthResponse
    {
        public ulong bthAddressRemote;
        public int authMethod;
        public fixed byte value[32];
        public byte negativeResponse;
    }

    [LibraryImport("bthprops.cpl")] private static partial uint BluetoothGetDeviceInfo(nint radio, ref DeviceInfo info);
    [LibraryImport("bthprops.cpl")] private static partial uint BluetoothRemoveDevice(ulong* address);
    [LibraryImport("bthprops.cpl")] private static partial nint BluetoothFindFirstDevice(ref SearchParams p, ref DeviceInfo info);
    [LibraryImport("bthprops.cpl")] private static partial int BluetoothFindNextDevice(nint find, ref DeviceInfo info);
    [LibraryImport("bthprops.cpl")] private static partial int BluetoothFindDeviceClose(nint find);
    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothRegisterForAuthenticationEx(ref DeviceInfo info, out nint registration,
        delegate* unmanaged[Stdcall]<nint, AuthParams*, int> callback, nint context);
    [LibraryImport("bthprops.cpl")] private static partial int BluetoothUnregisterAuthentication(nint registration);
    [LibraryImport("bthprops.cpl")] private static partial uint BluetoothSendAuthenticationResponseEx(nint radio, AuthResponse* response);
    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothAuthenticateDeviceEx(nint parent, nint radio, ref DeviceInfo info, nint oob, uint requirements);
    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothSetServiceState(nint radio, ref DeviceInfo info, ref Guid service, uint flags);
}
