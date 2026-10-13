// What a gamepad and this app say to each other: the HOJA dongle link (HOJA-LIB-DONGLE
// include/dongle_link.h, "Link mode"), with this app in the dongle's place. The gamepad meets this
// PC as a Bluetooth HID device of its own ("<name> Link"): a vendor-defined collection, which any
// app opens without a driver, in three reports:
//
//   report   direction      layout
//   -------  -------------  ------------------------------------------------------------------
//   1 live   gamepad -> PC  [seq u16][sampled_us u32][length u8][report, 64]
//   2 msg    gamepad -> PC  [lane u8][length u8][payload]: the gamepad's messages and live WebUSB report
//   3 out    PC -> gamepad  [lane u8][length u8][payload]: this app's messages and status
//
//   lanes: 2 bulk (live WebUSB report), 3 message ([dongle_link_msg_t][payload]: HOST, WAKE,
//   REPORT, OUTPUT, CONFIG), 4 status (dongle_status_s)
//
// Everything is little endian and packed.

using System.Buffers.Binary;
using System.Text;

namespace HhlGamepadLink;

static class Protocol
{
    public const ushort UsagePage = 0xFF48, Usage = 0x4C;
    public const byte ReportLive = 1, ReportMessage = 2, ReportOut = 3;
    public const byte LaneBulk = 2, LaneMessage = 3, LaneStatus = 4;
    public const int MessageMax = 65; // DONGLE_LINK_MSG_MAX: a type byte and 64

    // dongle_link_msg_t
    public const byte MsgHost = 1, MsgWake = 2, MsgReport = 3, MsgOutput = 4, MsgConfig = 5, MsgToken = 7;

    // Calling a gamepad to Link (dongle_link.h): its LE advertisement and call service. The call is
    // [CallToLink][token, 16]; the gamepad hands this app its token over Link (MsgToken). Pairing is
    // [CallPair][this PC's address, as shown]: the gamepad pages this PC to pair.
    public static readonly Guid CallService = new("2a16dfe1-6d54-4505-91e8-c617ef490116");
    public static readonly Guid CallCommand = new("a210117a-ea18-4571-a927-4e6b5821b7cb");
    public static readonly Guid CallInfo = new("c864f062-886b-4079-bfcd-192dbd7a0a8c");
    public const byte CallVersion = 2, CallToLink = 1, CallPair = 2, CallLinked = 0x01;
    public const int TokenLength = 16;

    public const byte DongleProtocol = 3;   // DONGLE_PROTOCOL_VERSION
    public const byte BoardPcApp = 4;       // dongle_board_t: this app
    public const byte HostPc = 1;           // DONGLE_HOST_PC

    // core_reportformat_t on the gamepad: what PadSession works in
    public const byte FormatXInput = 1;
    public const byte FormatSlippi = 2;
    public const byte FormatSInput = 6;

    public static string FormatName(byte format) => format switch
    {
        0 => "Switch",
        FormatXInput => "XInput",
        FormatSlippi => "Slippi",
        3 => "GameCube",
        4 => "N64",
        5 => "SNES",
        FormatSInput => "SInput",
        7 => "Wii",
        _ => $"mode {format}",
    };

    // dongle_mode_t, as the link carries modes
    public static byte FormatFromMode(byte mode) => mode switch
    {
        0 => 0,            // Switch
        1 => FormatSInput,
        2 => FormatXInput,
        3 => FormatSlippi,
        4 => 5,            // SNES
        5 => 4,            // N64
        6 => 3,            // GameCube
        _ => 0xFF,
    };

    public static byte ModeFromFormat(byte format) => format switch
    {
        FormatSInput => 1,
        FormatXInput => 2,
        FormatSlippi => 3,
        _ => 15,           // DONGLE_MODE_UNKNOWN: the gamepad keeps its own
    };

    // A dongle_link message: [type][payload]
    public static byte[] Message(byte type, ReadOnlySpan<byte> payload)
    {
        var m = new byte[1 + Math.Min(payload.Length, MessageMax - 1)];
        m[0] = type;
        payload.Slice(0, m.Length - 1).CopyTo(m.AsSpan(1));
        return m;
    }

    // dongle_link_host_s: what this "dongle" is plugged into (a PC), the mode wanted for it, and
    // the report pacing
    public static byte[] Host(byte format, uint appVersion, int rateHz)
    {
        var p = new byte[10];
        p[0] = DongleProtocol;
        p[1] = BoardPcApp;
        p[2] = HostPc;
        p[3] = ModeFromFormat(format);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), appVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), (ushort)(1_000_000 / rateHz));
        return Message(MsgHost, p);
    }

    // dongle_status_s: rumble is passed through output reports, so only the player number
    public static byte[] Status(byte player) => new byte[] { 1, player, 0, 0, 0, 0 };

    public static string Text(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field.Slice(0, end)).Trim();
    }
}

// dongle_wake_s: the gamepad's identity, sent after the app's HOST and again after a mode change
sealed record PadIdentity(string Id, string Name, string Manufacturer, ushort Vid, ushort Pid, byte Format,
                          uint Firmware, bool ModeForced)
{
    public string Title => Name;

    public static PadIdentity? Parse(string id, ReadOnlySpan<byte> p)
    {
        if (p.Length < 64 || p[6] != Protocol.DongleProtocol)
            return null;
        ushort session = BinaryPrimitives.ReadUInt16LittleEndian(p);
        return new PadIdentity(
            Id: id,
            Name: Protocol.Text(p.Slice(12, 32)),
            Manufacturer: Protocol.Text(p.Slice(44, 20)),
            Vid: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(2)),
            Pid: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(4)),
            Format: Protocol.FormatFromMode((byte)(session & 0x0F)),
            Firmware: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(8)),
            ModeForced: (p[7] & 0x01) != 0);
    }
}
