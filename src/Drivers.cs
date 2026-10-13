// The two drivers HHL Gamepad Link uses, for the Drivers window: what each is for, whether it is
// installed, and installing or removing it in the background.
//
//   The virtual controller driver (HIDMaestro): gamepads in SInput and XInput mode. The app installs
//   it when it starts.
//   The USB/IP transport (usbip-win2): XInput mode's Xbox 360 controller and Slippi mode's GameCube
//   adapter. Installed the first time a gamepad opens in either (PadSession, GcAdapter), or here.
//
// Removing either first closes every gamepad open here, since their devices go with it.

using System.Diagnostics;
using HIDMaestro;
using Microsoft.Win32;

namespace HhlGamepadLink;

sealed class Drivers
{
    public enum Which { Controller = 0, UsbIp = 1 }

    // usbip-win2's installer registers its uninstaller here
    const string UsbIpUninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1";

    const string InstallingUsbIp =
        "Installing the USB/IP transport. USB devices disconnect for a moment…";

    static readonly TimeSpan UninstallWait = TimeSpan.FromMinutes(5);

    // One install or removal at a time, here or from the GameCube adapter
    static readonly object _installLock = new();

    // What is being installed or removed right now, for the footer
    public static string? Working { get; private set; }

    readonly HMContext _ctx;
    readonly Func<LinkHub?> _hub;
    readonly bool[] _installed = new bool[2];
    Which? _busy;
    Task? _work;

    public string Note { get; private set; } = "";
    public event Action? ControllerDriverInstalled;

    public Drivers(HMContext ctx, Func<LinkHub?> hub)
    {
        _ctx = ctx;
        _hub = hub;
    }

    public IReadOnlyList<NativeUi.DriverRow> Rows => new[]
    {
        new NativeUi.DriverRow("Virtual controller driver",
            "HIDMaestro. Makes gamepads in SInput mode controllers on this PC. HHL Gamepad Link " +
            "installs it each time it starts.",
            State(Which.Controller), IsInstalled(Which.Controller), _busy == Which.Controller,
            "Every gamepad open here closes, and the driver and its certificate leave this PC. Other apps " +
            "that use HIDMaestro lose their controllers and the driver too."),
        new NativeUi.DriverRow("USB/IP transport",
            "usbip-win2. Carries the Xbox 360 controller of XInput mode and the GameCube adapter of Slippi " +
            "mode. Installed the first time a gamepad opens in either. USB devices disconnect for a moment " +
            "while it installs.",
            State(Which.UsbIp), IsInstalled(Which.UsbIp), _busy == Which.UsbIp,
            "Every gamepad open here closes. Other programs that use usbip-win2 lose it too."),
    };

    bool IsInstalled(Which which) => _installed[(int)which];

    string State(Which which) => _busy == which
        ? (IsInstalled(which) ? "Removing…" : "Installing…")
        : IsInstalled(which) ? "Installed" : "Not installed";

    // Look again, in the background: the window calls it when it opens
    public void Refresh()
    {
        if (_work is { IsCompleted: false })
            return;
        _work = Task.Run(Check);
    }

    void Check()
    {
        _installed[(int)Which.Controller] = Probe(() => _ctx.IsDriverInstalled);
        _installed[(int)Which.UsbIp] = Probe(() => HMContext.IsUsbipBackendAvailable);
    }

    // A check that fails reads as not installed
    static bool Probe(Func<bool> installed)
    {
        try { return installed(); }
        catch { return false; }
    }

    public void Act(Which which, bool remove)
    {
        if (_work is { IsCompleted: false })
            return;
        _busy = which;
        Note = "";
        _work = Task.Run(() =>
        {
            try
            {
                if (remove)
                    _hub()?.CloseAll();
                switch (which, remove)
                {
                    case (Which.Controller, false):
                        Run("Installing the virtual controller driver…", _ctx.InstallDriver);
                        break;
                    case (Which.Controller, true):
                        Run("Removing the virtual controller driver…", Cleanup.RemoveControllerDriver);
                        break;
                    case (Which.UsbIp, false):
                        Run(InstallingUsbIp, () => HMContext.InstallUsbipBackend());
                        break;
                    case (Which.UsbIp, true):
                        Run("Removing the USB/IP transport…", RemoveUsbIp);
                        break;
                }
                string driver = which == Which.Controller
                    ? "The virtual controller driver"
                    : "The USB/IP transport";
                Note = $"{driver} {(remove ? "is removed" : "is installed")}.";
            }
            catch (Exception e)
            {
                Note = $"That didn't work: {e.Message}";
            }
            finally
            {
                Check();
                _busy = null;
            }
            if (which == Which.Controller && !remove && IsInstalled(Which.Controller))
                ControllerDriverInstalled?.Invoke();
        });
    }

    // The GameCube adapter's first use on this PC
    public static void EnsureUsbIp()
    {
        if (!HMContext.IsUsbipBackendAvailable)
            Run(InstallingUsbIp, () => HMContext.InstallUsbipBackend());
    }

    static void Run(string working, Action action)
    {
        lock (_installLock)
        {
            Working = working;
            try { action(); }
            finally { Working = null; }
        }
    }

    // usbip-win2's own uninstaller, silently
    static void RemoveUsbIp()
    {
        using var key = Registry.LocalMachine.OpenSubKey(UsbIpUninstallKey);
        string? command = key?.GetValue("UninstallString") as string; // "C:\...\unins000.exe" /LOG
        if (command == null)
            throw new InvalidOperationException("usbip-win2's uninstaller isn't registered on this PC");
        string exe = command.StartsWith('"') ? command[1..command.IndexOf('"', 1)] : command.Split(' ')[0];

        using var p = Process.Start(new ProcessStartInfo(exe, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("the uninstaller didn't start");
        if (!p.WaitForExit(UninstallWait))
            throw new InvalidOperationException("the uninstaller didn't finish");
    }
}
