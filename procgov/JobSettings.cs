using MessagePack;
using System.Runtime.InteropServices;

namespace ProcessGovernor;

[MessagePackObject]
public record struct JobSettings(
    [property: Key(0)] ulong MaxProcessMemory,
    [property: Key(1)] ulong MaxJobMemory,
    [property: Key(2)] ulong MaxWorkingSetSize,
    [property: Key(3)] ulong MinWorkingSetSize,
    [property: Key(4)] ulong CpuAffinityMask,
    [property: Key(5)] uint CpuMaxRate,
    [property: Key(6)] ulong MaxBandwidth,
    [property: Key(7)] uint ProcessUserTimeLimitInMilliseconds,
    [property: Key(8)] uint JobUserTimeLimitInMilliseconds,
    [property: Key(9)] uint ClockTimeLimitInMilliseconds,
    [property: Key(10)] bool PropagateOnChildProcesses,
    [property: Key(11)] ushort NumaNode = 0xffff
);

