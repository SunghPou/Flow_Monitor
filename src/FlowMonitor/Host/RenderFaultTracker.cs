namespace FlowMonitor.Host;

/// <summary>
/// Counts consecutive render faults per widget; the render loop retires poisoned widgets.
/// Retire fires exactly once per widget, when the count first reaches the threshold.
/// </summary>
/// <typeparam name="TKey">Identity of a widget. Compared by the caller's own equality.</typeparam>
internal sealed class RenderFaultTracker<TKey> where TKey : notnull
{
    readonly Dictionary<TKey, int> _counts = new();
    readonly int _retireAfter;

    public RenderFaultTracker(int retireAfter)
    {
        if (retireAfter < 1) throw new ArgumentOutOfRangeException(nameof(retireAfter));
        _retireAfter = retireAfter;
    }

    /// <summary>Whether a fault at this count should be logged (throttled past 3 widgets).</summary>
    public bool ShouldLog(int faultNo) => _counts.Count <= 3 || faultNo % 60 == 1;

    /// <summary>Record a fault. Returns the new consecutive count for this widget.</summary>
    public int NoteFault(TKey key)
    {
        int n = _counts.TryGetValue(key, out int prev) ? prev + 1 : 1;
        _counts[key] = n;
        return n;
    }

    /// <summary>A frame succeeded, so the widget is healthy again.</summary>
    public void NoteSuccess(TKey key) => _counts.Remove(key);

    /// <summary>
    /// True on exactly one call per widget: the frame where the count first reaches the
    /// threshold. Deliberately not <c>&gt;=</c> (retirement queues to the UI thread async).
    /// </summary>
    public bool ShouldRetire(TKey key, int faultNo) => faultNo == _retireAfter;

    /// <summary>Stop tracking a widget entirely (after teardown, or on shutdown).</summary>
    public void Forget(TKey key) => _counts.Remove(key);
}
