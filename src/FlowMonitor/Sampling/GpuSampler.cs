using System.Collections.Concurrent;
using System.Globalization;
using FlowMonitor.Graphs;
using FlowMonitor.Interop;

namespace FlowMonitor.Sampling;

/// <summary>
/// GPU utilisation and VRAM from Windows GPU performance counters.
/// Engine instance names encode luid / physical device / engine / engine type.
/// Temperature, power and fan RPM have no Windows counter; they report Unsupported by design.
/// </summary>
public sealed class GpuSampler
{
    readonly Metrics.Telemetry _tel;
    readonly Pdh.WildcardCounter? _engines;
    readonly Pdh.WildcardCounter? _dedicated;
    readonly Pdh.WildcardCounter? _shared;
    readonly Pdh.WildcardCounter? _committed;

    /// <summary>One adapter, keyed by the LUID Windows reports rather than a guessed index.</summary>
    public sealed class Adapter
    {
        public required string Luid { get; init; }
        public int PhysicalDevice { get; init; }
        public string Name => PhysicalDevice == 0 ? $"GPU {Luid}" : $"GPU {Luid} (phys {PhysicalDevice})";
    }

    /// <summary>One engine type's utilisation, already summed across processes and devices.</summary>
    public sealed record EngineType(string Name, double Percent);

    // Rebuilt every tick on the telemetry thread; readers on the render thread get a
    // copy, so a live rebuild can never invalidate an enumeration mid-frame.
    readonly object _listGate = new();
    readonly List<EngineType> _engineTypes = new();
    readonly List<Adapter> _adapterList = new();

    /// <summary>Engine types as of the last sample, newest state copied out.</summary>
    public EngineType[] EngineSnapshot() { lock (_listGate) return _engineTypes.ToArray(); }

    /// <summary>Adapters as of the last sample, newest state copied out.</summary>
    public Adapter[] AdapterSnapshot() { lock (_listGate) return _adapterList.ToArray(); }

    /// <summary>Headline utilisation: the busiest engine type, 0-100.</summary>
    public TimeSeries Utilization { get; }

    public TimeSeries? EngineSeries(string engineName) => _engineSeries.TryGetValue(engineName, out var s) ? s : null;
    // Concurrent: written on the telemetry thread while the render thread looks series up.
    readonly ConcurrentDictionary<string, TimeSeries> _engineSeries = new(StringComparer.OrdinalIgnoreCase);

    public TimeSeries? DedicatedUsedBytes { get; }
    public TimeSeries? SharedUsedBytes { get; }
    public TimeSeries? CommittedBytes { get; }

    public bool Unsupported { get; }
    public string? UnsupportedReason { get; }

    public double LastUtilization { get; private set; }
    public long LastDedicatedUsed { get; private set; }
    public long LastSharedUsed { get; private set; }
    public long LastCommitted { get; private set; }

    public GpuSampler(Metrics.Telemetry tel)
    {
        _tel = tel;
        Utilization = tel.NewSeries();

        try
        {
            uint rc = Pdh.OpenQuery(out var query);
            if (rc != Pdh.ERROR_SUCCESS || query == IntPtr.Zero)
            {
                Unsupported = true;
                UnsupportedReason = $"PdhOpenQuery 0x{rc:X8}";
                return;
            }
            _query = query;

            _engines = new Pdh.WildcardCounter(query, @"\GPU Engine(*)\Utilization Percentage");
            _dedicated = new Pdh.WildcardCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage");
            _shared = new Pdh.WildcardCounter(query, @"\GPU Adapter Memory(*)\Shared Usage");
            _committed = new Pdh.WildcardCounter(query, @"\GPU Adapter Memory(*)\Committed Bytes");

            if (_engines.Error is not null)
            {
                Unsupported = true;
                UnsupportedReason = _engines.Error;
                return;
            }

            DedicatedUsedBytes = tel.NewSeries();
            SharedUsedBytes = tel.NewSeries();
            CommittedBytes = tel.NewSeries();
        }
        catch (Exception ex)
        {
            Unsupported = true;
            UnsupportedReason = ex.Message;
        }
    }

    IntPtr _query;

    public void Update(double now)
    {
        if (Unsupported) return;

        _engines!.Collect();
        _dedicated?.Collect();
        _shared?.Collect();
        _committed?.Collect();

        // ---- engines -------------------------------------------------------------------
        // Instances come and go with processes; accumulate into a fresh map each tick.
        var byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var adapters = new Dictionary<string, int>(StringComparer.Ordinal);
        int seen = 0;

        _engines.ReadAll((name, value) =>
        {
            seen++;
            if (!TryParseEngine(name, out string luid, out int phys, out string engType)) return;
            byType.TryGetValue(engType, out double running);
            byType[engType] = running + value;          // summed across pids, phys and engine units
            adapters[luid] = phys;
        });

        // Zero instances means not yet primed; hold the previous value.
        if (seen == 0) return;

        var engineList = new List<EngineType>(byType.Count);
        foreach (var kv in byType)
            engineList.Add(new EngineType(NormaliseEngineName(kv.Key), kv.Value));

        var adapterList = new List<Adapter>(adapters.Count);
        foreach (var kv in adapters.OrderBy(k => k.Key, StringComparer.Ordinal))
            adapterList.Add(new Adapter { Luid = kv.Key, PhysicalDevice = kv.Value });

        lock (_listGate)
        {
            _engineTypes.Clear();
            _engineTypes.AddRange(engineList);
            _adapterList.Clear();
            _adapterList.AddRange(adapterList);
        }

        // Engine names include numbered Compute units; fold them into one bar.
        LastUtilization = Math.Clamp(byType.Values.DefaultIfEmpty(0).Max(), 0, 100);
        _tel.Add(Utilization, now, (float)LastUtilization);

        // Keep one series per engine type so a per-engine graph has history, not just a snapshot.
        foreach (var e in engineList)
        {
            if (!_engineSeries.TryGetValue(e.Name, out var s))
            {
                s = _tel.NewSeries();
                _engineSeries[e.Name] = s;
            }
            _tel.Add(s, now, (float)Math.Clamp(e.Percent, 0, 100));
        }

        // ---- adapter memory ------------------------------------------------------------
        long dedicated = 0, shared = 0, committed = 0;
        bool anyMemory = false;

        _dedicated?.ReadAll((_, v) => { dedicated += (long)v; anyMemory = true; });
        _shared?.ReadAll((_, v) => { shared += (long)v; anyMemory = true; });
        _committed?.ReadAll((_, v) => { committed += (long)v; anyMemory = true; });

        if (anyMemory)
        {
            LastDedicatedUsed = dedicated;
            LastSharedUsed = shared;
            LastCommitted = committed;
            // Byte counters stored in float series (graph storage type); precision suffices for axes.
            _tel.Add(DedicatedUsedBytes!, now, LastDedicatedUsed);
            _tel.Add(SharedUsedBytes!, now, LastSharedUsed);
            _tel.Add(CommittedBytes!, now, LastCommitted);
        }
    }

    /// <summary>
    /// Splits <c>pid_..._luid_..._phys_..._eng_..._engtype_...</c> into parts.
    /// Instance names are undocumented; non-matching names are skipped.
    /// </summary>
    static bool TryParseEngine(string name, out string luid, out int phys, out string engType)
    {
        luid = "";
        phys = 0;
        engType = "";
        if (string.IsNullOrEmpty(name)) return false;

        int i = name.IndexOf("_luid_", StringComparison.Ordinal);
        if (i < 0) return false;
        i += 5;

        int eng = name.IndexOf("_phys_", i, StringComparison.Ordinal);
        if (eng < 0) return false;
        luid = name[i..eng];

        int engIdx = name.IndexOf("_eng_", eng, StringComparison.Ordinal);
        if (engIdx < 0) return false;
        eng += 6;
        if (!int.TryParse(name[eng..engIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out phys))
            return false;

        int typeAt = name.IndexOf("_engtype_", engIdx, StringComparison.Ordinal);
        if (typeAt < 0) return false;
        engType = name[(typeAt + 9)..];
        return engType.Length > 0;
    }

    /// <summary>Folds numbered Compute units into one bar.</summary>
    static string NormaliseEngineName(string raw)
    {
        if (raw.StartsWith("Compute_", StringComparison.OrdinalIgnoreCase)) return "Compute";
        return raw;
    }

    public void Dispose()
    {
        _engines?.Dispose();
        _dedicated?.Dispose();
        _shared?.Dispose();
        _committed?.Dispose();
        if (_query != IntPtr.Zero) { Pdh.PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}
