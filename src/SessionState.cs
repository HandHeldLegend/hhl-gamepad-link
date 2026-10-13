// Where a gamepad's session is: waiting for it to say who it is, making its device, open, or closed

namespace HhlGamepadLink;

enum SessionState { Opening, Starting, Open, Closed }
