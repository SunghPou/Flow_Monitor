using System.Runtime.InteropServices;

namespace FlowMonitor.Sampling;

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
{
    public long IdleTime;
    public long KernelTime;
    public long UserTime;
    public long DpcTime;
    public long InterruptTime;
    public uint InterruptCount;
}

public static class NtDll
{
    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformation,
        int SystemInformationLength,
        out int ReturnLength);

    public const int SystemProcessorPerformanceInformation = 8;

    public static int ProcessorCount { get; } = Math.Max(1, Environment.ProcessorCount);
}
