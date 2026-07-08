using Nerdbank.MessagePack;
using PolyType;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

namespace ProcessGovernor.Library;

public static class Updater
{
#pragma warning disable CS0612 // Type or member is obsolete
    public static JobSettings Update(this JobSettings_v3 jobSettings) =>
        new(
            jobSettings.MaxProcessMemory,
            jobSettings.MaxJobMemory,
            jobSettings.MaxWorkingSetSize,
            jobSettings.MinWorkingSetSize,
            jobSettings.CpuAffinity is null ? [] : [.. jobSettings.CpuAffinity],
            jobSettings.CpuMaxRate,
            jobSettings.MaxBandwidth,
            jobSettings.ProcessUserTimeLimitInMilliseconds,
            jobSettings.JobUserTimeLimitInMilliseconds,
            jobSettings.JobClockTimeLimitInMilliseconds,
            jobSettings.PropagateOnChildProcesses,
            jobSettings.ActiveProcessLimit,
            jobSettings.PriorityClass,
            RunModes.Default,
            [],
            [],
            PowerThrottling.Undefined
        );

    public static JobSettings ToCurrent(this JobSettings_v3 jobSettings) =>
        Update(jobSettings);

#pragma warning restore CS0612 // Type or member is obsolete
}

[Obsolete]
[JsonConverter(typeof(MessagePackToJsonConverter<JobSettings_v3>))]
[GenerateShape]
public sealed partial record JobSettings_v3(
    [property: Key(0)] ulong MaxProcessMemory,
    [property: Key(1)] ulong MaxJobMemory,
    [property: Key(2)] ulong MaxWorkingSetSize,
    [property: Key(3)] ulong MinWorkingSetSize,
    [property: Key(4)] GroupAffinity[]? CpuAffinity,
    [property: Key(5)] uint CpuMaxRate,
    [property: Key(6)] ulong MaxBandwidth,
    [property: Key(7)] uint ProcessUserTimeLimitInMilliseconds,
    [property: Key(8)] uint JobUserTimeLimitInMilliseconds,
    [property: Key(9)] uint JobClockTimeLimitInMilliseconds,
    [property: Key(10)] bool PropagateOnChildProcesses,
    [property: Key(11)] uint ActiveProcessLimit,
    [property: Key(12)] PriorityClass PriorityClass)
{
    public readonly static int Version = 3;

    public readonly static JobSettings_v3 Empty =
        new(MaxProcessMemory: 0,
            MaxJobMemory: 0,
            MaxWorkingSetSize: 0,
            MinWorkingSetSize: 0,
            CpuAffinity: [],
            CpuMaxRate: 0,
            MaxBandwidth: 0,
            ProcessUserTimeLimitInMilliseconds: 0,
            JobUserTimeLimitInMilliseconds: 0,
            JobClockTimeLimitInMilliseconds: 0,
            PropagateOnChildProcesses: false,
            ActiveProcessLimit: 0,
            PriorityClass: PriorityClass.Undefined);


    public bool Equals(JobSettings_v3? obj) => obj is JobSettings_v3 settings &&
        MaxProcessMemory == settings.MaxProcessMemory &&
        MaxJobMemory == settings.MaxJobMemory &&
        MaxWorkingSetSize == settings.MaxWorkingSetSize &&
        MinWorkingSetSize == settings.MinWorkingSetSize &&
        (CpuAffinity == settings.CpuAffinity || CpuAffinity.SequenceEqual(settings.CpuAffinity)) &&
        CpuMaxRate == settings.CpuMaxRate &&
        MaxBandwidth == settings.MaxBandwidth &&
        ProcessUserTimeLimitInMilliseconds == settings.ProcessUserTimeLimitInMilliseconds &&
        JobUserTimeLimitInMilliseconds == settings.JobUserTimeLimitInMilliseconds &&
        JobClockTimeLimitInMilliseconds == settings.JobClockTimeLimitInMilliseconds &&
        PropagateOnChildProcesses == settings.PropagateOnChildProcesses &&
        ActiveProcessLimit == settings.ActiveProcessLimit &&
        PriorityClass == settings.PriorityClass;

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(MaxProcessMemory);
        hash.Add(MaxJobMemory);
        hash.Add(MaxWorkingSetSize);
        hash.Add(MinWorkingSetSize);
        // We need to iterate through all the elements because we use value (not reference) equality
        if (CpuAffinity is not null)
        {
            foreach (var aff in CpuAffinity)
            {
                hash.Add(aff.GetHashCode());
            }
        }
        hash.Add(CpuMaxRate);
        hash.Add(MaxBandwidth);
        hash.Add(ProcessUserTimeLimitInMilliseconds);
        hash.Add(JobUserTimeLimitInMilliseconds);
        hash.Add(JobClockTimeLimitInMilliseconds);
        hash.Add(PropagateOnChildProcesses);
        hash.Add(ActiveProcessLimit);
        hash.Add(PriorityClass);
        return hash.ToHashCode();
    }
}

