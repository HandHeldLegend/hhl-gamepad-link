// The settings kept between runs, in %LOCALAPPDATA%\HHL Gamepad WLAN\settings.ini (key=value lines).
// Only what should outlast a run lives here: Close to tray. The report rate and the modes picked
// for gamepads stay for one run.

namespace HhlGamepadWlan;

static class Settings
{
    static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HHL Gamepad WLAN");
    static readonly string FilePath = Path.Combine(Folder, "settings.ini");

    public static bool CloseToTray { get; private set; }

    public static void Load()
    {
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
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
