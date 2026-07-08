using Nerdbank.MessagePack;
using PolyType;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Win32.System.Threading;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

namespace ProcessGovernor.Library;

public record struct ConfigJobId(string Id)
{
    public ConfigJobId() : this("") { }

    public override readonly string ToString() => Id.ToString();
}

/// <summary>
/// A class for storing all process group settings (both: controlled by system jobs and 
/// governed by process governor). If a given limit is not a system job limit, it may 
/// have some delay when applying.
/// </summary>
/// <param name="MaxProcessMemory"></param>
/// <param name="MaxJobMemory"></param>
/// <param name="MaxWorkingSetSize"></param>
/// <param name="MinWorkingSetSize"></param>
/// <param name="CpuAffinity"></param>
/// <param name="CpuMaxRate"></param>
/// <param name="MaxBandwidth"></param>
/// <param name="ProcessUserTimeLimitInMilliseconds"></param>
/// <param name="JobUserTimeLimitInMilliseconds"></param>
/// <param name="JobClockTimeLimitInMilliseconds"></param>
/// <param name="PropagateOnChildProcesses"></param>
/// <param name="ActiveProcessLimit"></param>
/// <param name="PriorityClass"></param>
/// <param name="RunMode"></param>
/// <param name="Privileges"></param>
/// <param name="Environment">Environment variables may require expansion (for example, a valid value could be "%PATH%;C:\\temp")</param>
[JsonConverter(typeof(MessagePackToJsonConverter<JobSettings>))]
[GenerateShape]
public sealed partial record JobSettings(
    [property: Key(0)] ulong MaxProcessMemory,
    [property: Key(1)] ulong MaxJobMemory,
    [property: Key(2)] ulong MaxWorkingSetSize,
    [property: Key(3)] ulong MinWorkingSetSize,
    [property: Key(4)] ImmutableArray<GroupAffinity> CpuAffinity,
    [property: Key(5)] uint CpuMaxRate,
    [property: Key(6)] ulong MaxBandwidth,
    [property: Key(7)] uint ProcessUserTimeLimitInMilliseconds,
    [property: Key(8)] uint JobUserTimeLimitInMilliseconds,
    [property: Key(9)] uint JobClockTimeLimitInMilliseconds, // not a job setting
    [property: Key(10)] bool PropagateOnChildProcesses,
    [property: Key(11)] uint ActiveProcessLimit,
    [property: Key(12)] PriorityClass PriorityClass,
    [property: Key(13)] IRunMode RunMode, // not a job setting
    [property: Key(14)] ImmutableArray<string> Privileges, // not a job setting
    [property: Key(15)] ImmutableDictionary<string, string> Environment, //  not a job setting
    [property: Key(16)] PowerThrottling PowerThrottling // not a job setting
)
{
    public readonly static byte Version = 4;

    public readonly static JobSettings Empty =
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
            PriorityClass: PriorityClass.Undefined,
            RunMode: RunModes.Default,
            Privileges: [],
            Environment: [],
            PowerThrottling: PowerThrottling.Undefined
        );


    public bool Equals(JobSettings? obj) => obj is JobSettings settings &&
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
        PriorityClass == settings.PriorityClass &&
        RunMode.IsEqualTo(settings.RunMode) &&
        (Privileges == settings.Privileges || Privileges.SequenceEqual(settings.Privileges)) &&
        (Environment == settings.Environment || AreDictionariesEqual(Environment, settings.Environment)) &&
        (PowerThrottling == settings.PowerThrottling);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(MaxProcessMemory);
        hash.Add(MaxJobMemory);
        hash.Add(MaxWorkingSetSize);
        hash.Add(MinWorkingSetSize);
        // We need to iterate through all the elements because we use value (not reference) equality
        foreach (var aff in CpuAffinity) { hash.Add(aff.GetHashCode()); }
        hash.Add(CpuMaxRate);
        hash.Add(MaxBandwidth);
        hash.Add(ProcessUserTimeLimitInMilliseconds);
        hash.Add(JobUserTimeLimitInMilliseconds);
        hash.Add(JobClockTimeLimitInMilliseconds);
        hash.Add(PropagateOnChildProcesses);
        hash.Add(ActiveProcessLimit);
        hash.Add(PriorityClass);
        hash.Add(RunMode);
        foreach (var priv in Privileges) { hash.Add(priv.GetHashCode()); }
        foreach (var env in Environment) { hash.Add(env.GetHashCode()); }
        hash.Add(PowerThrottling);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        StringBuilder buffer = new();
        buffer.Append("{MaxProcessMemory:").Append(MaxProcessMemory).Append(',');
        buffer.Append("MaxJobMemory:").Append(MaxJobMemory).Append(',');
        buffer.Append("MaxWorkingSetSize:").Append(MaxWorkingSetSize).Append(',');
        buffer.Append("MinWorkingSetSize:").Append(MinWorkingSetSize).Append(',');
        buffer.Append("CpuAffinity:[").Append(
            string.Join(',', CpuAffinity.Select(aff => $"({aff.GroupNumber}:0x{aff.Affinity:x})"))).Append(']').Append(',');
        buffer.Append("CpuMaxRate:").Append(CpuMaxRate).Append(',');
        buffer.Append("MaxBandwidth:").Append(MaxBandwidth).Append(',');
        buffer.Append("ProcessUserTimeLimitInMilliseconds:").Append(ProcessUserTimeLimitInMilliseconds).Append(',');
        buffer.Append("JobUserTimeLimitInMilliseconds:").Append(JobUserTimeLimitInMilliseconds).Append(',');
        buffer.Append("JobClockTimeLimitInMilliseconds:").Append(JobClockTimeLimitInMilliseconds).Append(',');
        buffer.Append("PropagateOnChildProcesses:").Append(PropagateOnChildProcesses).Append(',');
        buffer.Append("ActiveProcessLimit:").Append(ActiveProcessLimit).Append(',');
        buffer.Append("PriorityClass:").Append(PriorityClass).Append(',');
        buffer.Append("RunMode:").Append(RunMode.ToString()).Append(',');
        buffer.Append("Privileges:[").Append(string.Join(',', Privileges)).Append(']').Append(',');
        buffer.Append("Environment:[").Append(
            string.Join(',', Environment.Select(env => $"{env.Key}={env.Value}"))).Append(']').Append('}').Append(',');
        buffer.Append("PowerThrottling:").Append(PowerThrottling).Append(',');
        return buffer.ToString();
    }

    public string ToShortString(string separator = ", ")
    {
        List<string> settingsApplied = [];

        if (MaxProcessMemory > 0 || MaxJobMemory > 0 || MaxWorkingSetSize > 0)
        {
            settingsApplied.Add("memory");
        }
        if (CpuAffinity.Length > 0 || CpuMaxRate > 0 || ProcessUserTimeLimitInMilliseconds > 0 ||
            JobUserTimeLimitInMilliseconds > 0)
        {
            settingsApplied.Add("cpu");
        }
        if (MaxBandwidth > 0)
        {
            settingsApplied.Add("network");
        }
        if (JobClockTimeLimitInMilliseconds > 0)
        {
            settingsApplied.Add("time");
        }
        if (PropagateOnChildProcesses)
        {
            settingsApplied.Add("propagate");
        }
        if (ActiveProcessLimit > 0)
        {
            settingsApplied.Add("max_procs");
        }
        if (PriorityClass != PriorityClass.Undefined)
        {
            settingsApplied.Add("prio");
        }
        if (Privileges.Length > 0)
        {
            settingsApplied.Add("priv");
        }
        if (Environment.Count > 0)
        {
            settingsApplied.Add("env");
        }
        if (PowerThrottling != PowerThrottling.Undefined)
        {
            settingsApplied.Add("pwr_thr");
        }

        return settingsApplied.Count > 0 ? string.Join(separator, settingsApplied) : "no limits";
    }

    public bool IsEmpty() => this == Empty;

    public static implicit operator Win32ProcessSettings(JobSettings settings) =>
        new(settings.Privileges, settings.Environment, settings.PowerThrottling);

    public static implicit operator Win32JobSettings(JobSettings settings) => new(
        settings.MaxProcessMemory, settings.MaxJobMemory, settings.MaxWorkingSetSize, settings.MinWorkingSetSize,
        settings.CpuAffinity, settings.CpuMaxRate, settings.MaxBandwidth, settings.ProcessUserTimeLimitInMilliseconds,
        settings.JobUserTimeLimitInMilliseconds, settings.PropagateOnChildProcesses, settings.ActiveProcessLimit,
        settings.PriorityClass
    );
}

[DerivedTypeShape(typeof(RunInNamedJob), Tag = 0)]
[DerivedTypeShape(typeof(RunInAnonymousIsolatedJob), Tag = 1)]
[DerivedTypeShape(typeof(RunInAnonymousSharedJob), Tag = 2)]
public interface IRunMode { }

public record RunInNamedJob(string JobName) : IRunMode { }

public record RunInAnonymousSharedJob() : IRunMode { }

public record RunInAnonymousIsolatedJob() : IRunMode { }

public static class RunModes
{
    private readonly static IRunMode shared = new RunInAnonymousSharedJob();
    private readonly static IRunMode isolated = new RunInAnonymousIsolatedJob();

    public static IRunMode Default => shared;

    public static IRunMode SharedJob => shared;

    public static IRunMode IsolatedJob => isolated;

    public static IRunMode NamedJob(string jobName) => new RunInNamedJob(jobName);

    public static bool IsEqualTo(this IRunMode runMode, IRunMode otherRunMode) =>
        (runMode is RunInAnonymousIsolatedJob && otherRunMode is RunInAnonymousSharedJob) ||
        (runMode is RunInAnonymousSharedJob && otherRunMode is RunInAnonymousSharedJob) ||
        (runMode is RunInNamedJob { JobName: var jobName } &&
            otherRunMode is RunInNamedJob { JobName: var otherJobName } && jobName == otherJobName);
}

public sealed record GroupAffinity(
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

public enum PowerThrottling { Undefined = 0, Auto, On, Off }

public sealed class MessagePackToJsonConverter<T> : JsonConverter<T> where T : IShapeable<T>
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MsgPackSerializer.Deserialize<T>(reader.GetBytesFromBase64());

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteBase64StringValue(MsgPackSerializer.Serialize(value));
}

public sealed class ConfigJobIdConverter : JsonConverter<ConfigJobId>
{
    public override ConfigJobId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new ConfigJobId(reader.GetString() ?? throw new JsonException("Invalid key"));
    }

    public override void Write(Utf8JsonWriter writer, ConfigJobId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Id);
    }

    public override ConfigJobId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new ConfigJobId(reader.GetString() ?? throw new JsonException("Invalid key"));
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, ConfigJobId value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.Id);
    }
}
