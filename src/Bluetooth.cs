// Windows' Bluetooth sockets (Winsock, AF_BTH): an RFCOMM server for the app's service, published
// in SDP so a gamepad can look up its channel. Plain user-mode sockets: no driver.

using System.Runtime.InteropServices;

namespace HhlGamepadLink;

sealed class BluetoothException(string what, int error) : Exception($"{what} failed (Winsock error {error})")
{
    public int Error { get; } = error;
}

// One RFCOMM socket: the listening one, or a gamepad's connection
sealed unsafe partial class RfcommSocket : IDisposable
{
    const int AfBth = 32, SockStream = 1, BthProtoRfcomm = 3;
    const uint BtPortAny = 0xFFFFFFFF;
    const int SockaddrBthSize = 30; // packed: family u16, address u64, service GUID, port u32
    const int NsBth = 16, RnrServiceRegister = 0, RnrServiceDelete = 2;
    static readonly nint InvalidSocket = -1;

    nint _s;
    readonly object _sendLock = new();
    public ulong Peer { get; }  // The gamepad's address (connections only)
    public int Channel { get; private set; }

    static RfcommSocket()
    {
        byte* data = stackalloc byte[512]; // WSADATA
        int err = WSAStartup(0x0202, data);
        if (err != 0)
            throw new BluetoothException("WSAStartup", err);
    }

    RfcommSocket(nint s, ulong peer)
    {
        _s = s;
        Peer = peer;
    }

    // Listen on `channel`, or on any free one if it is taken
    public static RfcommSocket Listen(int channel)
    {
        nint s = socket(AfBth, SockStream, BthProtoRfcomm);
        if (s == InvalidSocket)
            throw new BluetoothException("Opening a Bluetooth socket", WSAGetLastError());
        var listener = new RfcommSocket(s, 0);
        try
        {
            if (!listener.TryBind((uint)channel) && !listener.TryBind(BtPortAny))
                throw new BluetoothException("Binding an RFCOMM channel", WSAGetLastError());
            if (listen(s, 4) != 0)
                throw new BluetoothException("Listening", WSAGetLastError());

            byte* addr = stackalloc byte[SockaddrBthSize];
            int len = SockaddrBthSize;
            if (getsockname(s, addr, ref len) != 0)
                throw new BluetoothException("Reading the channel", WSAGetLastError());
            listener.Channel = (int)*(uint*)(addr + 26);
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    bool TryBind(uint port)
    {
        byte* addr = stackalloc byte[SockaddrBthSize];
        new Span<byte>(addr, SockaddrBthSize).Clear();
        *(ushort*)addr = AfBth;
        *(uint*)(addr + 26) = port;
        return bind(_s, addr, SockaddrBthSize) == 0;
    }

    // Blocks until a gamepad connects; null once the socket is closed
    public RfcommSocket? Accept()
    {
        byte* addr = stackalloc byte[SockaddrBthSize];
        int len = SockaddrBthSize;
        nint c = accept(_s, addr, ref len);
        if (c == InvalidSocket)
            return null;
        return new RfcommSocket(c, *(ulong*)(addr + 2));
    }

    // What arrived, 0 when the gamepad closed the connection or it dropped
    public int Receive(Span<byte> buffer)
    {
        fixed (byte* b = buffer)
        {
            int n = recv(_s, b, buffer.Length, 0);
            return n < 0 ? 0 : n;
        }
    }

    public bool Send(ReadOnlySpan<byte> data)
    {
        lock (_sendLock)
        {
            fixed (byte* b = data)
            {
                int done = 0;
                while (done < data.Length)
                {
                    int n = send(_s, b + done, data.Length - done, 0);
                    if (n <= 0)
                        return false;
                    done += n;
                }
            }
            return true;
        }
    }

    public void Dispose()
    {
        nint s = Interlocked.Exchange(ref _s, InvalidSocket);
        if (s != InvalidSocket)
            closesocket(s);
    }

    // The service's SDP record, built by Windows from the listening socket's address. Removed again
    // with Unpublish (and by Windows when the app exits).
    public void Publish(Guid service, string name) => SetService(service, name, RnrServiceRegister);

    public void Unpublish(Guid service, string name) => SetService(service, name, RnrServiceDelete);

    void SetService(Guid service, string name, int op)
    {
        byte* addr = stackalloc byte[SockaddrBthSize];
        int len = SockaddrBthSize;
        if (getsockname(_s, addr, ref len) != 0)
            throw new BluetoothException("Reading the channel", WSAGetLastError());

        var csa = new CsAddrInfo
        {
            LocalAddr = new SocketAddress { Sockaddr = (nint)addr, Length = SockaddrBthSize },
            SocketType = SockStream,
            Protocol = BthProtoRfcomm,
        };
        fixed (char* n = name)
        {
            var q = new WsaQuerySet
            {
                Size = (uint)sizeof(WsaQuerySet),
                ServiceInstanceName = (nint)n,
                ServiceClassId = (nint)(&service),
                NameSpace = NsBth,
                NumberOfCsAddrs = 1,
                CsaBuffer = (nint)(&csa),
            };
            if (WSASetServiceW(&q, op, 0) != 0)
                throw new BluetoothException("Publishing the service", WSAGetLastError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SocketAddress { public nint Sockaddr; public int Length; }

    [StructLayout(LayoutKind.Sequential)]
    struct CsAddrInfo { public SocketAddress LocalAddr, RemoteAddr; public int SocketType, Protocol; }

    [StructLayout(LayoutKind.Sequential)]
    struct WsaQuerySet
    {
        public uint Size;
        public nint ServiceInstanceName, ServiceClassId, Version, Comment;
        public uint NameSpace;
        public nint NsProviderId, Context;
        public uint NumberOfProtocols;
        public nint AfpProtocols, QueryString;
        public uint NumberOfCsAddrs;
        public nint CsaBuffer;
        public uint OutputFlags;
        public nint Blob;
    }

    [LibraryImport("ws2_32.dll")] private static partial int WSAStartup(ushort version, byte* data);
    [LibraryImport("ws2_32.dll")] private static partial int WSAGetLastError();
    [LibraryImport("ws2_32.dll")] private static partial nint socket(int af, int type, int protocol);
    [LibraryImport("ws2_32.dll")] private static partial int bind(nint s, byte* addr, int len);
    [LibraryImport("ws2_32.dll")] private static partial int listen(nint s, int backlog);
    [LibraryImport("ws2_32.dll")] private static partial nint accept(nint s, byte* addr, ref int len);
    [LibraryImport("ws2_32.dll")] private static partial int getsockname(nint s, byte* addr, ref int len);
    [LibraryImport("ws2_32.dll")] private static partial int recv(nint s, byte* buf, int len, int flags);
    [LibraryImport("ws2_32.dll")] private static partial int send(nint s, byte* buf, int len, int flags);
    [LibraryImport("ws2_32.dll")] private static partial int closesocket(nint s);
    [LibraryImport("ws2_32.dll")] private static partial int WSASetServiceW(WsaQuerySet* query, int op, uint flags);
}
