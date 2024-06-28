using MessagePack;
using System.Runtime.InteropServices;

namespace ProcessGovernor;

[MessagePackObject]
public record JobSettings(
    [property: Key(0)] ulong MaxProcessMemory = 0,
    [property: Key(1)] ulong MaxJobMemory = 0,
    [property: Key(2)] ulong MaxWorkingSetSize = 0,
    [property: Key(3)] ulong MinWorkingSetSize = 0,
    [property: Key(4)] ulong CpuAffinityMask = 0,
    [property: Key(5)] uint CpuMaxRate = 0,
    [property: Key(6)] ulong MaxBandwidth = 0,
    [property: Key(7)] uint ProcessUserTimeLimitInMilliseconds = 0,
    [property: Key(8)] uint JobUserTimeLimitInMilliseconds = 0,
    [property: Key(9)] uint ClockTimeLimitInMilliseconds = 0,
    [property: Key(10)] bool PropagateOnChildProcesses = false,
    [property: Key(11)] ushort NumaNode = 0xffff,
    [property: Key(12)] uint ActiveProcessLimit = 0
);

