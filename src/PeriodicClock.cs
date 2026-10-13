// A steady clock on deadlines: a high-resolution timer sleeps until just before each deadline and a
// short spin finishes the wait. Windows rounds plain timer waits up, which loses ticks.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HhlGamepadLink;

sealed partial class PeriodicClock : IDisposable
{
    const uint HighResolution = 0x2;          // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
    const uint AllAccess = 0x1F0003;          // TIMER_ALL_ACCESS
    const long SpinTicks = 3_000;             // The last 0.3 ms of each wait is spun (100 ns units)
    const uint TimerResolutionMs = 1;
    const uint TimerWaitLimitMs = 1000;

    readonly nint _timer;

    public PeriodicClock()
    {
        timeBeginPeriod(TimerResolutionMs);
        _timer = CreateWaitableTimerExW(0, 0, HighResolution, AllAccess);
    }

    // The clock samples are timed on: microseconds of Stopwatch.GetTimestamp, shared by every thread
    // (split so the multiply can't overflow however long the PC has been up)
    public static long NowUs
    {
        get
        {
            long ts = Stopwatch.GetTimestamp(), f = Stopwatch.Frequency;
            return ts / f * 1_000_000 + ts % f * 1_000_000 / f;
        }
    }

    // Sleep until just before `targetUs` on the high-resolution timer, then spin the rest
    public void WaitUntilUs(long targetUs)
    {
        long sleep = (targetUs - NowUs) * 10 - SpinTicks; // 100 ns units
        if (sleep > 0 && _timer != 0)
        {
            long due = -sleep; // Negative: relative
            if (SetWaitableTimer(_timer, ref due, 0, 0, 0, false))
                WaitForSingleObject(_timer, TimerWaitLimitMs);
        }
        while (NowUs < targetUs)
            Thread.SpinWait(20);
    }

    public void Dispose()
    {
        if (_timer != 0)
            CloseHandle(_timer);
        timeEndPeriod(TimerResolutionMs);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint timer, ref long dueTime, int periodMs, nint routine,
                                                 nint arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint ms);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint ms);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint ms);
}
