using System.Runtime.InteropServices;
using FlowMonitor.Graphs;
using FlowMonitor.Interop;

namespace FlowMonitor.Sampling;

/// <summary>
/// Per-interface throughput from <c>GetIfTable2</c>, reported the way Task Manager does it:
/// the *primary* interface (the non-loopback interface that is up and carrying the most
/// traffic) is graphed, with send and receive as separate series.
/// </summary>
public sealed unsafe class NetworkSampler
{
    readonly Metrics.Telemetry _tel;
    readonly int _stride = Marshal.SizeOf<MIB_IF_ROW2>();
    readonly Dictionary<ulong, ulong> _prevIn = new();
    readonly Dictionary<ulong, ulong> _prevOut = new();
    readonly double[] _inRate = new double[64];
    readonly double[] _outRate = new double[64];

    IntPtr _table;
    long _lastTick;
    bool _primed;

    public TimeSeries SendBytes { get; }
    public TimeSeries RecvBytes { get; }

    /// <summary>Friendly name of the interface currently being graphed.</summary>
    public string InterfaceName { get; private set; } = "Network";
    public string InterfaceDescription { get; private set; } = "";
    public double SendLinkSpeedBps { get; private set; }
    public double RecvLinkSpeedBps { get; private set; }
    public bool Available { get; private set; }

    public float LastSend { get; private set; }
    public float LastRecv { get; private set; }

    public NetworkSampler(Metrics.Telemetry tel)
    {
        _tel = tel;
        SendBytes = tel.NewSeries();
        RecvBytes = tel.NewSeries();
    }

    public void Update(double now)
    {
        long tick = Environment.TickCount64;
        double interval = _lastTick == 0 ? 1.0 : Math.Max(0.05, (tick - _lastTick) / 1000.0);
        _lastTick = tick;

        if (!Refresh(interval)) return;

        // Graph the busiest up, non-loopback interface this tick.
        int best = -1;
        double bestRate = -1;
        int count = Math.Min(_inRate.Length - 1, NumRows - 1);
        for (int i = 0; i <= count; i++)
        {
            double r = _inRate[i] + _outRate[i];
            if (r > bestRate) { bestRate = r; best = i; }
        }
        if (best < 0) return;

        ref MIB_IF_ROW2 row = ref Row(best);
        string alias = row.AliasString;
        InterfaceName = alias.Length > 0 ? alias : "Ethernet";
        InterfaceDescription = row.DescriptionString;
        RecvLinkSpeedBps = row.ReceiveLinkSpeed;
        SendLinkSpeedBps = row.TransmitLinkSpeed;

        if (!_primed) { _primed = true; return; }   // first tick only establishes the baselines
        LastSend = (float)_outRate[best];
        LastRecv = (float)_inRate[best];
        _tel.Add(SendBytes, now, LastSend);
        _tel.Add(RecvBytes, now, LastRecv);
    }

    int NumRows;
    const int NumEntriesOffset = 8;   // ULONG NumEntries + 4 bytes alignment padding

    bool Refresh(double interval)
    {
        // GetIfTable2 allocates a fresh buffer per call; free the previous one first.
        if (_table != IntPtr.Zero) { Marshal.FreeHGlobal(_table); _table = IntPtr.Zero; }
        uint rc = IpHlpApi.GetIfTable2(out _table);
        if (rc != 0 || _table == IntPtr.Zero) { Available = false; return false; }

        uint n = *(uint*)_table;
        NumRows = (int)n;
        if (NumRows <= 0) { Available = false; return false; }

        int limit = Math.Min(NumRows, _inRate.Length);
        for (int i = 0; i < limit; i++)
        {
            ref MIB_IF_ROW2 row = ref Row(i);
            if (!row.IsUp || row.IsLoopback) { _inRate[i] = 0; _outRate[i] = 0; continue; }

            // LUID is 64-bit; key on the full value to avoid interface collisions.
            ulong key = row.InterfaceLuid;
            if (_prevIn.TryGetValue(key, out ulong pIn) && _prevOut.TryGetValue(key, out ulong pOut))
            {
                // Backwards counters mean interface reset; report idle, not a spike.
                _inRate[i] = row.InOctets >= pIn ? (row.InOctets - pIn) / interval : 0;
                _outRate[i] = row.OutOctets >= pOut ? (row.OutOctets - pOut) / interval : 0;
            }
            else
            {
                _inRate[i] = 0; _outRate[i] = 0;
            }
            _prevIn[key] = row.InOctets;
            _prevOut[key] = row.OutOctets;
        }
        for (int i = limit; i < _inRate.Length; i++) { _inRate[i] = 0; _outRate[i] = 0; }

        // Drop baselines for vanished interfaces.
        if (_prevIn.Count > NumRows * 4)
        {
            var live = new HashSet<ulong>();
            for (int i = 0; i < limit; i++) live.Add(Row(i).InterfaceLuid);
            foreach (var k in _prevIn.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _prevIn.Remove(k);
                _prevOut.Remove(k);
            }
        }

        Available = true;
        return true;
    }

    ref MIB_IF_ROW2 Row(int i) => ref *(MIB_IF_ROW2*)((byte*)_table + NumEntriesOffset + i * _stride);

    public void Dispose()
    {
        if (_table != IntPtr.Zero) { Marshal.FreeHGlobal(_table); _table = IntPtr.Zero; }
    }
}
