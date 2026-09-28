using System.Runtime.InteropServices;
using FlowMonitor.Interop;
using static FlowMonitor.Interop.Native;

namespace FlowMonitor.Host;

[StructLayout(LayoutKind.Sequential)]
public struct DWM_TIMING_INFO
{
    public ulong QpcCompose;
    public ulong QpcVBlank;
    public uint Numerator, Denominator;
    public uint UpdateFlags;
    public int UsedInBeginFrame;
}

public static class DwmApi
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, out DWM_TIMING_INFO info);

    public static double CompositionRefreshRate()
    {
        try
        {
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, out var t) == 0 && t.Denominator > 0 && t.Numerator > 0)
                return t.Numerator / (double)t.Denominator;
        }
        catch { }
        return 60.0;
    }
}
