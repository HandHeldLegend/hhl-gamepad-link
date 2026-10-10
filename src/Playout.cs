// Plays a gamepad's samples out at the spacing it took them, not as the WLAN delivers them.
//
// Each packet carries the gamepad's clock when it was sampled. Arrival minus that is the clock
// offset plus the trip; the smallest value of the last two seconds is the offset plus the quickest
// trip, and following it also follows the two clocks drifting apart. A sample is played at its
// gamepad time + offset + a small delay. Packets slower than the delay are late and dropped; the
// delay grows while too many are and shrinks while almost none are.

namespace HhlGamepadWlan;

sealed class Playout<T>
{
    const int Buckets = 20;            // 20 x 100 ms: the offset follows the last 2 s
    const long BucketUs = 100_000;
    const long MinDelayUs = 1_000, MaxDelayUs = 20_000;
    const double TooLate = 0.02, FewLate = 0.005; // Late share that grows / shrinks the delay

    readonly object _lock = new();
    readonly Queue<(long PlayAtUs, T Sample)> _queue = new();
    readonly long[] _bucketMin = new long[Buckets];
    int _bucket = -1;
    long _bucketStartUs;
    long _offsetUs;
    long _lastSentUs = long.MinValue;

    int _onTime, _late;
    long _windowStartUs;

    public long DelayUs { get; private set; } = 4_000;
    public double LateShare { get; private set; }

    public void Add(long arrivalUs, long sentUs, T sample)
    {
        lock (_lock)
        {
            if (sentUs <= _lastSentUs)
                return; // Out of order or repeated: a newer sample is already queued
            _lastSentUs = sentUs;

            TrackOffset(arrivalUs, arrivalUs - sentUs);
            Adapt(arrivalUs);

            long playAt = sentUs + _offsetUs + DelayUs;
            if (playAt < arrivalUs)
            {
                _late++; // Its slot has passed: the host keeps the previous sample
                return;
            }
            _onTime++;
            _queue.Enqueue((playAt, sample));
        }
    }

    // When the next sample is due, or long.MaxValue with none queued
    public long NextDueUs()
    {
        lock (_lock)
            return _queue.Count > 0 ? _queue.Peek().PlayAtUs : long.MaxValue;
    }

    public bool TryTakeDue(long nowUs, out T sample)
    {
        lock (_lock)
        {
            if (_queue.Count > 0 && _queue.Peek().PlayAtUs <= nowUs)
            {
                sample = _queue.Dequeue().Sample;
                return true;
            }
        }
        sample = default!;
        return false;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _bucket = -1;
            _lastSentUs = long.MinValue;
        }
    }

    void TrackOffset(long nowUs, long sample)
    {
        if (_bucket < 0)
        {
            Array.Fill(_bucketMin, sample);
            _bucket = 0;
            _bucketStartUs = nowUs;
        }
        else if (nowUs - _bucketStartUs >= BucketUs)
        {
            _bucket = (_bucket + 1) % Buckets;
            _bucketMin[_bucket] = sample;
            _bucketStartUs = nowUs;
        }
        else if (sample < _bucketMin[_bucket])
            _bucketMin[_bucket] = sample;

        _offsetUs = _bucketMin.Min();
    }

    void Adapt(long nowUs)
    {
        if (_windowStartUs == 0)
            _windowStartUs = nowUs;
        if (nowUs - _windowStartUs < 1_000_000)
            return;

        int total = _onTime + _late;
        LateShare = total > 0 ? (double)_late / total : 0;
        if (LateShare > TooLate)
            DelayUs = Math.Min(MaxDelayUs, DelayUs + 1_000);
        else if (LateShare < FewLate)
            DelayUs = Math.Max(MinDelayUs, DelayUs - 500);

        _onTime = _late = 0;
        _windowStartUs = nowUs;
    }
}
