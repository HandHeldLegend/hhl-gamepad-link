<p align="center"><img src="assets/icon-512.png" width="128" alt=""></p>

# HHL Gamepad Link

Use a HOJA gamepad over Bluetooth on a Windows PC, the way it works with a HOJA dongle. In Link mode the gamepad connects to this PC as a Bluetooth device of its own, this app takes the dongle's place, and turns it into a real controller on the PC, in SInput, XInput or Slippi mode. Steam and games see it like any other controller, and Dolphin sees gamepads in Slippi mode as a GameCube adapter.

- **Switch modes without re-pairing.** Pick SInput, XInput or Slippi from the gamepad's card or the tray icon. The gamepad changes mode over the same connection.
- **One portable .exe.** There's no installer and no registry settings. The one setting it keeps, Close to tray, is in `%LOCALAPPDATA%\HHL Gamepad Link`.
- **Report rate:** 125, 250 or 500 Hz. The app tells the gamepad, and plays input out evenly.
- **Config app over Bluetooth.** While the app is running, [HHL Gamepad Config](https://handheldlegend.github.io/hoja3/) can connect to your gamepads through it, with no cable.
- Optional **Close to tray** (the window's close button hides it in the notification area; Quit from the tray icon exits) and **Start with Windows**.

This app was called HHL Gamepad WLAN up to version 0.1, when gamepads reached it over the home network. That path is gone; Bluetooth replaces it. The app takes over the old version's Start with Windows and Close to tray settings.

## Get it

Download `HHL Gamepad Link.exe` from the [latest release](https://github.com/HandHeldLegend/hhl-gamepad-link/releases/latest) and run it. Each release lists its changes. The app's footer shows whether a newer release is out.

The .exe isn't code-signed, so Windows SmartScreen may warn you the first time. Choose **More info**, then **Run anyway**.

The app asks for administrator rights. It needs them to install the virtual controller driver the first time it runs.

## Connecting a gamepad

The gamepad needs firmware with Link mode.

1. Start the gamepad in dongle mode with pairing (the wireless pairing combo). For the first 30 seconds it can be found as **"<name> Link"**.
2. In Windows, open **Settings → Bluetooth & devices → Add device → Bluetooth** and pick "<name> Link".
3. From then on, starting the gamepad in dongle mode connects it to this PC whenever the app is running. Without the app, it looks for a HOJA dongle instead.

Link mode is a Bluetooth HID device with a vendor-defined collection: games and Windows don't see it as a controller, and the app reads it without a driver. Its own address keeps it apart from the gamepad's other Bluetooth modes, so pairing it doesn't disturb them.

A gamepad you disconnect from the app goes back to looking for a dongle, and stays away from the app until you click **Open** on its card. A gamepad that drops out reconnects on its own when it's back.

## Slippi and Dolphin

Gamepads in Slippi mode share one virtual GameCube Controller Adapter, the Wii U adapter Dolphin and Slippi read. Each gamepad gets a port of its own, in the order they connect. A gamepad that drops out and comes back gets its old port back. Up to four fit. Rumble from Dolphin goes to the gamepad on that port.

In Dolphin, set the ports to **GameCube Adapter for Wii U**. No Zadig or driver setup is needed.

## XInput mode

A gamepad in XInput mode is a wired Xbox 360 controller to Windows: the same USB device, driven by Windows' own Xbox 360 driver, so Steam and games see exactly one controller.

XInput and Slippi modes both run on [usbip-win2](https://github.com/vadimgrn/usbip-win2), the signed USB/IP transport these USB devices need. The first time a gamepad opens in either mode on a PC, the app installs it, and your USB devices disconnect for a moment while it does.

## Removing it

Untick **Start with Windows** and click **Drivers** in the app's footer. The Drivers window lists the two drivers the app uses: the virtual controller driver (HIDMaestro) and the USB/IP transport (usbip-win2), if a gamepad in XInput or Slippi mode was ever opened. Remove each one, close the app, and delete the .exe and the `%LOCALAPPDATA%\HHL Gamepad Link` folder.

## Building

You need Windows 10 or 11 (x64) and Visual Studio 2022 with **Desktop development with C++** and the Windows SDK 10.0.26100. Then:

```bat
git clone --recurse-submodules https://github.com/HandHeldLegend/hhl-gamepad-link
cd hhl-gamepad-link
build.cmd
```

`build.cmd` downloads the .NET 10 SDK and the Windows Driver Kit's NuGet package into `.tools\` and checks each against a pinned hash. Nothing is installed system-wide. To keep the tools somewhere else, set `HHL_TOOLS`. The result is `build\release\HHL Gamepad Link.exe`.

The layout:

| Path | What |
| --- | --- |
| `src/` | The app (C#, compiled ahead of time into one native .exe) |
| `src/Protocol.cs` | What the gamepad and the app say: the HOJA dongle link, in Link mode's HID reports |
| `src/HidLink.cs` | Finds and opens a gamepad's Link collection with `hid.dll` |
| `native/` | The window: `hhl_ui.cpp` with [Dear ImGui](https://github.com/ocornut/imgui) |
| `external/HIDMaestro` | [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro), the virtual controller driver, pinned |
| `patches/` | Our build changes to HIDMaestro |
| `tools/make_icon.py` | Draws `src/app.ico` and `assets/icon-512.png` |

See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the software this includes.
