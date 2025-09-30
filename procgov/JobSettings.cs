using MessagePack;
using System.Text;
using Windows.Win32.System.Threading;

namespace ProcessGovernor;

[MessagePackObject]
public sealed partial class JobSettings(ulong maxProcessMemory = 0, ulong maxJobMemory = 0,
    ulong maxWorkingSetSize = 0, ulong minWorkingSetSize = 0, GroupAffinity[]? cpuAffinity = null,
    uint cpuMaxRate = 0, ulong maxBandwidth = 0, uint processUserTimeLimitInMilliseconds = 0,
    uint jobUserTimeLimitInMilliseconds = 0, uint clockTimeLimitInMilliseconds = 0, bool propagateOnChildProcesses = false,
    uint activeProcessLimit = 0, PriorityClass priorityClass = PriorityClass.Undefined
)
{
    [Key(0)] public ulong MaxProcessMemory => maxProcessMemory;

    [Key(1)] public ulong MaxJobMemory => maxJobMemory;

    [Key(2)] public ulong MaxWorkingSetSize => maxWorkingSetSize;

    [Key(3)] public ulong MinWorkingSetSize => minWorkingSetSize;

    [Key(4)] public GroupAffinity[]? CpuAffinity => cpuAffinity;

    [Key(5)] public uint CpuMaxRate => cpuMaxRate;

    [Key(6)] public ulong MaxBandwidth => maxBandwidth;

    [Key(7)] public uint ProcessUserTimeLimitInMilliseconds => processUserTimeLimitInMilliseconds;

    [Key(8)] public uint JobUserTimeLimitInMilliseconds => jobUserTimeLimitInMilliseconds;

    [Key(9)] public uint ClockTimeLimitInMilliseconds => clockTimeLimitInMilliseconds;

    [Key(10)] public bool PropagateOnChildProcesses => propagateOnChildProcesses;

    [Key(11)] public uint ActiveProcessLimit => activeProcessLimit;

    [Key(12)] public PriorityClass PriorityClass => priorityClass;

    public override bool Equals(object? obj)
    {
        return obj is JobSettings settings &&
               MaxProcessMemory == settings.MaxProcessMemory &&
               MaxJobMemory == settings.MaxJobMemory &&
               MaxWorkingSetSize == settings.MaxWorkingSetSize &&
               MinWorkingSetSize == settings.MinWorkingSetSize &&
               IsCpuAffinityEqual() &&
               CpuMaxRate == settings.CpuMaxRate &&
               MaxBandwidth == settings.MaxBandwidth &&
               ProcessUserTimeLimitInMilliseconds == settings.ProcessUserTimeLimitInMilliseconds &&
               JobUserTimeLimitInMilliseconds == settings.JobUserTimeLimitInMilliseconds &&
               ClockTimeLimitInMilliseconds == settings.ClockTimeLimitInMilliseconds &&
               PropagateOnChildProcesses == settings.PropagateOnChildProcesses &&
               ActiveProcessLimit == settings.ActiveProcessLimit &&
               PriorityClass == settings.PriorityClass;

        bool IsCpuAffinityEqual()
        {
            return (CpuAffinity == settings.CpuAffinity) ||
                (CpuAffinity is not null && settings.CpuAffinity is not null &&
                        Enumerable.SequenceEqual(CpuAffinity, settings.CpuAffinity));
        }
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(MaxProcessMemory);
        hash.Add(MaxJobMemory);
        hash.Add(MaxWorkingSetSize);
        hash.Add(MinWorkingSetSize);
        if (CpuAffinity != null)
        {
            foreach (var aff in CpuAffinity)
            {
                hash.Add(aff.GetHashCode());
            }
        }
        else
        {
            hash.Add(CpuAffinity);
        }
        hash.Add(CpuMaxRate);
        hash.Add(MaxBandwidth);
        hash.Add(ProcessUserTimeLimitInMilliseconds);
        hash.Add(JobUserTimeLimitInMilliseconds);
        hash.Add(ClockTimeLimitInMilliseconds);
        hash.Add(PropagateOnChildProcesses);
        hash.Add(ActiveProcessLimit);
        hash.Add(PriorityClass);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        StringBuilder buffer = new();
        buffer.Append($"{{ MaxProcessMemory: {MaxProcessMemory}, ");
        buffer.Append($"MaxJobMemory: {MaxJobMemory}, ");
        buffer.Append($"MaxWorkingSetSize: {MaxWorkingSetSize}, ");
        buffer.Append($"MinWorkingSetSize: {MinWorkingSetSize}, ");
        if (CpuAffinity != null)
        {
            buffer.Append($"CpuAffinity: [");
            buffer.Append(string.Join(',', CpuAffinity.Select(aff => $"({aff.GroupNumber},0x{aff.Affinity:x})")));
            buffer.Append("], ");
        }
        else
        {
            buffer.Append($"CpuAffinity: null");
        }
        buffer.Append($"CpuMaxRate: {CpuMaxRate}, ");
        buffer.Append($"MaxBandwidth: {MaxBandwidth}, ");
        buffer.Append($"ProcessUserTimeLimitInMilliseconds: {ProcessUserTimeLimitInMilliseconds}, ");
        buffer.Append($"JobUserTimeLimitInMilliseconds: {JobUserTimeLimitInMilliseconds}, ");
        buffer.Append($"ClockTimeLimitInMilliseconds: {ClockTimeLimitInMilliseconds}, ");
        buffer.Append($"PropagateOnChildProcesses: {PropagateOnChildProcesses}, ");
        buffer.Append($"ActiveProcessLimit: {ActiveProcessLimit}, ");
        buffer.Append($"PriorityClass: {PriorityClass} }}");
        return buffer.ToString();
    }

    public JobSettings Merge(JobSettings other)
    {
        return new JobSettings(
            maxProcessMemory: this.MaxProcessMemory > 0 ? this.MaxProcessMemory : other.MaxProcessMemory,
            maxJobMemory: this.MaxJobMemory > 0 ? this.MaxJobMemory : other.MaxJobMemory,
            maxWorkingSetSize: this.MaxWorkingSetSize > 0 ? this.MaxWorkingSetSize : other.MaxWorkingSetSize,
            minWorkingSetSize: this.MinWorkingSetSize > 0 ? this.MinWorkingSetSize : other.MinWorkingSetSize,
            cpuAffinity: this.CpuAffinity is not null ? this.CpuAffinity : other.CpuAffinity,
            cpuMaxRate: this.CpuMaxRate > 0 ? this.CpuMaxRate : other.CpuMaxRate,
            maxBandwidth: this.MaxBandwidth > 0 ? this.MaxBandwidth : other.MaxBandwidth,
            processUserTimeLimitInMilliseconds: this.ProcessUserTimeLimitInMilliseconds > 0 ?
                this.ProcessUserTimeLimitInMilliseconds : other.ProcessUserTimeLimitInMilliseconds,
            jobUserTimeLimitInMilliseconds: this.JobUserTimeLimitInMilliseconds > 0 ?
                this.JobUserTimeLimitInMilliseconds : other.JobUserTimeLimitInMilliseconds,
            // clock time limit is governed by the running cmd client, so we always overwrite its value
            clockTimeLimitInMilliseconds: other.ClockTimeLimitInMilliseconds,
            propagateOnChildProcesses: this.PropagateOnChildProcesses || other.PropagateOnChildProcesses,
            activeProcessLimit: this.ActiveProcessLimit > 0 ? this.ActiveProcessLimit : other.ActiveProcessLimit,
            priorityClass: this.PriorityClass != PriorityClass.Undefined ? this.PriorityClass : other.PriorityClass
        );
    }

    public bool IsEmpty() => MaxProcessMemory == 0 && MaxJobMemory == 0
        && MaxWorkingSetSize == 0 && MinWorkingSetSize == 0 && CpuAffinity is null
        && CpuMaxRate == 0 && MaxBandwidth == 0 && ProcessUserTimeLimitInMilliseconds == 0
        && JobUserTimeLimitInMilliseconds == 0 && ClockTimeLimitInMilliseconds == 0 && !propagateOnChildProcesses
        && ActiveProcessLimit == 0 && PriorityClass == PriorityClass.Undefined;
}

[MessagePackObject]
public record GroupAffinity(
    [property: Key(0)] ushort GroupNumber,
    [property: Key(1)] ulong Affinity
);

public enum PriorityClass : uint
{
    Undefined = 0,
    Idle = PROCESS_CREATION_FLAGS.IDLE_PRIORITY_CLASS,
    BelowNormal = PROCESS_CREATION_FLAGS.BELOW_NORMAL_PRIORITY_CLASS,
    Normal = PROCESS_CREATION_FLAGS.NORMAL_PRIORITY_CLASS,
    AboveNormal = PROCESS_CREATION_FLAGS.ABOVE_NORMAL_PRIORITY_CLASS,
    High = PROCESS_CREATION_FLAGS.HIGH_PRIORITY_CLASS,
    Realtime = PROCESS_CREATION_FLAGS.REALTIME_PRIORITY_CLASS,
}

