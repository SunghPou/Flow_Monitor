using System.Runtime.InteropServices;
using FlowMonitor.Graphs;

using FlowMonitor.Metrics;

namespace FlowMonitor.Sampling;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PERFORMANCE_INFORMATION
{
    public uint cb;
    public ulong CommitTotal;
    public ulong CommitLimit;
    public ulong CommitPeak;
    public ulong PhysicalTotal;
    public ulong PhysicalAvailable;
    public ulong SystemCache;
    public ulong KernelTotal;
    public ulong KernelPaged;
    public ulong KernelNonpaged;
    public ulong PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}

/// <summary>
/// Physical memory breakdown in the same shape Task Manager shows it, sourced from
/// GetPerformanceInfo (the exact API Task Manager uses): In use, Cached and
/// cache-excluded Available stack to the physical total, plus Committed in bytes.
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

        // Every count field arrives in pages; all are scaled by the page size.
        // Available includes the standby cache, so the plotted Available is free
        // memory excluding cache: InUse + Cached + Available then stacks to total.
        long pageSize = pi.PageSize == 0 ? 4096 : (long)pi.PageSize;
        long total = (long)pi.PhysicalTotal * pageSize;
        long avail = (long)pi.PhysicalAvailable * pageSize;
        long cached = (long)pi.SystemCache * pageSize;
        long committed = (long)pi.CommitTotal * pageSize;
        long commitLimit = (long)pi.CommitLimit * pageSize;

        long inUse = Math.Max(0, total - avail);
        long free = Math.Max(0, avail - cached);

        TotalPhysical = total;
        TotalCommitLimit = commitLimit;
        LastInUse = inUse;
        LastCached = cached;
        LastCommitted = committed;

        _telemetry.Add(InUse, now, inUse);
        _telemetry.Add(Cached, now, cached);
        _telemetry.Add(Available, now, free);
        _telemetry.Add(Committed, now, committed);
        _telemetry.Add(CommitLimit, now, commitLimit);
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
