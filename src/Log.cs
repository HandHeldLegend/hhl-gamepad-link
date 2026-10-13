// Errors worth keeping, in %LOCALAPPDATA%\HHL Gamepad Link\log.txt: what a gamepad's session or the
// app itself hit, so a report can come with it. Kept short: it starts again past MaxBytes.

namespace HhlGamepadLink;

static class Log
{
    const long MaxBytes = 256 * 1024;

    static readonly string FilePath = Path.Combine(Settings.Folder, "log.txt");
    static readonly object _lock = new();

    public static void Write(string text)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Delete(FilePath);
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}";
                File.AppendAllText(FilePath, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Anything that would end the app is written down first
    public static void CatchUnhandled() =>
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"Unhandled: {e.ExceptionObject}");
}
