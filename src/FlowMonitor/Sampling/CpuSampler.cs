using System.Runtime.InteropServices;
using FlowMonitor.Graphs;
using FlowMonitor.Interop;

using FlowMonitor.Metrics;

namespace FlowMonitor.Sampling;

/// <summary>
/// CPU utilisation exactly the way Task Manager computes it: raw per-core kernel/user/idle
/// cycles from NtQuerySystemInformation, differenced over the sampling interval.
/// Kernel time includes idle time, so busy = (dKernel + dUser) - dIdle.
/// </summary>
public sealed class CpuSampler
{
    readonly Telemetry _telemetry;
    readonly SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] _buffer;
    readonly SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] _previous;
    IntPtr _native;
    bool _primed;

    /// <summary>Bytes per native entry, from the managed layout.</summary>
    static readonly int EntrySize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();

    public TimeSeries Total { get; }
    public TimeSeries[] Cores { get; }
    public int CoreCount => _buffer.Length;

    public CpuSampler(Telemetry telemetry)
    {
        _telemetry = telemetry;
        int n = NtDll.ProcessorCount;
        _buffer = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[n];
        _previous = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[n];
        Total = telemetry.NewSeries();
        Cores = new TimeSeries[n];
        for (int i = 0; i < n; i++) Cores[i] = telemetry.NewSeries();
        _native = Marshal.AllocHGlobal(EntrySize * n);
    }

    public void Update(double now)
    {
        int bytes = EntrySize * _buffer.Length;
        int status = NtDll.NtQuerySystemInformation(
            NtDll.SystemProcessorPerformanceInformation, _native, bytes, out int returned);
        if (status != 0) return;

        // Short return: kernel describes more processors than the buffer holds.
        // STATUS_INFO_LENGTH_MISMATCH is a clean refusal, not corruption.
        if (returned > 0 && returned < bytes)
        {
            Log.Warn($"cpu: kernel reported {returned} bytes for {bytes} offered; processor count " +
                     "changed under us, keeping the first " + (_buffer.Length - 1) + " cores");
        }

        // Copy the native block by hand; PtrToStructure is invalid for blittable value-type arrays.
        unsafe
        {
            var dst = new Span<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(_buffer);
            fixed (SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION* p = dst)
                Buffer.MemoryCopy((void*)_native, p, bytes, bytes);
        }

        if (!_primed)
        {
            Array.Copy(_buffer, _previous, _buffer.Length);
            _primed = true;
            return;
        }

        double totalBusy = 0, totalAll = 0;
        for (int i = 0; i < _buffer.Length; i++)
        {
            var c = _buffer[i];
            var p = _previous[i];

            double dIdle = c.IdleTime - p.IdleTime;
            double dKernel = c.KernelTime - p.KernelTime;
            double dUser = c.UserTime - p.UserTime;
            double dAll = dKernel + dUser;
            double dBusy = dAll - dIdle;
            if (dBusy < 0) dBusy = 0;
            if (dAll <= 0) continue;

            float pct = (float)Math.Clamp(dBusy / dAll * 100.0, 0, 100);
            _telemetry.Add(Cores[i], now, pct);

            totalBusy += dBusy;
            totalAll += dAll;
        }

        Array.Copy(_buffer, _previous, _buffer.Length);

        if (totalAll > 0)
        {
            float pct = (float)Math.Clamp(totalBusy / totalAll * 100.0, 0, 100);
            _telemetry.Add(Total, now, pct);
        }
    }

    public float LatestUtilization => _telemetry.Read(() => Total.Latest);

    public IReadOnlyList<float> LatestCoreUtilization
    {
        get
        {
            var list = new float[Cores.Length];
            _telemetry.Read(() =>
            {
                for (int i = 0; i < Cores.Length; i++) list[i] = Cores[i].Latest;
            });
            return list;
        }
    }
}
