using MessagePack;

namespace ProcessGovernor;

/* ***** Requests ***** */

[Union(0, typeof(CreateJobForProcess))]
public interface IMonitorRequest { }

[MessagePackObject]
public record CreateJobForProcess(
    [property: Key(0)] int ProcessId,
    [property: Key(1)] JobSettings JobSettings,
    [property: Key(2)] bool RequestNotifications
) : IMonitorRequest;

/* ***** Responses ***** */

[Union(0, typeof(JobAssigned))]
public interface IMonitorResponse { }

[MessagePackObject]
public record JobAssigned(
    [property: Key(0)] int JobId
) : IMonitorResponse;

