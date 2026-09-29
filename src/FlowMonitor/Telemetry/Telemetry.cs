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
    double _prevTick = double.NaN, _lastDelta;

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
    /// Last measured seconds between samples (MC progress runs on the measured tick, not
    /// the configured interval). Falls back to the configured interval until 2 ticks land.
    /// </summary>
    public double SampleIntervalSec
    {
        get
        {
            lock (_gate)
            {
                if (_lastDelta > 0.01 && _lastDelta < 30) return Math.Max(0.05, _lastDelta);
                return Math.Max(0.05, _intervalMs / 1000.0);
            }
        }
    }

    /// <summary>Seconds since this instance was created. Monotonic, high resolution.</summary>
    public double Time => _clock.Elapsed.TotalSeconds;

    // A plain sleep loop: the 1 Hz cadence never needed the high-resolution waitable
    // timer, and the timer branch was the only user of the handle wrapper here.
    void Loop()
    {
        while (_running)
        {
            if (_stop.Wait(IntervalMs)) break;
            if (_running) SampleOnce();
        }
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
            if (!double.IsNaN(_prevTick)) _lastDelta = now - _prevTick;
            _prevTick = now;
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
