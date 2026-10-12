// Whether a newer HHL Gamepad Link is out: the latest GitHub release against this build's version.
// Checked at start and every few hours; it only feeds a small badge, it never downloads anything.

using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace HhlGamepadLink;

static class Updates
{
    const string Repo = "HandHeldLegend/hhl-gamepad-link";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases/latest";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    public enum State { Unknown, UpToDate, Available }

    public static State Status { get; private set; } = State.Unknown;
    public static string Latest { get; private set; } = "";

    // This build's version, from the project's <Version>
    public static string Current { get; } =
        typeof(Updates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    public static void Start(CancellationToken stop) => _ = Task.Run(async () =>
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"HhlGamepadLink/{Current}"); // GitHub requires one
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync(
                    $"https://api.github.com/repos/{Repo}/releases/latest", stop));
                Latest = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
                Status = IsNewer(Latest, Current) ? State.Available : State.UpToDate;
            }
            catch (Exception e) when (e is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                // Offline or no release yet: show nothing rather than a wrong answer
            }

            try { await Task.Delay(CheckEvery, stop); }
            catch (OperationCanceledException) { return; }
        }
    });

    public static void OpenReleasesPage() =>
        Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });

    // This build's version as one number for the gamepad (0.2.0 is 0x000200)
    public static uint CurrentNumber
    {
        get
        {
            var p = Current.Split('.').Select(x => uint.TryParse(x, out uint n) ? n : 0).Concat(new uint[3]).Take(3).ToArray();
            return (p[0] << 16) | (p[1] << 8) | p[2];
        }
    }

    // Versions are numbers separated by dots ("0.2.1"); a missing part counts as 0
    static bool IsNewer(string latest, string current)
    {
        int[] Parts(string v) => v.Split('.', '-')
            .Select(p => int.TryParse(p, out int n) ? n : 0).Take(3).ToArray();
        int[] a = Parts(latest), b = Parts(current);
        for (int i = 0; i < 3; i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y)
                return x > y;
        }
        return false;
    }
}
