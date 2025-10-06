using PolyType;

namespace ProcessGovernor.Library;

public record struct RunningJobId(string Id)
{
    public RunningJobId() : this("") { }

    public RunningJobId(IRunMode runMode, ConfigJobId configId) : this(
        runMode switch
        {
            RunInAnonymousIsolatedJob => $"a:{Guid.NewGuid()}:{configId}",
            RunInAnonymousSharedJob => $"s:{configId}:{configId}",
            RunInNamedJob n => $"n:{n.JobName}:{configId}",
            _ => throw new InvalidOperationException($"Unknown run mode: {runMode}")
        })
    { }

    public override readonly string ToString() => Id.ToString();

    public readonly bool IsInvalid() => Id is null or "";

    public readonly bool IsNamedJob() => Id.StartsWith("n:");

    public readonly string? GetJobName() =>
        IsNamedJob() && Id.Split(':') is [_, var jobName, _] ? jobName : null;

    public readonly ConfigJobId GetConfigId() => Id.Split(':') is [_, _, var configId] ? new(configId) : new("");
}

/* ***** Requests ***** */

[DerivedTypeShape(typeof(MonitorJobReq))]
[DerivedTypeShape(typeof(GetJobIdReq))]
[DerivedTypeShape(typeof(SubscribeToNotificationsReq))]
[GenerateShape]
public partial interface IMonitorRequest { }

[GenerateShape]
public sealed partial record MonitorJobReq(RunningJobId JobId) : IMonitorRequest;

[GenerateShape]
public sealed partial record GetJobIdReq(uint ProcessId) : IMonitorRequest;

[GenerateShape]
public sealed partial record SubscribeToNotificationsReq(RunningJobId JobId) : IMonitorRequest;

/* ***** Responses ***** */

[DerivedTypeShape(typeof(AckResp))]
[DerivedTypeShape(typeof(GetJobIdResp))]
[DerivedTypeShape(typeof(NewProcessEvent))]
[DerivedTypeShape(typeof(ExitProcessEvent))]
[DerivedTypeShape(typeof(JobLimitExceededEvent))]
[DerivedTypeShape(typeof(ProcessLimitExceededEvent))]
[DerivedTypeShape(typeof(NoProcessesInJobEvent))]
[GenerateShape]
public partial interface IMonitorResponse { }

[GenerateShape]
public sealed partial record AckResp(bool IsSuccess) : IMonitorResponse;

[GenerateShape]
public sealed partial record GetJobIdResp(RunningJobId JobId) : IMonitorResponse;

/* ***** Notifications ***** */

public interface IMonitorNotification : IMonitorResponse
{
    public RunningJobId JobId { get; }
}


// not yet used by monitor (no MessagePack serialization required)
public sealed record NewJobEvent(RunningJobId JobId, JobSettings JobSettings) : IMonitorNotification;

[GenerateShape]
public sealed partial record NewProcessEvent(RunningJobId JobId, uint ProcessId) : IMonitorNotification;

[GenerateShape]
public sealed partial record ExitProcessEvent(RunningJobId JobId, uint ProcessId, bool AbnormalExit) : IMonitorNotification;

public enum LimitType { Memory = 0, CpuTime = 1, ActiveProcessNumber = 2, ClockTime = 3, };

[GenerateShape]
public sealed partial record JobLimitExceededEvent(RunningJobId JobId, LimitType ExceededLimit) : IMonitorNotification;

[GenerateShape]
public sealed partial record ProcessLimitExceededEvent(RunningJobId JobId, uint ProcessId, LimitType ExceededLimit) : IMonitorNotification;

[GenerateShape]
public sealed partial record NoProcessesInJobEvent(RunningJobId JobId) : IMonitorNotification;
