// A gamepad's identity, from its WAKE message (dongle_wake_s): sent after the app's HOST and again
// after a mode change

using System.Buffers.Binary;
using System.Text;

namespace HhlGamepadLink;

sealed record PadIdentity(string Id, string Name, ushort Vid, ushort Pid, byte Format)
{
    const int WakeBytes = 64;

    // [session u16, mode in the low nibble][vid u16][pid u16][protocol][flags][firmware u32]
    // [name, 32][manufacturer, 20]
    public static PadIdentity? Parse(string id, ReadOnlySpan<byte> p)
    {
        if (p.Length < WakeBytes || p[6] != Protocol.DongleProtocol)
            return null;
        ushort session = BinaryPrimitives.ReadUInt16LittleEndian(p);
        return new PadIdentity(
            Id: id,
            Name: Text(p.Slice(12, 32)),
            Vid: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(2)),
            Pid: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(4)),
            Format: Protocol.FormatFromMode((byte)(session & 0x0F)));
    }

    // A fixed-size text field, ended by a zero when shorter
    static string Text(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field.Slice(0, end)).Trim();
    }
}
