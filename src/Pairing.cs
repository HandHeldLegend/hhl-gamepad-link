// Pairing a gamepad's Link mode from the app. Asked over LE (Caller), the gamepad's radio comes up
// as Link mode's and pages this PC, so nobody searches; on that link Windows pairs it (Just Works,
// accepted here), then the app turns on its HID service, which makes Windows connect to it. Then
// the gamepad restarts in Link mode, which calls this PC with its key in a few seconds.
// Failing that, the app pairs it as Windows' Add device would: pages it (it is findable too).
// Windows' own Bluetooth API (bthprops), user mode.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HhlGamepadLink;

static unsafe partial class Pairing
{
    const uint MitmNotRequiredGeneralBonding = 4, ServiceEnable = 1;
    const int DirectTries = 4;       // Pages to its address before searching for it
    const int PagedSeconds = 20;     // The gamepad's radio restarts, then pages up to 3 x 5.12 s
    const int SlowPairMs = 3000;     // Pairing on its link that takes longer than this: say so
    const int LinkPollMs = 100;
    const int RetryMs = 1000;
    const byte InquiryUnits = 4;     // 4 x 1.28 s a round
    static readonly Guid HidService = new("00001124-0000-1000-8000-00805f9b34fb");

    // Whether Windows holds a pairing for this address (as shown). Removed in Settings, it doesn't,
    // and the gamepad can't connect to this PC however it is called.
    public static bool IsPaired(string address) => IsPaired(Convert.ToUInt64(address, 16));

    // Pair the gamepad at this address (as shown, "6AF4B0E017F2"): `ask` asks it to pair with this
    // PC (null when it took that), then it pages here; failing that, it is paged for up to
    // `seconds`. `progress` hears each step, for the card. Returns null when paired and its HID
    // service is on, else what went wrong.
    public static string? PairAsked(string address, Func<string?> ask, int seconds, Action<string> progress)
    {
        ulong addr = Convert.ToUInt64(address, 16);
        Forget(addr);

        var sinceAsked = Stopwatch.StartNew();
        var info = NewInfo(addr);
        uint r = BluetoothRegisterForAuthenticationEx(ref info, out nint registration, &OnAuthentication, 0);
        if (r != 0)
            return $"Couldn't take the pairing request (error {r})";
        try
        {
            progress("Pairing 1/4: asking it…");
            string? error = ask();
            if (error != null)
                return error;
            progress("Pairing 2/4: it's calling this PC…");
            if (WaitForLink(addr, out var link))
            {
                long ms = sinceAsked.ElapsedMilliseconds;
                Log.Write($"Pairing {address}: its link is up, {ms} ms after asking");
                r = PairOnLink(ref link, progress);
                if (r != 0)
                    Log.Write($"Pairing {address}: pairing on its link failed (error {r})");
            }
        }
        finally
        {
            BluetoothUnregisterAuthentication(registration);
        }
        if (!IsPaired(addr))
        {
            Log.Write($"Pairing {address}: not paired on its own link, paging it");
            progress("Pairing: looking for it…");
            return Pair(addr, seconds);
        }
        Log.Write($"Pairing {address}: paired, {sinceAsked.ElapsedMilliseconds} ms after asking");
        progress("Pairing 4/4: paired, setting it up on Windows…");
        return EnableHid(addr);
    }

    static DeviceInfo NewInfo(ulong addr) => new() { dwSize = (uint)sizeof(DeviceInfo), Address = addr };

    static bool IsPaired(ulong addr)
    {
        var info = NewInfo(addr);
        return BluetoothGetDeviceInfo(0, ref info) == 0 && info.fAuthenticated != 0;
    }

    // The gamepad's page to this PC: its link up within PagedSeconds
    static bool WaitForLink(ulong addr, out DeviceInfo link)
    {
        var until = DateTime.UtcNow.AddSeconds(PagedSeconds);
        while (DateTime.UtcNow < until)
        {
            link = NewInfo(addr);
            if (BluetoothGetDeviceInfo(0, ref link) == 0 && link.fConnected != 0)
                return true;
            Thread.Sleep(LinkPollMs);
        }
        link = default;
        return false;
    }

    // Windows pairs on the gamepad's link: about a second (measured on a GCU-2)
    static uint PairOnLink(ref DeviceInfo link, Action<string> progress)
    {
        progress("Pairing 3/4: connected, pairing…");
        bool waiting = true;
        using var slow = new Timer(_ =>
        {
            if (waiting)
                progress("Pairing 3/4: connected, pairing (taking longer than usual)…");
        }, null, SlowPairMs, Timeout.Infinite);
        uint r = Authenticate(ref link);
        waiting = false;
        return r;
    }

    // Pair the device at this address, paging it for up to `seconds`. Returns null when paired and
    // its HID service is on, else what went wrong.
    static string? Pair(ulong addr, int seconds)
    {
        Forget(addr);

        // The address is known, so Windows pages it straight away rather than searching (an inquiry
        // takes 5 s a round). It may still be coming up in pairing: tried again until the time is
        // up, searching after a few direct tries in case the direct way doesn't take.
        var until = DateTime.UtcNow.AddSeconds(seconds);
        uint r = 0;
        for (int attempt = 0; ; attempt++)
        {
            int left = (int)(until - DateTime.UtcNow).TotalSeconds;
            if (left <= 0)
                return r == 0 ? "It didn't show up to pair" : $"Pairing failed (error {r})";
            DeviceInfo info;
            if (attempt < DirectTries)
                info = NewInfo(addr);
            else if (!Find(addr, left, out info))
                return "It didn't show up to pair";

            r = BluetoothRegisterForAuthenticationEx(ref info, out nint registration, &OnAuthentication, 0);
            if (r != 0)
                return $"Couldn't take the pairing request (error {r})";
            try
            {
                r = Authenticate(ref info);
            }
            finally
            {
                BluetoothUnregisterAuthentication(registration);
            }
            if (r == 0)
                return EnableHid(addr);
            Thread.Sleep(RetryMs);
        }
    }

    static uint Authenticate(ref DeviceInfo info) =>
        BluetoothAuthenticateDeviceEx(0, 0, ref info, 0, MitmNotRequiredGeneralBonding);

    // A pairing left from before (the gamepad forgot it, or a new descriptor) can't be reused
    static void Forget(ulong addr)
    {
        if (IsPaired(addr))
            BluetoothRemoveDevice(&addr);
    }

    // Paired: its HID service on, which makes Windows connect to it
    static string? EnableHid(ulong addr)
    {
        var info = NewInfo(addr);
        uint r = BluetoothGetDeviceInfo(0, ref info);
        if (r == 0)
        {
            Guid hid = HidService;
            r = BluetoothSetServiceState(0, ref info, ref hid, ServiceEnable);
        }
        return r == 0 ? null : $"Paired, but its HID service didn't turn on (error {r})";
    }

    // Inquiry rounds until the device answers
    static bool Find(ulong addr, int seconds, out DeviceInfo found)
    {
        found = default;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var search = new SearchParams
            {
                dwSize = (uint)sizeof(SearchParams),
                fReturnAuthenticated = 1, fReturnRemembered = 1, fReturnUnknown = 1, fReturnConnected = 1,
                fIssueInquiry = 1, cTimeoutMultiplier = InquiryUnits,
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
        uint r = BluetoothSendAuthenticationResponseEx(0, &response);
        Log.Write($"Pairing {p->deviceInfo.Address:X12}: request (method {p->authenticationMethod}, " +
                  $"io {p->ioCapability}) accepted: {r}");
        return 1;
    }

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

    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothGetDeviceInfo(nint radio, ref DeviceInfo info);

    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothRemoveDevice(ulong* address);

    [LibraryImport("bthprops.cpl")]
    private static partial nint BluetoothFindFirstDevice(ref SearchParams p, ref DeviceInfo info);

    [LibraryImport("bthprops.cpl")]
    private static partial int BluetoothFindNextDevice(nint find, ref DeviceInfo info);

    [LibraryImport("bthprops.cpl")]
    private static partial int BluetoothFindDeviceClose(nint find);

    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothRegisterForAuthenticationEx(
        ref DeviceInfo info, out nint registration,
        delegate* unmanaged[Stdcall]<nint, AuthParams*, int> callback, nint context);

    [LibraryImport("bthprops.cpl")]
    private static partial int BluetoothUnregisterAuthentication(nint registration);

    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothSendAuthenticationResponseEx(nint radio, AuthResponse* response);

    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothAuthenticateDeviceEx(nint parent, nint radio, ref DeviceInfo info,
                                                              nint oob, uint requirements);

    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothSetServiceState(nint radio, ref DeviceInfo info, ref Guid service,
                                                         uint flags);
}
