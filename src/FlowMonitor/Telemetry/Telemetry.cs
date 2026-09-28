using FlowMonitor.Graphs;
using FlowMonitor.Interop;
using FlowMonitor.Sampling;
using static FlowMonitor.Interop.Native;

namespace FlowMonitor.Metrics;

/// <summary>
/// Process-wide performance telemetry. A single background thread samples every source at the
/// Task Manager cadence (1 Hz by default) into timestamped ring buffers; widgets read the
/// buffers and interpolate at display refresh rate.
/// </summary>
public sealed class Telemetry : IDisposable
{
    readonly object _gate = new();
    readonly List<TimeSeries> _all = new();
    readonly ManualResetEventSlim _stop = new(false);

    public CpuSampler Cpu { get; }
    public MemorySampler Memory { get; }
    public DiskSampler Disk { get; }
    public NetworkSampler Network { get; }
    public GpuSampler Gpu { get; }

    Thread? _thread;
    volatile bool _running;
    int _intervalMs = 1000;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly List<double> _tickTimes = new();

    public event Action? Sampled;

    public Telemetry()
    {
        Cpu = new(this);
        Memory = new(this);
        Disk = new(this);
        Network = new(this);
        Gpu = new(this);
    }

    public TimeSeries NewSeries()
    {
        var s = new TimeSeries();
        lock (_gate) _all.Add(s);
        return s;
    }

    public void Add(TimeSeries s, double now, float v)
    {
        lock (_gate) s.Add(now, v);
    }

    public T Read<T>(Func<T> reader)
    {
        lock (_gate) return reader();
    }

    /// <summary>Runs a multi-read block under the telemetry lock.</summary>
    public void Read(Action reader)
    {
        lock (_gate) reader();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FlowMonitor.Telemetry", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public int IntervalMs
    {
        get { lock (_gate) return _intervalMs; }
        set { lock (_gate) _intervalMs = Math.Clamp(value, 100, 5000); }
    }

    /// <summary>
    /// Median seconds between recent samples (MC progress runs on the measured tick, not
    /// the configured interval). Falls back to the configured interval until 3 ticks land.
    /// </summary>
    public double SampleIntervalSec
    {
        get
        {
            lock (_gate)
            {
                if (_tickTimes.Count >= 3)
                {
                    var deltas = new List<double>(_tickTimes.Count - 1);
                    for (int i = 1; i < _tickTimes.Count; i++)
                        deltas.Add(_tickTimes[i] - _tickTimes[i - 1]);
                    deltas.Sort();
                    double median = deltas[deltas.Count / 2];
                    if (median > 0.01 && median < 30) return Math.Max(0.05, median);
                }
                return Math.Max(0.05, _intervalMs / 1000.0);
            }
        }
    }

    /// <summary>Seconds since this instance was created. Monotonic, high resolution.</summary>
    public double Time => _clock.Elapsed.TotalSeconds;

    /// <summary>Wraps a raw Win32 HANDLE in a <see cref="WaitHandle"/> so it can be used with WaitHandle.WaitAny.</summary>
    sealed class RawWaitHandle : WaitHandle
    {
        public RawWaitHandle(IntPtr handle) => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, false);
    }

    void Loop()
    {
        var timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null,
            Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (timer == IntPtr.Zero)
        {
            // Fall back to a plain sleep loop if the high-resolution timer is unavailable.
            while (_running)
            {
                if (_stop.Wait(IntervalMs)) break;
                if (_running) SampleOnce();
            }
            return;
        }

        var handles = new[] { (WaitHandle)new RawWaitHandle(timer), _stop.WaitHandle };
        const int WAIT_OBJECT_0 = 0;

        while (_running)
        {
            try
            {
                int interval = IntervalMs;
                long due = -Math.Max(1, interval) * 10_000;   // 100 ns units, relative
                if (!Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                {
                    if (_stop.Wait(interval)) break;
                }
                else
                {
                    int r = WaitHandle.WaitAny(handles, interval + 500);
                    if (r != WAIT_OBJECT_0) break;          // stop signalled or timed out
                }

                if (!_running) break;
                SampleOnce();
            }
            catch { if (_stop.Wait(500)) break; }
        }

        Native.CancelWaitableTimer(timer);
        Native.CloseHandle(timer);
    }

    /// <summary>
    /// Takes one sample of every source on the calling thread. The background loop uses this; the
    /// self test calls it directly so the data layer can be exercised without a live thread.
    /// </summary>
    public void SampleNow() => SampleOnce();

    void SampleOnce()
    {
        double now = Time;
        lock (_gate)
        {
            _tickTimes.Add(now);
            while (_tickTimes.Count > 8) _tickTimes.RemoveAt(0);
        }
        try { Cpu.Update(now); } catch (Exception ex) { Log.Warn("cpu sample: " + ex.Message); }
        try { Memory.Update(now); } catch (Exception ex) { Log.Warn("memory sample: " + ex.Message); }
        try { Disk.Update(now); } catch (Exception ex) { Log.Warn("disk sample: " + ex.Message); }
        try { Network.Update(now); } catch (Exception ex) { Log.Warn("network sample: " + ex.Message); }
        try { Gpu.Update(now); } catch (Exception ex) { Log.Warn("gpu sample: " + ex.Message); }
        Sampled?.Invoke();
    }

    public void Dispose()
    {
        _running = false;
        _stop.Set();
        _thread?.Join(700);
        _stop.Dispose();
        Disk.Dispose();
        Network.Dispose();
        Gpu.Dispose();
    }
}
