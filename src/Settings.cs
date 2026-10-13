// The settings kept between runs, in %LOCALAPPDATA%\HHL Gamepad Link\settings.ini (key=value lines).
// Only what should outlast a run lives here: Close to tray. The report rate and the modes picked
// for gamepads stay for one run.

namespace HhlGamepadLink;

static class Settings
{
    // The app's own folder: these settings, the known gamepads and the log
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HHL Gamepad Link");

    static readonly string FilePath = Path.Combine(Folder, "settings.ini");

    // Where the app kept them up to 0.1, as HHL Gamepad WLAN: read when there are none here yet
    static readonly string OldFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HHL Gamepad WLAN",
        "settings.ini");

    public static bool CloseToTray { get; private set; }

    public static void Load()
    {
        string path = File.Exists(FilePath) || !File.Exists(OldFilePath) ? FilePath : OldFilePath;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0] == "closeToTray")
                    CloseToTray = parts[1] == "1";
            }
        }
        catch (IOException) { }                  // None yet: the defaults
        catch (UnauthorizedAccessException) { }
    }

    public static void SetCloseToTray(bool on)
    {
        if (on == CloseToTray)
            return;
        CloseToTray = on;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, $"closeToTray={(on ? 1 : 0)}\n");
        }
        catch (IOException) { }                  // Still applies for this run
        catch (UnauthorizedAccessException) { }
    }
}
