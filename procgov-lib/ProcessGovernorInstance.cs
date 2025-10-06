using ProcessGovernor.Library.Win32;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Win32.System.Threading;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

using ProcessJob = (ProcessGovernor.Library.RunningJobId Id, ProcessGovernor.Library.JobSettings Settings);
using MonitoredJob = (System.Runtime.InteropServices.SafeHandle JobHandle, ProcessGovernor.Library.JobSettings Settings);
using Microsoft.Win32.SafeHandles;

namespace ProcessGovernor.Library;

public delegate NamedPipeClientStream? CreateMonitorStream(CancellationToken ct);

public sealed class ProcessGovernorInstance : IDisposable
{
    private static readonly TraceSource Logger = ProcessGovernorLibraryApi.Logger;

    private readonly CancellationTokenSource mainCts;

    // a priority queue used for enforcing the clock time limits
    private readonly SynchronizedPriorityQueue<RunningJobId, DateTime> jobsClockTimeQueue = new();
    // currently monitored jobs and processes
    private readonly ConcurrentDictionary<RunningJobId, MonitoredJob> monitoredJobs = [];
    private readonly ConcurrentDictionary<uint, RunningJobId> governedProcesses = [];
    // IOCP port used for getting notifications from monitored jobs
    private readonly Win32JobIoCompletionPort jobIocp = new();

    // used by new process monitoring
    private Dictionary<uint, Win32ProcessMetadata> cachedRunningProcesses = [];
    // the frequency of searching for new processes in the system (0 - paused)
    private volatile int processMonitorIntervalMs;
    // the main monitor task
    private readonly Task mainMonitorTask;

    private volatile AutoAssignSettings monitorSettings = new([], [], []);

    // variables to access the external monitor used for named jobs
    private readonly CreateMonitorStream? createMonitorStream;
    private volatile ProcessGovernorMonitorPipeClient? monitorPipeClient = null;
    private NamedPipeClientStream? monitorClientStream = null;

    public ProcessGovernorInstance(ChannelWriter<IMonitorNotification> notificationSink,
        CreateMonitorStream? createMonitorStream = null, int processMonitorIntervalMs = 1000,
        CancellationToken ct = default)
    {
        mainCts = ct != default ? CancellationTokenSource.CreateLinkedTokenSource(ct) : new();
        this.processMonitorIntervalMs = processMonitorIntervalMs;
        this.createMonitorStream = createMonitorStream;

        mainMonitorTask = RunBackgroundTask(Run, mainCts.Token);

        // Helpers (ProcessGovernor)

        async Task Run(CancellationToken ct)
        {
            Dictionary<RunningJobId, List<uint>> jobProcesses = [];
            var lastCheckTimeUtc = DateTime.UtcNow;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // check if there are any job that passed their clock time limit
                    while (!ct.IsCancellationRequested && jobsClockTimeQueue.TryDequeue(DateTime.UtcNow, out var jobId))
                    {
                        if (monitoredJobs.TryGetValue(jobId, out var jobData))
                        {
                            await notificationSink.WriteAsync(new JobLimitExceededEvent(jobId, LimitType.ClockTime), ct);
                            try
                            {
                                // this triggers NoProcessesInJobEvent
                                Win32JobModule.TerminateJob(jobData.JobHandle, uint.MaxValue);
                            }
                            catch (Exception ex)
                            {
                                Logger.TraceError("[job_monitor] Error occurred when trying to terminate a job '{0}': {1}", jobId, ex);
                            }
                        }
                    }

                    // the main monitor loop looking for new processes to be governed
                    if (processMonitorIntervalMs > 0 &&
                        DateTime.UtcNow.Subtract(lastCheckTimeUtc).TotalMilliseconds >= processMonitorIntervalMs)
                    {
                        foreach (var (pid, job) in GetNewProcessesToMonitor(Win32ProcessModule.GetRunningProcesses()))
                        {
                            if (governedProcesses.ContainsKey(pid))
                            {
                                continue;
                            }

                            try
                            {
                                var jobHandle = GetOrCreateJobObjectInternal(job.Id, job.Settings, out bool isNew);
                                if (isNew)
                                {
                                    Logger.TraceVerbose("[job_monitor] New job {0} created.", job.Id);
                                    await notificationSink.WriteAsync(new NewJobEvent(job.Id, job.Settings), ct);
                                }

                                Logger.TraceVerbose("[job_monitor] Assigning process {0} to job {1}.", pid, job.Id);
                                Win32ProcessModule.AssignExistingProcessToJob(pid, job.Settings, jobHandle);
                            }
                            catch (Exception ex)
                            {
                                Logger.TraceError("[job_monitor] Assigning process {0} to job {1}) failed with error {3}",
                                    pid, job.Id, ex);
                            }
                        }

                        lastCheckTimeUtc = DateTime.UtcNow;
                    }

                    // receives, processes and sends job notifications
                    while (!ct.IsCancellationRequested && (jobIocp.TryRead(out var notification) ||
                        (monitorPipeClient?.NotificationsReader.TryRead(out notification) ?? false)))
                    {
                        switch (notification)
                        {
                            case NewProcessEvent n:
                                OnNewProcessEvent(n.JobId, n.ProcessId);
                                break;
                            case ExitProcessEvent n:
                                OnExitProcessEvent(n.JobId, n.ProcessId);
                                break;
                            case NoProcessesInJobEvent n:
                                OnEmptyJobEvent(n.JobId);
                                break;
                            default:
                                break;
                        }
                        notificationSink.TryWrite(notification);
                    }

                    if (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(500, ct);
                    }
                }
            }
            catch (Exception ex) when (ex.IsCancelledException()) { }

            // Helpers

            void OnNewProcessEvent(RunningJobId jobId, uint processId)
            {
                Logger.TraceVerbose("[job_monitor] New process {0} in job '{1}'.", processId, jobId);
                governedProcesses[processId] = jobId;
                if (jobProcesses.TryGetValue(jobId, out var processes))
                {
                    processes.Add(processId);
                }
                else { jobProcesses.Add(jobId, [processId]); }

                if (monitoredJobs.TryGetValue(jobId, out var monitoredJob))
                {
                    try
                    {
                        Win32ProcessModule.UpdateUninheritedProcessSettings(processId, monitoredJob.Settings);
                    }
                    catch (Exception ex)
                    {
                        Logger.TraceError("[job_monitor] Error when updating settings of a new process {0} in job '{1}': {2}",
                            processId, jobId, ex);
                    }
                }
            }

            void OnExitProcessEvent(RunningJobId jobId, uint processId)
            {
                Logger.TraceVerbose("[job_monitor] Process {0} exited in job '{1}'.", processId, jobId);
                governedProcesses.TryRemove(processId, out _);
                if (jobProcesses.TryGetValue(jobId, out var processes))
                {
                    processes.Remove(processId);
                }
            }

            void OnEmptyJobEvent(RunningJobId jobId)
            {
                Logger.TraceVerbose("[job_monitor] No processes in the job '{0}'", jobId);

                // when using TerminateJob we might not receive the ExitProcess events
                // so there might be some processes to remove
                if (jobProcesses.TryGetValue(jobId, out var processes))
                {
                    foreach (var processId in processes)
                    {
                        governedProcesses.TryRemove(processId, out _);
                    }
                }

                if (monitoredJobs.TryRemove(jobId, out var jobData))
                {
                    // we are sure that this job is monitored by the local IOCP
                    jobIocp.UnassignJob(jobData.JobHandle);
                    jobData.JobHandle.Dispose();

                    Debug.Assert(!governedProcesses.Any(kv => kv.Value == jobId));
                }
            }
        }
    }

    public int ProcessMonitorIntervalMilliseconds
    {
        get => processMonitorIntervalMs;
        set => processMonitorIntervalMs = value;
    }

    public ImmutableDictionary<ConfigJobId, JobSettings> AutoAssignJobSettings
    {
        get => monitorSettings.JobSettings;
        set => monitorSettings = monitorSettings with { JobSettings = value };
    }

    public ImmutableDictionary<string, ConfigJobId> AutoAssignProcessSettings
    {
        get => monitorSettings.ProcessSettings;
        set
        {
            if (!ReferenceEquals(monitorSettings.ProcessSettings, value))
            {
                monitorSettings = monitorSettings with
                {
                    ProcessSettings = value,
                    MonitoredProcessNames = [.. value.Keys.Select(s => Path.GetFileName(s)!)]
                };
                // we also need to clear the cache
                Interlocked.Exchange(ref cachedRunningProcesses, []);
            }
        }
    }

    public SafeHandleReference GetOrCreateJobObject(RunningJobId jobId, JobSettings jobSettings, out bool wasCreated) =>
        new(GetOrCreateJobObjectInternal(jobId, jobSettings, out wasCreated));

    private SafeHandle GetOrCreateJobObjectInternal(RunningJobId jobId, JobSettings jobSettings, out bool wasCreated)
    {
        bool isNew = false;
        var (jobHandle, _) = monitoredJobs.GetOrAdd(jobId, (jobId) =>
        {
            var jobHandle = jobId.GetJobName() is var jobName && jobName is not null &&
                Win32JobModule.OpenJobHandle(jobName) is { } h && !h.IsInvalid ?
                h : Win32JobModule.CreateJob(jobName);

            if (!jobId.IsNamedJob() || !TryUsingExternalMonitor(jobId))
            {
                jobIocp.AssignJob(jobHandle, jobId);
            }

            if (jobSettings.JobClockTimeLimitInMilliseconds > 0)
            {
                jobsClockTimeQueue.Enqueue(jobId, DateTime.UtcNow.AddMilliseconds(
                    jobSettings.JobClockTimeLimitInMilliseconds));
            }

            Win32JobModule.SetLimits(jobHandle, jobSettings);

            isNew = true;
            return new(jobHandle, jobSettings);
        });

        wasCreated = isNew;
        return jobHandle;

        // Helpers

        bool TryUsingExternalMonitor(RunningJobId jobId)
        {
            if (createMonitorStream is null)
            {
                return false;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);
                cts.CancelAfter(2000);

                if (monitorClientStream?.IsConnected is not true)
                {
                    // we need to recreate the stream and the monitor client
                    monitorClientStream = createMonitorStream(cts.Token) is { } pipe ? pipe : null;
                    monitorPipeClient = monitorClientStream is not null ? new(monitorClientStream, mainCts.Token) : null;
                }

                if (monitorPipeClient is not null)
                {
                    IMonitorRequest[] requests = [new MonitorJobReq(jobId), new SubscribeToNotificationsReq(jobId)];
                    foreach (var request in requests)
                    {
                        var sendTask = monitorPipeClient.SendRequest(request).ConfigureAwait(false);
                        // ugly, I know, but it have to be synchronous
                        if (sendTask.GetAwaiter().GetResult() is not AckResp { IsSuccess: true })
                        {
                            Logger.TraceWarning("[job_monitor] Request to external monitor failed for job '{0}'", jobId);
                            return false;
                        }
                    }
                    Logger.TraceVerbose("[job_monitor] Subscribed to the external monitor for job '{0}'", jobId);
                    return true;
                }
                else { return false; }
            }
            catch (Exception ex)
            {
                Logger.TraceError("[job_monitor] Error when starting external monitoring for job '{0}': {1}", jobId, ex);
                return false;
            }
        }
    }

    public bool TryGetJob(RunningJobId jobId, [MaybeNullWhen(false)] out SafeHandleReference jobHandle,
        [MaybeNullWhen(false)] out JobSettings jobSettings)
    {
        if (monitoredJobs.TryGetValue(jobId, out var jobData))
        {
            jobHandle = new(jobData.JobHandle);
            jobSettings = jobData.Settings;
            return true;
        }
        jobHandle = null;
        jobSettings = null;
        return false;
    }

    public bool IsProcessGoverned(uint pid) => governedProcesses.ContainsKey(pid);

    public Win32Process StartProcessInJob(string arguments, ProcessCreationFlags flags, RunningJobId jobId)
    {
        if (!monitoredJobs.TryGetValue(jobId, out var jobData))
        {
            Logger.TraceError("[job_monitor] Job {0} could not be found for process: {1}", jobId, arguments);
            throw new ArgumentException($"Invalid job ID: {jobId}");
        }

        var p = Win32ProcessModule.StartProcessInJob(arguments, flags, jobData.Settings, jobData.JobHandle);
        // there is a minimal chance of a race condition here if the monitor discovers this process
        // before we add it to the governed processes
        governedProcesses.TryAdd(p.Id, jobId);
        return p;
    }

    public Win32Process StartProcessInJobWithToken(string arguments, ProcessCreationFlags flags,
        SafeHandle tokenHandle, RunningJobId jobId)
    {
        if (!monitoredJobs.TryGetValue(jobId, out var jobData))
        {
            Logger.TraceError("[job_monitor] Job {0} could not be found for process: {1}", jobId, arguments);
            throw new ArgumentException($"Invalid job ID: {jobId}");
        }

        var p = Win32ProcessModule.StartProcessInJobWithToken(arguments, flags, tokenHandle,
            jobData.Settings, jobData.JobHandle);
        // there is a minimal chance of a race condition here if the monitor discovers this process
        // before we add it to the governed processes
        governedProcesses.TryAdd(p.Id, jobId);
        return p;
    }

    public void AssignExistingProcessToJob(uint processId, RunningJobId jobId)
    {
        if (monitoredJobs.TryGetValue(jobId, out var jobData))
        {
            if (governedProcesses.TryAdd(processId, jobId))
            {
                Win32ProcessModule.AssignExistingProcessToJob(processId, jobData.Settings, jobData.JobHandle);
            }
            else
            {
                Logger.TraceInformation("[job_monitor] Process {0} was not assigned to job {1} because it belongs to another job",
                    processId, jobId);
            }
        }
        else
        {
            Logger.TraceError("[job_monitor] Job {0} could not be found for process: {1}", jobId, processId);
            throw new ArgumentException($"Invalid job ID: {jobId}");
        }
    }

    internal Dictionary<uint, ProcessJob> GetNewProcessesToMonitor(Dictionary<uint, Win32ProcessMetadata> runningProcesses)
    {
        var monitorSettings = this.monitorSettings;
        var prevRunningProcesses = Interlocked.Exchange(ref cachedRunningProcesses, runningProcesses);

        var newProcesses = runningProcesses.Where(kv => !prevRunningProcesses.ContainsKey(kv.Key));

        Dictionary<uint, ProcessJob> newProcessesToMonitor = [];
        HashSet<uint> processesToSkip = [];

        foreach (var (processId, processMetadata) in newProcesses)
        {
            ResolveProcessSettings(processId, processMetadata);
        }

        return newProcessesToMonitor;

        // Helpers

        ProcessJob? ResolveProcessSettings(uint processId, Win32ProcessMetadata processMetadata, HashSet<uint>? visited = null)
        {
            visited ??= [];

            var parentId = processMetadata.ParentId;

            if (!visited.Add(processId))
            {
                // cycle detected - PIDs were reused
                return null;
            }
            else if (processesToSkip.Contains(processId))
            {
                return null;
            }
            else if (newProcessesToMonitor.TryGetValue(processId, out var g))
            {
                // this process is already added to the list of processes
                return g;
            }
            else if (governedProcesses.TryGetValue(processId, out var runningJobId) &&
                monitoredJobs.TryGetValue(runningJobId, out var runningJobData))
            {
                // we are already monitoring this process
                return new(runningJobId, runningJobData.Settings);
            }
            else if (parentId > 0 && runningProcesses.TryGetValue(parentId, out var parentMetadata) &&
                ResolveProcessSettings(parentId, parentMetadata, visited) is { } parentSettings &&
                    parentSettings.Settings.PropagateOnChildProcesses)
            {
                // parent has a job assigned and it has a propagate flag set
                newProcessesToMonitor[processId] = parentSettings;

                return parentSettings;
            }
            else if (ReadProcessJobId(monitorSettings, processId, processMetadata.ProcessName) is { } configJobId &&
                configJobId is not ConfigJobId("") && monitorSettings.JobSettings.TryGetValue(configJobId, out var configSettings))
            {
                var pg = new ProcessJob(new(configSettings.RunMode, configJobId), configSettings);

                newProcessesToMonitor[processId] = pg;

                return pg;
            }
            else
            {
                processesToSkip.Add(processId);
                return null;
            }

            // Helpers

            static ConfigJobId ReadProcessJobId(AutoAssignSettings settings, uint processId, string processName)
            {
                if (settings.MonitoredProcessNames.Contains(processName))
                {
                    bool shouldBeMonitored = settings.ProcessSettings.TryGetValue(processName, out var ps) ||
                        (GetFullProcessPath(processId, processName) is { } path && settings.ProcessSettings.TryGetValue(path, out ps));

                    return shouldBeMonitored ? ps : new ConfigJobId("");
                }
                else
                {
                    return new ConfigJobId("");
                }
            }

            static string? GetFullProcessPath(uint processId, string processName)
            {
                using var h = Win32ProcessModule.OpenProcessHandle(processId,
                    (uint)PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
                try
                {
                    // We make this additional check to minimize the chances of picking a wrong process (our
                    // original process might have terminated).
                    return Win32ProcessModule.GetProcessImageNameWin32(h) is { } path &&
                        processName.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) ? path : null;
                }
                catch (Win32Exception)
                {
                    return null;
                }
            }
        }
    }

    public void ForceStop()
    {
        mainCts.Cancel();
    }

    public async Task<bool> WaitForStop(TimeSpan timeout = default)
    {
        try
        {
            if (timeout != default)
            {
                await mainMonitorTask.WaitAsync(timeout);
            }
            else
            {
                await mainMonitorTask;
            }
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        monitorClientStream?.Dispose();

        SafeHandle[] jobHandles = [.. monitoredJobs.Values.Select(v => v.JobHandle)];
        monitoredJobs.Clear();

        foreach (var jobHandle in jobHandles)
        {
            jobHandle.Dispose();
        }

        jobIocp.Dispose();

        mainCts.Dispose();
    }

    // Helper classes (ProcessesAndJobMonitor)

    private sealed record AutoAssignSettings(
        ImmutableDictionary<ConfigJobId, JobSettings> JobSettings,
        // the key could be a process name or full executable path
        ImmutableDictionary<string, ConfigJobId> ProcessSettings,
        ImmutableHashSet<string> MonitoredProcessNames
    );
}
