// Errors worth keeping, in %LOCALAPPDATA%\HHL Gamepad Link\log.txt: what a gamepad's session or the
// app itself hit, so a report can come with it. Kept short: it starts again past 256 KB.

namespace HhlGamepadLink;

static class Log
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HHL Gamepad Link", "log.txt");
    static readonly object Lock = new();

    public static void Write(string text)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 256 * 1024)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {text}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Anything that would end the app is written down first
    public static void CatchUnhandled() =>
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"Unhandled: {e.ExceptionObject}");
}
