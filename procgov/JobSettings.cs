using System.Runtime.InteropServices;

namespace ProcessGovernor;

[StructLayout(LayoutKind.Sequential)]
record struct JobSettings(
    ulong MaxProcessMemory,
    ulong MaxJobMemory,
    ulong MaxWorkingSetSize,
    ulong MinWorkingSetSize,
    ulong CpuAffinityMask,
    uint CpuMaxRate,
    ulong MaxBandwidth,
    uint ProcessUserTimeLimitInMilliseconds,
    uint JobUserTimeLimitInMilliseconds,
    uint ClockTimeLimitInMilliseconds,
    bool PropagateOnChildProcesses,
    ushort NumaNode = 0xffff
);

