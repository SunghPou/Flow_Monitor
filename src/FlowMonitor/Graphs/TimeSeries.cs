namespace FlowMonitor.Graphs;

/// <summary>
/// Fixed-capacity timestamped ring buffer. The renderer consumes immutable newest-first
/// snapshots (<see cref="SnapshotLatest"/>); x position is the slot index, never the timestamp.
/// </summary>
public sealed class TimeSeries
{
    /// <summary>
    /// Sized so the whole visible window fits with room to spare. 60s window at 100ms
    /// interval needs 600 samples; 1024 leaves headroom (~12KB per series). Ring overwrite
    /// retains the newest <c>Capacity</c> samples contiguously for binary search.
    /// </summary>
    public const int DefaultCapacity = 1024;

    readonly double[] _times;
    readonly float[] _values;
    int _head;      // index of the next write slot
    int _count;

    public int Capacity => _times.Length;
    public int Count => _count;
    public double NewestTime => _count == 0 ? 0 : TimeAt(_count - 1);

    public TimeSeries(int capacity = DefaultCapacity)
    {
        _times = new double[capacity];
        _values = new float[capacity];
    }

    public void Clear() { _head = 0; _count = 0; }

    public void Add(double time, float value)
    {
        if (_count > 0)
        {
            double last = TimeAt(_count - 1);
            if (time <= last) time = last + 1e-6;   // enforce monotonic ordering
        }

        _times[_head] = time;
        _values[_head] = value;
        _head = (_head + 1) % _times.Length;
        if (_count < _times.Length) _count++;
    }

    /// <summary>Logical index 0 = oldest retained sample.</summary>
    public double TimeAt(int index)
    {
        int i = (_head - _count + index + _times.Length * 2) % _times.Length;
        return _times[i];
    }

    public float ValueAt(int index)
    {
        int i = (_head - _count + index + _times.Length * 2) % _times.Length;
        return _values[i];
    }

    /// <summary>Newest sample value.</summary>
    public float Latest => _count == 0 ? 0f : ValueAt(_count - 1);

    /// <summary>
    /// Immutable newest-first copy of up to <paramref name="n"/> samples. Unfilled slots are
    /// NaN, which the renderer draws as a gap rather than a fabricated line. Call under the
    /// telemetry lock; the renderer never touches the live ring.
    /// </summary>
    public float[] SnapshotLatest(int n)
    {
        var snap = new float[Math.Max(0, n)];
        for (int i = 0; i < snap.Length; i++) snap[i] = float.NaN;
        int take = Math.Min(_count, snap.Length);
        for (int i = 0; i < take; i++) snap[i] = ValueAt(_count - 1 - i);
        return snap;
    }

    /// <summary>Highest value inside the visible window (peak hold).</summary>
    public float Peak(double windowStart)
    {
        float max = float.MinValue;
        for (int i = 0; i < _count; i++)
            if (TimeAt(i) >= windowStart)
                max = Math.Max(max, ValueAt(i));
        return max == float.MinValue ? 0f : max;
    }

    public float Min(double windowStart)
    {
        float min = float.MaxValue;
        for (int i = 0; i < _count; i++)
            if (TimeAt(i) >= windowStart)
                min = Math.Min(min, ValueAt(i));
        return min == float.MaxValue ? 0f : min;
    }

    /// <summary>Value at an arbitrary instant. Linear between samples, held flat outside range.</summary>
    public float ValueAtTime(double t)
    {
        if (_count == 0) return 0f;
        if (_count == 1 || t <= TimeAt(0)) return ValueAt(0);
        if (t >= TimeAt(_count - 1)) return ValueAt(_count - 1);

        int lo = 0, hi = _count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (TimeAt(mid) <= t) lo = mid; else hi = mid;
        }

        double t0 = TimeAt(lo), t1 = TimeAt(hi);
        double f = t1 <= t0 ? 0 : (t - t0) / (t1 - t0);
        return (float)(ValueAt(lo) + (ValueAt(hi) - ValueAt(lo)) * f);
    }
}
