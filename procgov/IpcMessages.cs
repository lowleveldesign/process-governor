using MessagePack;

namespace ProcessGovernor;

/* ***** Requests ***** */

[Union(0, typeof(MonitorJob))]
[Union(1, typeof(IsProcessGoverned))]
public interface IMonitorRequest { }

[MessagePackObject]
public record MonitorJob(
    [property: Key(0)] string JobName,
    [property: Key(1)] bool SubscribeToEvents
) : IMonitorRequest;

[MessagePackObject]
public record IsProcessGoverned([property: Key(0)] uint ProcessId) : IMonitorRequest;

/* ***** Responses ***** */

[Union(0, typeof(ProcessStatus))]
[Union(1, typeof(NewProcessEvent))]
[Union(2, typeof(ExitProcessEvent))]
[Union(3, typeof(JobLimitExceededEvent))]
[Union(4, typeof(ProcessLimitExceededEvent))]
[Union(5, typeof(NoProcessesInJobEvent))]
public interface IMonitorResponse { }

[MessagePackObject]
public record ProcessStatus([property: Key(0)] string JobName) : IMonitorResponse;

/* ***** Notifications ***** */

[MessagePackObject]
public record NewProcessEvent(
    [property: Key(0)] string JobName,
    [property: Key(1)] uint ProcessId
) : IMonitorResponse;

[MessagePackObject]
public record ExitProcessEvent(
    [property: Key(0)] string JobName,
    [property: Key(1)] uint ProcessId,
    [property: Key(2)] bool AbnormalExit
) : IMonitorResponse;

public enum LimitType
{
    Memory = 0,
    CpuTime = 1,
    ActiveProcessNumber = 2
};

[MessagePackObject]
public record JobLimitExceededEvent(
    [property: Key(0)] string JobName,
    [property: Key(1)] LimitType ExceededLimit
) : IMonitorResponse;

[MessagePackObject]
public record ProcessLimitExceededEvent(
    [property: Key(0)] string JobName,
    [property: Key(1)] uint ProcessId,
    [property: Key(2)] LimitType ExceededLimit
) : IMonitorResponse;

[MessagePackObject]
public record NoProcessesInJobEvent(
    [property: Key(0)] string JobName
) : IMonitorResponse;

