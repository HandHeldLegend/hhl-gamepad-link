// A steady clock on deadlines: a high-resolution timer sleeps until just before each tick and a
// short spin finishes the wait. Windows rounds plain timer waits up, which loses ticks.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HhlGamepadWlan;

sealed partial class PeriodicClock : IDisposable
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr timer, ref long dueTime, int periodMs, IntPtr routine,
                                                 IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr handle, uint ms);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint ms);
    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint ms);

    const uint HighResolution = 0x2;          // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
    const uint AllAccess = 0x1F0003;          // TIMER_ALL_ACCESS
    const long SpinTicks = 10000 * 3 / 10;    // The last 0.3 ms of each wait is spun (100 ns units)

    readonly IntPtr _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _period;
    long _deadline;

    public PeriodicClock(int periodMs)
    {
        timeBeginPeriod(1);
        _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, HighResolution, AllAccess);
        Period = periodMs;
    }

    long Now => _clock.Elapsed.Ticks;

    public int Period
    {
        get => (int)(_period / 10000);
        set
        {
            _period = value * 10000L;
            _deadline = Now + _period;
        }
    }

    public void WaitTick()
    {
        long sleep = _deadline - Now - SpinTicks;
        if (sleep > 0 && _timer != IntPtr.Zero)
        {
            long due = -sleep; // Negative: relative, in 100 ns units
            if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(_timer, 1000);
        }
        while (Now < _deadline)
            Thread.SpinWait(20);

        _deadline += _period;
        if (Now - _deadline > 10 * _period)
            _deadline = Now + _period; // Held up for a long while: carry on from now, no burst
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
        if (sleep > 0 && _timer != IntPtr.Zero)
        {
            long due = -sleep;
            if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(_timer, 1000);
        }
        while (NowUs < targetUs)
            Thread.SpinWait(20);
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero)
            CloseHandle(_timer);
        timeEndPeriod(1);
    }
}
