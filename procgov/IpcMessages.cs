using MessagePack;

namespace ProcessGovernor;

/* ***** Requests ***** */

[Union(0, typeof(MonitorJob))]
[Union(1, typeof(IsProcessGoverned))]
[Union(2, typeof(SubscribeToNotifications))]
public interface IMonitorRequest { }

[MessagePackObject]
public record MonitorJob([property: Key(0)] string JobName) : IMonitorRequest;

[MessagePackObject]
public record SubscribeToNotifications([property: Key(0)] string JobName) : IMonitorRequest;

[MessagePackObject]
public record IsProcessGoverned([property: Key(0)] int ProcessId) : IMonitorRequest;

/* ***** Responses ***** */

[Union(0, typeof(JobMonitored))]
[Union(1, typeof(ProcessStatus))]
public interface IMonitorResponse { }

[MessagePackObject]
public record JobMonitored([property: Key(0)] int HResult) : IMonitorResponse;

[MessagePackObject]
public record ProcessStatus([property: Key(0)] string JobName) : IMonitorResponse;

/* ***** Notifications ***** */



