using MessagePack;

namespace ProcessGovernor;

/* ***** Requests ***** */

[Union(0, typeof(MonitorJobReq))]
[Union(1, typeof(GetJobNameReq))]
[Union(2, typeof(GetJobSettingsReq))]
public interface IMonitorRequest { }

[MessagePackObject]
public record MonitorJobReq(
    [property: Key(0)] string JobName,
    [property: Key(1)] bool SubscribeToEvents,
    [property: Key(2)] JobSettings JobSettings
) : IMonitorRequest;

[MessagePackObject]
public record GetJobNameReq([property: Key(0)] uint ProcessId) : IMonitorRequest;

[MessagePackObject]
public record GetJobSettingsReq([property: Key(0)] string JobName) : IMonitorRequest;

/* ***** Responses ***** */

[Union(0, typeof(MonitorJobResp))]
[Union(1, typeof(GetJobNameResp))]
[Union(2, typeof(GetJobSettingsResp))]
[Union(3, typeof(NewProcessEvent))]
[Union(4, typeof(ExitProcessEvent))]
[Union(5, typeof(JobLimitExceededEvent))]
[Union(6, typeof(ProcessLimitExceededEvent))]
[Union(7, typeof(NoProcessesInJobEvent))]
public interface IMonitorResponse { }

// JobName is an empty string is there is no job associated with the process
[MessagePackObject]
public record GetJobNameResp([property: Key(0)] string JobName) : IMonitorResponse;

[MessagePackObject]
public record GetJobSettingsResp([property: Key(0)] JobSettings JobSettings) : IMonitorResponse;

[MessagePackObject]
public record MonitorJobResp([property: Key(0)] string JobName) : IMonitorResponse;

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

