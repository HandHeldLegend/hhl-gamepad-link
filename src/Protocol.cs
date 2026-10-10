// Wire format shared with the gamepad's LAN test build (src/hal/rp2040/wlan_lan_test.c).
// Everything is little endian and packed.

using System.Buffers.Binary;
using System.Text;

namespace HhlGamepadWlan;

static class Protocol
{
    public const int GamepadPort = 51700; // The gamepad listens here
    public const int PcPort = 51701;      // Beacons and the stream arrive here

    public const uint MagicOpen = 0x4E45504F;   // "OPEN"
    public const uint MagicClose = 0x534F4C43;  // "CLOS"
    public const uint MagicBeacon = 0x424A4F48; // "HOJB"
    public const uint MagicData = 0x3154414C;   // "LAT1"
    public const uint MagicOut = 0x3154554F;    // "OUT1": an output report from the host
    public const uint MagicConfig = 0x31474643; // "CFG1": a config app command
    public const uint MagicReply = 0x31524643;  // "CFR1": a config reply, echoed back as receipt
    public const uint MagicLive = 0x31494643;   // "CFI1": the config app's live input view

    public const int BeaconSize = 68;         // Without the user name (earlier firmware)
    public const int BeaconWithUserSize = 92; // With the name set in the config app
    public const int DataSize = 126;
    public const int OutSize = 72;
    const int PcNameLength = 16;

    // core_reportformat_t on the gamepad
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

    public static byte[] Open(uint periodUs, uint session, string pcName)
    {
        var p = new byte[12 + PcNameLength];
        BinaryPrimitives.WriteUInt32LittleEndian(p, MagicOpen);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), periodUs);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), session);
        Encoding.ASCII.GetBytes(pcName, 0, Math.Min(pcName.Length, PcNameLength - 1), p, 12);
        return p;
    }

    public static byte[] Close(uint session)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(p, MagicClose);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), session);
        return p;
    }

    // OUT1 and CFG1 share one layout: magic, token, length, then the 64-byte packet
    public static byte[] Message(uint magic, ushort token, ReadOnlySpan<byte> report)
    {
        var p = new byte[OutSize];
        int len = Math.Min(report.Length, 64);
        BinaryPrimitives.WriteUInt32LittleEndian(p, magic);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), token);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)len);
        report.Slice(0, len).CopyTo(p.AsSpan(8));
        return p;
    }

    public static string Text(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field.Slice(0, end)).Trim();
    }
}

// What a gamepad says about itself every second
sealed record Beacon(string Id, string Name, string UserName, ushort Vid, ushort Pid, byte Format, int Rssi,
                     uint OwnerSession, string OwnerName)
{
    // The name to show: the one set in the config app, else the model
    public string Title => UserName.Length > 0 ? UserName : Name;

    public static Beacon? Parse(ReadOnlySpan<byte> p)
    {
        if ((p.Length != Protocol.BeaconSize && p.Length != Protocol.BeaconWithUserSize) ||
            BinaryPrimitives.ReadUInt32LittleEndian(p) != Protocol.MagicBeacon)
            return null;
        return new Beacon(
            Id: Convert.ToHexString(p.Slice(4, 6)),
            Name: Protocol.Text(p.Slice(36, 32)),
            UserName: p.Length >= Protocol.BeaconWithUserSize ? Protocol.Text(p.Slice(68, 24)) : "",
            Vid: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(10)),
            Pid: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(12)),
            Format: p[14],
            Rssi: (sbyte)p[15],
            OwnerSession: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(16)),
            OwnerName: Protocol.Text(p.Slice(20, 16)));
    }
}

// One packet of the stream: the input report plus what the gamepad tracks about the link
readonly ref struct DataPacket
{
    public readonly ReadOnlySpan<byte> Report;
    public readonly long SentUs;          // The gamepad's clock when it sampled this
    public readonly uint RttUs;
    public readonly ushort ReliableToken; // Non-zero on a command reply, resent until echoed
    public readonly ushort OutAck;        // The last output report the gamepad handled
    public readonly byte Format;

    DataPacket(ReadOnlySpan<byte> p)
    {
        // magic, seq, sent_us, rtt_us, rtt_seq, rssi (28 bytes), report_size, report[64],
        // rel_token, out_ack, stall and link counters, and last the mode
        Report = p.Slice(29, Math.Min((int)p[28], 64));
        SentUs = (long)BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(8));
        RttUs = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(16));
        ReliableToken = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(93));
        OutAck = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(95));
        Format = p[125];
    }

    public static bool TryParse(ReadOnlySpan<byte> p, out DataPacket packet)
    {
        bool ok = p.Length == Protocol.DataSize && BinaryPrimitives.ReadUInt32LittleEndian(p) == Protocol.MagicData;
        packet = ok ? new DataPacket(p) : default;
        return ok;
    }
}
