// A gamepad's Link HID collection (Protocol.cs), opened from user mode with hid.dll: no driver.
// Windows keeps a paired Bluetooth HID device present while the gamepad is away, so an open device
// is not a connected gamepad: reads just wait until it is back.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HhlGamepadLink;

sealed unsafe partial class LinkDevice : IDisposable
{
    const uint WriteTimeoutMs = 500;   // A write to a gamepad that is away can wait; give up on it
    const int SerialChars = 64;
    const uint DetailCbSize = 8;       // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
    const int DetailPathOffset = 4;    // Its DevicePath, after cbSize

    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3, FileFlagOverlapped = 0x40000000;
    const uint DigcfPresent = 2, DigcfDeviceInterface = 0x10;
    const int HidpStatusSuccess = 0x00110000;
    const int ErrorIoPending = 997;
    const uint Infinite = 0xFFFFFFFF;

    readonly SafeFileHandle _handle; // Overlapped: a read and a write can be in flight together
    readonly object _writeLock = new();
    volatile bool _closed;
    int _disposed;

    LinkDevice(string path, string id, SafeFileHandle handle, int inputLength, int outputLength)
    {
        Path = path;
        Id = id;
        _handle = handle;
        InputLength = inputLength;
        OutputLength = outputLength;
    }

    public string Path { get; }
    public string Id { get; }          // The gamepad's Link address, as Windows' HID serial number gives it
    public int InputLength { get; }
    public int OutputLength { get; }
    public bool Closed => _closed;

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
                if (InterfacePath(set, ref iface) is string path && IsLink(path))
                    paths.Add(path);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return paths;
    }

    static string? InterfacePath(nint set, ref SpDeviceInterfaceData iface)
    {
        uint needed = 0;
        SetupDiGetDeviceInterfaceDetailW(set, ref iface, null, 0, ref needed, 0);
        if (needed == 0)
            return null;
        var buffer = new byte[needed];
        fixed (byte* detail = buffer)
        {
            *(uint*)detail = DetailCbSize;
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, needed, ref needed, 0))
                return null;
            return new string((char*)(detail + DetailPathOffset));
        }
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
        char* serial = stackalloc char[SerialChars];
        string id = HidD_GetSerialNumberString(h, serial, SerialChars * sizeof(char))
            ? new string(serial).ToUpperInvariant()
            : path;
        return new LinkDevice(path, id, h, caps.InputReportByteLength, caps.OutputReportByteLength);
    }

    // The next input report (report ID first); null once the device is gone or closed
    public byte[]? Read()
    {
        var buffer = new byte[InputLength];
        if (_closed || !Transfer(false, buffer, Infinite, out uint n) || n == 0)
        {
            _closed = true;
            return null;
        }
        return buffer[..(int)n];
    }

    // Report 3: [lane][length][payload], padded to the report's size. A disconnected gamepad's
    // writes fail; the device stays, and works again when the gamepad is back.
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
            return Transfer(true, report, WriteTimeoutMs, out _);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _closed = true;
        CancelIoEx(_handle, null); // The reader's pending read ends
        _handle.Dispose();
    }

    // One overlapped transfer, waited for here; false when it failed, timed out or was cancelled.
    // Its buffer and OVERLAPPED are let go only once its event says the transfer has ended, and the
    // result is read from the OVERLAPPED, so a Dispose meanwhile (which cancels it) is safe.
    bool Transfer(bool write, byte[] buffer, uint timeoutMs, out uint done)
    {
        done = 0;
        var ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        nint ev = CreateEventW(0, true, false, null);
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            ov->EventHandle = ev;
            byte* b = (byte*)pin.AddrOfPinnedObject();
            uint n = 0;
            bool started;
            try
            {
                started = write ? WriteFile(_handle, b, (uint)buffer.Length, &n, ov)
                                : ReadFile(_handle, b, (uint)buffer.Length, &n, ov);
            }
            catch (ObjectDisposedException)
            {
                return false; // Closed before it started
            }
            if (!started && Marshal.GetLastPInvokeError() != ErrorIoPending)
                return false;
            if (WaitForSingleObject(ev, timeoutMs) != 0)
            {
                try { CancelIoEx(_handle, ov); }
                catch (ObjectDisposedException) { } // Dispose has cancelled it already
                WaitForSingleObject(ev, Infinite);   // Until it has ended
            }
            done = (uint)ov->InternalHigh;
            return ov->InternalLow == 0; // STATUS_SUCCESS
        }
        finally
        {
            pin.Free();
            CloseHandle(ev);
            NativeMemory.Free(ov);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength,
                      FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps,
                      NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps,
                      NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps,
                      NumberFeatureDataIndices;
    }

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid guid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle h, out nint preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(nint preparsed);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(nint preparsed, out HidpCaps caps);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetSerialNumberString(SafeFileHandle h, char* buffer, int length);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security,
                                                      uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(SafeFileHandle h, byte* buffer, uint length, uint* done,
                                         NativeOverlapped* ov);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle h, byte* buffer, uint length, uint* done,
                                          NativeOverlapped* ov);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle h, NativeOverlapped* ov);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateEventW(nint security, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
                                             [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint ms);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SetupDiGetClassDevsW(ref Guid guid, string? enumerator, nint parent,
                                                     uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index,
                                                            ref SpDeviceInterfaceData data);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetailW(nint set, ref SpDeviceInterfaceData data,
                                                                 byte* detail, uint size, ref uint needed,
                                                                 nint info);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);
}
