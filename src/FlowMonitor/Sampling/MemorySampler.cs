using System.Runtime.InteropServices;
using FlowMonitor.Graphs;

using FlowMonitor.Metrics;

namespace FlowMonitor.Sampling;

[StructLayout(LayoutKind.Sequential)]
public struct PERFORMANCE_INFORMATION
{
    public uint cb;
    public ulong CommitTotal;
    public ulong CommitLimit;
    public ulong CommitPeak;
    public ulong PhysicalTotal;
    public ulong PhysicalAvailable;
    public ulong PhysicalPageList;
    public ulong SystemCache;
    public ulong KernelTotal;
    public ulong KernelPaged;
    public ulong KernelNonpaged;
    public uint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

/// <summary>
/// Physical memory breakdown in the same shape Task Manager shows it, sourced from
/// GetPerformanceInfo (the exact API Task Manager uses): In use, Cached, Available stacked as
/// percentages of physical total, plus a separate Committed trace.
/// </summary>
public sealed class MemorySampler
{
    readonly Telemetry _telemetry;

    public TimeSeries InUse { get; }
    public TimeSeries Cached { get; }
    public TimeSeries Available { get; }
    public TimeSeries Committed { get; }
    public TimeSeries CommitLimit { get; }

    public long TotalPhysical { get; private set; }
    public long TotalCommitLimit { get; private set; }
    public long LastInUse { get; private set; }
    public long LastCached { get; private set; }
    public long LastCommitted { get; private set; }

    public MemorySampler(Telemetry telemetry)
    {
        _telemetry = telemetry;
        InUse = telemetry.NewSeries();
        Cached = telemetry.NewSeries();
        Available = telemetry.NewSeries();
        Committed = telemetry.NewSeries();
        CommitLimit = telemetry.NewSeries();
    }

    public void Update(double now)
    {
        if (!PsApi.GetPerformanceInfo(out var pi)) return;

        // Page-denominated fields scaled by page size; commit fields are already bytes.
        long pageSize = pi.PageSize == 0 ? 4096 : pi.PageSize;
        long total = (long)pi.PhysicalTotal * pageSize;
        long avail = (long)pi.PhysicalAvailable * pageSize;
        long cached = (long)pi.SystemCache * pageSize;

        long inUse = Math.Max(0, total - avail);

        TotalPhysical = total;
        TotalCommitLimit = (long)pi.CommitLimit;
        LastInUse = inUse;
        LastCached = cached;
        LastCommitted = (long)pi.CommitTotal;

        _telemetry.Add(InUse, now, inUse);
        _telemetry.Add(Cached, now, cached);
        _telemetry.Add(Available, now, avail);
        _telemetry.Add(Committed, now, (float)pi.CommitTotal);
        _telemetry.Add(CommitLimit, now, (float)pi.CommitLimit);
    }
}

static class PsApi
{
    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION cb, uint cbSize);

    public static bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info)
    {
        var tmp = default(PERFORMANCE_INFORMATION);
        tmp.cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>();
        return GetPerformanceInfo(out info, tmp.cb);
    }
}
