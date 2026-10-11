// The devices a gamepad can appear as on this PC, picked by its mode

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
}
