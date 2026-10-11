// The devices a gamepad can appear as on this PC, picked by its mode

using System.Buffers.Binary;
using HIDMaestro;

namespace HhlGamepadWlan;

static class Personas
{
    // SInput's report descriptor, as SINPUT-LIB-HID declares it (k_sinput_hid_report_descriptor):
    // input 0x01, command replies 0x02, output commands 0x03. The same for every HOJA pad.
    const string SInputDescriptor =
        "05010905A10185010600FF0901150025FF75089502810205091901292015002501750195208102050109300931" +
        "093209350933093416008026FF7F7510950681020600FF0920150026FFFF752095018102092116008026FF7F75" +
        "10950681020922150026FF007508951D810285020923150026FF007508953F810285030924150026FF00750895" +
        "2F9102C0";

    // The gamepad's own identity. SDL (Steam Input) tells the bus from the parent's compatible
    // IDs: a root device reads as an unknown bus and SDL's SInput driver passes it by. Presented
    // as Bluetooth, which SInput also runs over, it is picked up like a real wireless pad.
    public static HMProfile SInput(Beacon pad) => new HMProfileBuilder()
        .Id($"hhl-wlan-sinput-{pad.Id}")
        .Name($"{pad.Name} (LAN)")
        .Vendor("HHL")
        .ManufacturerString("HHL")
        .ProductString(pad.Name)
        .DeviceDescription(pad.Name)
        .Vid(pad.Vid)
        .Pid(pad.Pid)
        .Type("gamepad")
        .Connection("bluetooth")
        .DescriptorHex(SInputDescriptor)
        .Build();

    public static bool IsFeatureRequest(ReadOnlySpan<byte> r) => r.Length >= 2 && r[0] == 0x03 && r[1] == 0x02;
    public static bool IsFeatureReply(ReadOnlySpan<byte> r) => r.Length >= 2 && r[0] == 0x02 && r[1] == 0x02;

    public static byte[] FeatureRequest()
    {
        var r = new byte[48];
        r[0] = 0x03; // SInput output report
        r[1] = 0x02; // Request features
        return r;
    }

    // The gamepad's XInput report (as over USB): id, size, two button bytes, triggers, then four
    // signed 16-bit stick axes with up positive
    public static HMGamepadState XInput(HMProfile profile, ReadOnlySpan<byte> r)
    {
        byte b1 = r[2], b2 = r[3];
        bool up = (b1 & 0x01) != 0, down = (b1 & 0x02) != 0, left = (b1 & 0x04) != 0, right = (b1 & 0x08) != 0;

        HMButton buttons = HMButton.None;
        if ((b1 & 0x10) != 0) buttons |= HMButton.Start;
        if ((b1 & 0x20) != 0) buttons |= HMButton.Back;
        if ((b1 & 0x40) != 0) buttons |= HMButton.LeftStick;
        if ((b1 & 0x80) != 0) buttons |= HMButton.RightStick;
        if ((b2 & 0x01) != 0) buttons |= HMButton.LeftBumper;
        if ((b2 & 0x02) != 0) buttons |= HMButton.RightBumper;
        if ((b2 & 0x04) != 0) buttons |= HMButton.Guide;
        if ((b2 & 0x10) != 0) buttons |= HMButton.A;
        if ((b2 & 0x20) != 0) buttons |= HMButton.B;
        if ((b2 & 0x40) != 0) buttons |= HMButton.X;
        if ((b2 & 0x80) != 0) buttons |= HMButton.Y;

        return new HMGamepadState
        {
            Buttons = buttons,
            Hat = Hat(up, down, left, right),
            Axes = HMGamepadStateHelpers.StandardAxes(profile,
                leftStickX: AxisX(BinaryPrimitives.ReadInt16LittleEndian(r.Slice(6))),
                leftStickY: AxisY(BinaryPrimitives.ReadInt16LittleEndian(r.Slice(8))),
                rightStickX: AxisX(BinaryPrimitives.ReadInt16LittleEndian(r.Slice(10))),
                rightStickY: AxisY(BinaryPrimitives.ReadInt16LittleEndian(r.Slice(12))),
                leftTrigger: r[4] / 255f,
                rightTrigger: r[5] / 255f),
        };
    }

    // XInput rumble arrives as command, size, low motor, high motor: the gamepad's XInput core
    // takes it as the USB report, 00 08 00 left right
    public static byte[]? XInputRumble(ReadOnlySpan<byte> data) =>
        data.Length >= 4 ? new byte[] { 0x00, 0x08, 0x00, data[2], data[3], 0x00, 0x00, 0x00 } : null;

    // The Xbox Series controller's rumble over Bluetooth (xinputhid writes it for XInputSetState):
    // report 0x03 with enable, left trigger, right trigger, left motor, right motor, each motor 0..100
    public static byte[]? XboxHidRumble(byte reportId, ReadOnlySpan<byte> data)
    {
        if (reportId != 0x03 || data.Length < 5)
            return null;
        static byte Motor(byte percent) => (byte)(Math.Min(percent, (byte)100) * 255 / 100);
        return new byte[] { 0x00, 0x08, 0x00, Motor(data[3]), Motor(data[4]), 0x00, 0x00, 0x00 };
    }

    // HID axes run 0..1 with 0.5 centred; XInput's Y is up positive, HID's grows downward
    static float AxisX(short v) => (v + 32768) / 65535f;
    static float AxisY(short v) => (32767 - v) / 65535f;

    static HMHat Hat(bool up, bool down, bool left, bool right) => (up, down, left, right) switch
    {
        (true, false, false, true) => HMHat.NorthEast,
        (false, true, false, true) => HMHat.SouthEast,
        (false, true, true, false) => HMHat.SouthWest,
        (true, false, true, false) => HMHat.NorthWest,
        (true, false, _, _) => HMHat.North,
        (false, true, _, _) => HMHat.South,
        (_, _, false, true) => HMHat.East,
        (_, _, true, false) => HMHat.West,
        _ => HMHat.None,
    };
}
