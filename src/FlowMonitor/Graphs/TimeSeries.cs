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

    public int Count => _count;
    public double NewestTime => _count == 0 ? 0 : TimeAt(_count - 1);

    public TimeSeries(int capacity = DefaultCapacity)
    {
        _times = new double[capacity];
        _values = new float[capacity];
    }

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
    public float Peak(double windowStart) => Scan(windowStart, float.MinValue, Math.Max);

    public float Min(double windowStart) => Scan(windowStart, float.MaxValue, Math.Min);

    float Scan(double windowStart, float seed, Func<float, float, float> fold)
    {
        float acc = seed;
        for (int i = 0; i < _count; i++)
            if (TimeAt(i) >= windowStart)
                acc = fold(acc, ValueAt(i));
        return acc is float.MinValue or float.MaxValue ? 0f : acc;
    }
}
