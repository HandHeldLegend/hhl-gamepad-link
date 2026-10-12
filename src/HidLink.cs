// A gamepad's Link HID collection (Protocol.cs), opened from user mode with hid.dll: no driver.
// Windows keeps a paired Bluetooth HID device present while the gamepad is away, so an open device
// is not a connected gamepad: reads just wait until it is back.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HhlGamepadLink;

sealed unsafe partial class LinkDevice : IDisposable
{
    public string Path { get; }
    public string Id { get; }          // The gamepad's Link address, as Windows' HID serial number gives it
    public int InputLength { get; }
    public int OutputLength { get; }

    readonly SafeFileHandle _handle; // Overlapped: a read and a write can be in flight together (RandomAccess)
    readonly object _writeLock = new();
    volatile bool _closed;
    public bool Closed => _closed;

    LinkDevice(string path, string id, SafeFileHandle handle, int inputLength, int outputLength)
    {
        Path = path;
        Id = id;
        _handle = handle;
        InputLength = inputLength;
        OutputLength = outputLength;
    }

    // Every present HID collection with the Link usage
    public static List<string> FindPaths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out Guid guid);
        nint set = SetupDiGetClassDevsW(ref guid, null, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == -1)
            return paths;
        try
        {
            var iface = new SpDeviceInterfaceData { cbSize = sizeof(SpDeviceInterfaceData) };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref iface); i++)
            {
                uint needed = 0;
                SetupDiGetDeviceInterfaceDetailW(set, ref iface, null, 0, ref needed, 0);
                if (needed == 0)
                    continue;
                byte* detail = stackalloc byte[(int)needed];
                *(uint*)detail = 8; // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, needed, ref needed, 0))
                    continue;
                string path = new string((char*)(detail + 4));
                if (IsLink(path))
                    paths.Add(path);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return paths;
    }

    static bool IsLink(string path)
    {
        using var h = CreateFileW(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        return !h.IsInvalid && Caps(h) is { UsagePage: Protocol.UsagePage, Usage: Protocol.Usage };
    }

    static HidpCaps? Caps(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out nint pre))
            return null;
        try
        {
            return HidP_GetCaps(pre, out HidpCaps caps) == HidpStatusSuccess ? caps : null;
        }
        finally
        {
            HidD_FreePreparsedData(pre);
        }
    }

    public static LinkDevice? Open(string path)
    {
        var h = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting,
                            FileFlagOverlapped, 0);
        if (h.IsInvalid)
            return null;
        if (Caps(h) is not HidpCaps caps)
        {
            h.Dispose();
            return null;
        }
        char* serial = stackalloc char[64];
        string id = HidD_GetSerialNumberString(h, serial, 128) ? new string(serial).ToUpperInvariant() : path;
        return new LinkDevice(path, id, h, caps.InputReportByteLength, caps.OutputReportByteLength);
    }

    // The next input report (report ID first); null once the device is gone or closed
    public byte[]? Read()
    {
        var buffer = new byte[InputLength];
        try
        {
            int n = RandomAccess.Read(_handle, buffer, 0);
            return n > 0 ? buffer[..n] : null;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or UnauthorizedAccessException)
        {
            _closed = true;
            return null;
        }
    }

    // Report 3: [lane][length][payload], padded to the report's size
    public bool Send(byte lane, ReadOnlySpan<byte> payload)
    {
        if (_closed || payload.Length > Protocol.MessageMax || OutputLength < 3 + payload.Length)
            return false;
        var report = new byte[OutputLength];
        report[0] = Protocol.ReportOut;
        report[1] = lane;
        report[2] = (byte)payload.Length;
        payload.CopyTo(report.AsSpan(3));
        lock (_writeLock)
        {
            try
            {
                RandomAccess.Write(_handle, report, 0);
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A disconnected gamepad's writes fail; the device stays, and works when it is back
                return false;
            }
        }
    }

    public void Dispose()
    {
        _closed = true;
        _handle.Dispose();
    }

    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3, FileFlagOverlapped = 0x40000000;
    const uint DigcfPresent = 2, DigcfDeviceInterface = 0x10;
    const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceData { public int cbSize; public Guid InterfaceClassGuid; public uint Flags; public nint Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                      NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps,
                      NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [LibraryImport("hid.dll")] private static partial void HidD_GetHidGuid(out Guid guid);
    [LibraryImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle h, out nint preparsed);
    [LibraryImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(nint preparsed);
    [LibraryImport("hid.dll")] private static partial int HidP_GetCaps(nint preparsed, out HidpCaps caps);
    [LibraryImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetSerialNumberString(SafeFileHandle h, char* buffer, int length);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation,
                                                      uint flags, nint template);
    [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SetupDiGetClassDevsW(ref Guid guid, string? enumerator, nint parent, uint flags);
    [LibraryImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index,
                                                            ref SpDeviceInterfaceData data);
    [LibraryImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetailW(nint set, ref SpDeviceInterfaceData data, byte* detail,
                                                                 uint size, ref uint needed, nint info);
    [LibraryImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);
}
