// A gamepad as the list shows it: open here, or seen nearby
//
// Away: it was here and has gone quiet. Refused: disconnected here. Nearby: not here, but
// advertising (connected elsewhere). Known: it gave this app its call token and Windows holds its
// pairing, so it can be called over; otherwise it can be paired. Call says how the last call or
// pairing went.

namespace HhlGamepadLink;

sealed record PadEntry(PadIdentity Pad, PadSession? Session, bool Away, bool Refused, Caller.Sighting? Nearby,
                       string? Call, bool Known, string UserName);
