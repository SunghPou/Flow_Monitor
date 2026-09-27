using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// Minimal PDH client for PhysicalDisk / GPU Engine / GPU Adapter Memory counters.
/// </summary>
internal static class Pdh
{
    public const uint PDH_FMT_COUNTERVALUE_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const uint PDH_FMT_NOSCALE = 0x00001000;
    public const uint ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Explicit)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
        [FieldOffset(8)] public long largeValue;
        [FieldOffset(8)] public long AnsiValueValue;
    }

    /// <summary>
    /// No PdhOpenMachine exists in pdh.dll; NULL data source means the local machine.
    /// Local counter paths use a single leading backslash: \Object\Counter.
    /// </summary>
    // NOTE: dataSource is IntPtr, never IntPtr?. Nullable IntPtr cannot be marshalled.
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    public static extern uint PdhOpenQueryW(IntPtr dataSource, uint userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterValue")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    public static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>Returned when the ItemBuffer is too small; the required size is reported back.</summary>
    public const uint PDH_MORE_DATA = 0x800007D2;
    /// <summary>0x800007D5 — counter is valid but has no collected sample yet.</summary>
    public const uint PDH_NO_DATA = 0x800007D5;
    /// <summary>0x800007D1 — the wildcard currently matches no instances.</summary>
    public const uint PDH_CSTATUS_NO_INSTANCE = 0x800007D1;
    /// <summary>0xC0000BC6 — returned data is not valid (seen on "(_Total)" instances here).</summary>
    public const uint PDH_INVALID_DATA = 0xC0000BC6;

    public const uint PDH_CSTATUS_NO_COUNTER = 0xC0000BB9;
    public const uint PDH_CSTATUS_BAD_COUNTERNAME = 0xC0000BC0;
    public const uint PDH_INVALID_ARGUMENT = 0xC0000BBD;

    /// <summary>
    /// One wildcard-counter instance: szName pointer plus value (x64: 8 bytes padding + double).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    /// <summary>
    /// Five parameters, no lpdwType. PDH_FMT_1000 is rejected; do not scale.
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format,
        ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    /// <summary>Opens a query against the local machine. Returns 0 and a valid handle on success.</summary>
    public static uint OpenQuery(out IntPtr query)
    {
        uint rc = PdhOpenQueryW(IntPtr.Zero, 0, out query);
        return rc;
    }

    /// <summary>A live counter. <see cref="Collect"/> must be called once per sample before reading.</summary>
    public sealed class Counter : IDisposable
    {
        public IntPtr Query { get; }
        public IntPtr Handle { get; }
        readonly string _path;
        public string? Error { get; private set; }

        public Counter(IntPtr query, string path)
        {
            _path = path;
            Query = query;
            uint rc = PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var h);
            Handle = h;
            if (rc != ERROR_SUCCESS || h == IntPtr.Zero)
                Error = $"PdhAddEnglishCounter 0x{rc:X8} for '{path}'";
        }

        public bool Valid => Handle != IntPtr.Zero;

        public void Collect() { if (Valid) PdhCollectQueryData(Query); }

        /// <summary>Last formatted value, or NaN when the counter has no valid sample yet.</summary>
        public double Read()
        {
            if (!Valid) return double.NaN;
            uint rc = PdhGetFormattedCounterValue(Handle,
                PDH_FMT_COUNTERVALUE_DOUBLE | PDH_FMT_NOCAP100 | PDH_FMT_NOSCALE,
                IntPtr.Zero, out var v);
            if (rc != ERROR_SUCCESS || v.CStatus != 0) return double.NaN;
            return v.doubleValue;
        }

        public void Dispose()
        {
            if (Valid) PdhCloseQuery(Handle);
        }

        public override string ToString() => _path;
    }

    /// <summary>
    /// A counter whose path contains a wildcard instance list, e.g.
    /// <c>\GPU Engine(*)\Utilization Percentage</c>. Reads every current instance in one call.
    /// </summary>
    public sealed class WildcardCounter : IDisposable
    {
        public IntPtr Query { get; }
        public IntPtr Handle { get; }
        readonly string _path;
        public string? Error { get; private set; }

        readonly int _itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
        IntPtr _buffer = IntPtr.Zero;
        uint _bufferBytes;

        public WildcardCounter(IntPtr query, string path)
        {
            _path = path;
            Query = query;
            uint rc = PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var h);
            Handle = h;
            if (rc != ERROR_SUCCESS || h == IntPtr.Zero)
                Error = $"PdhAddEnglishCounter 0x{rc:X8} for '{path}'";
        }

        public bool Valid => Handle != IntPtr.Zero;

        public void Collect() { if (Valid) PdhCollectQueryData(Query); }

        public int ItemSize => _itemSize;
        public uint LastError { get; private set; }
        public int LastItemCount { get; private set; }
        /// <summary>Human-readable reason for the last failure, when the status code is not enough.</summary>
        public string? LastErrorDetail { get; private set; }

        /// <summary>Ceiling on believable array size (64 MB); larger sizes are rejected.</summary>
        const uint MaxArrayBytes = 64u * 1024 * 1024;

        void EnsureBuffer(uint bytes)
        {
            if (bytes < 4096) bytes = 4096;
            if (_buffer != IntPtr.Zero && _bufferBytes >= bytes) return;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferBytes = bytes;
            _buffer = Marshal.AllocHGlobal(checked((int)_bufferBytes));
        }

        /// <summary>
        /// Reads every instance into <paramref name="sink"/>. False means unreadable;
        /// empty is legitimate (no instances). Uses the two-call probe-then-read protocol.
        /// </summary>
        public bool ReadAll(Action<string, double> sink)
        {
            LastError = 0;
            LastItemCount = 0;
            if (!Valid) { LastError = PDH_INVALID_ARGUMENT; return false; }

            const uint Format = PDH_FMT_COUNTERVALUE_DOUBLE | PDH_FMT_NOCAP100 | PDH_FMT_NOSCALE;

            uint bytes = 0, items = 0;
            uint rc = PdhGetFormattedCounterArrayW(Handle, Format, ref bytes, ref items, IntPtr.Zero);
            if (rc != PDH_MORE_DATA)
            {
                // NO_DATA / NO_INSTANCE mean "not yet collected", not broken.
                if (rc == ERROR_SUCCESS || rc == PDH_NO_DATA || rc == PDH_CSTATUS_NO_INSTANCE)
                    return true;
                LastError = rc;
                return false;
            }

            // Reject implausible sizes before allocating; never trust them into an allocation.
            if (bytes == 0 || bytes > MaxArrayBytes)
            {
                LastError = PDH_INVALID_ARGUMENT;
                LastErrorDetail = $"implausible required size {bytes} bytes for '{_path}'";
                return false;
            }

            // Margin: an instance can appear between the probe and the read.
            EnsureBuffer(bytes + (uint)_itemSize * 8);

            bytes = _bufferBytes;
            items = 0;
            rc = PdhGetFormattedCounterArrayW(Handle, Format, ref bytes, ref items, _buffer);
            if (rc != ERROR_SUCCESS) { LastError = rc; return false; }

            // Clamp the item count to the owned buffer before iterating.
            if (items > _bufferBytes / (uint)_itemSize) items = _bufferBytes / (uint)_itemSize;

            LastItemCount = (int)items;
            for (int i = 0; i < items; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(_buffer + i * _itemSize);
                if (item.szName == IntPtr.Zero) continue;
                string name = Marshal.PtrToStringUni(item.szName) ?? string.Empty;
                if (item.FmtValue.CStatus != 0) continue;      // instance not yet primed
                sink(name, item.FmtValue.doubleValue);
            }
            return true;
        }

        public void Dispose()
        {
            if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
            if (Valid) PdhCloseQuery(Handle);
        }

        public override string ToString() => _path;
    }
}
