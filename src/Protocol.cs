// What a gamepad and this app say to each other over Bluetooth: the HOJA dongle link
// (HOJA-LIB-DONGLE include/dongle_link.h), with this app in the dongle's place. The gamepad pages
// this PC and opens an RFCOMM channel to the app's service; RFCOMM is one reliable byte stream, so
// each piece travels as a frame:
//
//   [lane u8][length u8][payload]
//
//   lane          direction      payload
//   ------------  -------------  ------------------------------------------------------------
//   1 input       gamepad -> PC  [seq u16][sampled_us u32][full length][report, trailing zeros cut]
//   2 bulk        gamepad -> PC  the gamepad's live WebUSB report, for the config app
//   3 message     both           [dongle_link_msg_t][payload]: HOST, WAKE, REPORT, OUTPUT, CONFIG
//   4 status      PC -> gamepad  dongle_status_s: rumble and player number
//
// Everything is little endian and packed.

using System.Buffers.Binary;
using System.Text;

namespace HhlGamepadLink;

static class Protocol
{
    // The app's RFCOMM service, published in SDP. Gamepads without an SDP lookup call
    // FixedChannel; the app binds it when it is free.
    public static readonly Guid ServiceUuid = new("bbebd868-289d-4930-81dd-45d882b64bbf");
    public const int FixedChannel = 5;
    public const string ServiceName = "HHL Gamepad Link";

    public const byte LaneInput = 1, LaneBulk = 2, LaneMessage = 3, LaneStatus = 4;

    // dongle_link_msg_t
    public const byte MsgHost = 1, MsgWake = 2, MsgReport = 3, MsgOutput = 4, MsgConfig = 5;

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

    public static byte[] Frame(byte lane, ReadOnlySpan<byte> payload)
    {
        var f = new byte[2 + payload.Length];
        f[0] = lane;
        f[1] = (byte)payload.Length;
        payload.CopyTo(f.AsSpan(2));
        return f;
    }

    public static byte[] Message(byte type, ReadOnlySpan<byte> payload)
    {
        var m = new byte[1 + Math.Min(payload.Length, 64)];
        m[0] = type;
        payload.Slice(0, m.Length - 1).CopyTo(m.AsSpan(1));
        return Frame(LaneMessage, m);
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
    public static byte[] Status(byte player) => Frame(LaneStatus, new byte[] { 1, player, 0, 0, 0, 0 });

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
