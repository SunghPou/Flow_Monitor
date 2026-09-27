using FlowMonitor.Graphs;
using FlowMonitor.Interop;

namespace FlowMonitor.Sampling;

/// <summary>
/// Aggregate disk throughput across all physical disks, using the same
/// <c>PhysicalDisk(_Total)\Disk Bytes/sec</c> counters Task Manager reports,
/// split into read and write as Task Manager does.
/// </summary>
public sealed class DiskSampler
{
    readonly Metrics.Telemetry _tel;
    readonly Pdh.WildcardCounter? _read;
    readonly Pdh.WildcardCounter? _write;
    readonly Pdh.WildcardCounter? _busy;

    public TimeSeries ReadBytes { get; }
    public TimeSeries WriteBytes { get; }
    public TimeSeries ActivePercent { get; }
    /// <summary>True when the PDH counters are unavailable and the graph will be empty.</summary>
    public bool Unavailable { get; }
    public string? UnavailableReason { get; }

    public DiskSampler(Metrics.Telemetry tel)
    {
        _tel = tel;
        ReadBytes = tel.NewSeries();
        WriteBytes = tel.NewSeries();
        ActivePercent = tel.NewSeries();

        try
        {
            // NULL data source means the local machine; query handle suffices.
            uint rc = Pdh.OpenQuery(out var query);
            if (rc != Pdh.ERROR_SUCCESS || query == IntPtr.Zero)
            {
                Unavailable = true;
                UnavailableReason = $"PdhOpenQuery 0x{rc:X8}";
                return;
            }
            _query = query;

            // Local paths take a single leading backslash. Wildcard instances are summed;
            // "(_Total)" returns INVALID_DATA here, so it is not used.
            _read = new Pdh.WildcardCounter(query, @"\PhysicalDisk(*)\Disk Read Bytes/sec");
            _write = new Pdh.WildcardCounter(query, @"\PhysicalDisk(*)\Disk Write Bytes/sec");
            _busy = new Pdh.WildcardCounter(query, @"\PhysicalDisk(*)\% Disk Time");
            if (_read.Error is not null || _write.Error is not null)
            {
                Unavailable = true;
                UnavailableReason = _read.Error ?? _write.Error;
            }
        }
        catch (Exception ex)
        {
            Unavailable = true;
            UnavailableReason = ex.Message;
        }
    }

    IntPtr _query;
    bool _reportedNoData;

    public float LastRead { get; private set; }
    public float LastWrite { get; private set; }
    public float LastBusy { get; private set; }

    /// <summary>
    /// Set when byte-rate counters are inert on this machine; the graph shows activity only.
    /// Evidence-based: busy disk with exactly-zero byte rates over repeated samples.
    /// </summary>
    public bool ByteRatesUnavailable { get; private set; }
    public string? ByteRatesUnavailableReason { get; private set; }

    int _busyButZeroByteRates;
    const int BusyButZeroSamplesBeforeConcluding = 5;

    public void Update(double now)
    {
        if (Unavailable) return;

        // First read after Collect has no rate yet (PDH_NO_DATA); treat as "not yet".
        _read!.Collect();
        _write!.Collect();
        _busy?.Collect();

        double r = Sum(_read, out int readInstances);
        double w = Sum(_write, out int writeInstances);
        double b = _busy is null ? double.NaN : Sum(_busy, out _);

        if (readInstances == 0 && writeInstances == 0)
        {
            // Not primed yet; log once to distinguish unprimed from dead.
            if (!_reportedNoData)
            {
                _reportedNoData = true;
                Log.Info($"disk: no instances yet (read err=0x{_read!.LastError:X8} " +
                         $"items={_read.LastItemCount})");
            }
            return;
        }

        LastRead = (float)r;
        LastWrite = (float)w;
        LastBusy = double.IsNaN(b) ? LastBusy : (float)Math.Clamp(b, 0, 100);

        // Inert byte-rate detection: demonstrably busy yet exactly zero over repeated samples.
        if (!ByteRatesUnavailable && LastBusy > 0.5f && LastRead == 0f && LastWrite == 0f)
        {
            if (++_busyButZeroByteRates >= BusyButZeroSamplesBeforeConcluding)
            {
                ByteRatesUnavailable = true;
                ByteRatesUnavailableReason =
                    "this machine's storage stack reports % Disk Time but no byte rates " +
                    "(PDH byte-rate counters stay at 0 under load; IOCTL_DISK_PERFORMANCE is " +
                    "unsupported on every physical drive)";
                Log.Warn($"disk: byte-rate counters are inert; falling back to activity only " +
                         $"(busy={LastBusy:F1}%, read={LastRead}, write={LastWrite})");
            }
        }
        else if (LastRead != 0f || LastWrite != 0f)
        {
            // Any non-zero reading clears the flag; configuration can change at runtime.
            ByteRatesUnavailable = false;
            ByteRatesUnavailableReason = null;
            _busyButZeroByteRates = 0;
        }

        _tel.Add(ReadBytes, now, ByteRatesUnavailable ? 0f : LastRead);
        _tel.Add(WriteBytes, now, ByteRatesUnavailable ? 0f : LastWrite);
        _tel.Add(ActivePercent, now, LastBusy);
    }

    /// <summary>
    /// Sums a wildcard counter across instances. "% Disk Time" sums per-disk percentages (clamped by caller).
    /// </summary>
    static double Sum(Pdh.WildcardCounter counter, out int instances)
    {
        double total = 0;
        int n = 0;
        bool ok = counter.ReadAll((_, v) => { total += v; n++; });
        instances = ok ? n : 0;
        return ok ? total : double.NaN;
    }

    public void Dispose()
    {
        _read?.Dispose();
        _write?.Dispose();
        _busy?.Dispose();
        if (_query != IntPtr.Zero) { Pdh.PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}
