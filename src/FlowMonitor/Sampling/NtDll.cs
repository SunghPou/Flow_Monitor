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

/// <summary>SYSTEM_PERFORMANCE_INFORMATION (NtQuerySystemInformation class 2).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_PERFORMANCE_INFORMATION
{
    public long IdleTime;
    public long NtDpcTime;
    public long NtInterruptTime;
    public long SystemTime;
    public uint VmIrqlLine;
    public uint VmIrql;
    public uint VmHighestIrql;
    public uint VmCurrentIrql;
    public uint VmCurrentPci;
    public ulong TickCount;
    public uint TickCountMultiplier;
    public uint PerformanceCount;
    public uint NtSystemTimeIncrement;
    public uint InterruptTimeIncrement;
    public int DpcTimeIncrement;
    public int DpcBias;
    public uint ApplicationCpuCount;
    public uint MaximumProcessorPerformance;
    public uint MaximumProcessorFrequency;
    public uint SystemTimeIncrement;
    public uint LookasideSideOptIn;
    public uint LookasideIncrement;
    public ulong PerformanceCountEx;
    public uint PerformanceFlags;
    public ulong ThreadPriorityAssignment;
}

public static class NtDll
{
    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformation,
        int SystemInformationLength,
        out int ReturnLength);

    public const int SystemBasicInformation = 0;
    public const int SystemPerformanceInformation = 2;
    public const int SystemProcessorPerformanceInformation = 8;

    public static int ProcessorCount { get; } = Math.Max(1, Environment.ProcessorCount);

    public static SYSTEM_PERFORMANCE_INFORMATION GetPerformanceInfo()
    {
        int len = Marshal.SizeOf<SYSTEM_PERFORMANCE_INFORMATION>();
        IntPtr p = Marshal.AllocHGlobal(len);
        try
        {
            int status = NtQuerySystemInformation(SystemPerformanceInformation, p, len, out _);
            if (status != 0) return default;
            return Marshal.PtrToStructure<SYSTEM_PERFORMANCE_INFORMATION>(p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }
}
