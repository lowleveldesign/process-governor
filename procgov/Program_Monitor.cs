using MessagePack;
using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;

using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

static partial class Program
{
    // main pipe used to receive commands from the client and respond to them
    public static readonly string PipeName = Environment.IsPrivilegedProcess ? "procgov-system" : "procgov-user";

    public static async Task<int> Execute(RunAsMonitor monitor, CancellationToken ct)
    {
        try
        {
            if (monitor.NoGui)
            {
                PInvoke.FreeConsole();
            }

            await StartMonitor(monitor.MaxMonitorIdleTime, ct);
            return 0;
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : -1;
        }
    }

    static async Task StartMonitor(TimeSpan maxMonitorIdleTime, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var monitoredJobs = new MonitoredJobs();
        using var notifier = new Notifier();

        using var iocpHandle = CheckWin32Result(PInvoke.CreateIoCompletionPort(new SafeFileHandle(HANDLE.INVALID_HANDLE_VALUE, true),
                                                    new SafeFileHandle(nint.Zero, true), nuint.Zero, 0));

        var iocpListener = Task.Factory.StartNew(IOCPListener, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        while (!cts.IsCancellationRequested)
        {
            var pipeSecurity = new PipeSecurity();
            // we always add current user (the one who started monitor)
            if (WindowsIdentity.GetCurrent().User is { } currentUser)
            {
                pipeSecurity.SetAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            // and administrators
            pipeSecurity.SetAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.WriteThrough | PipeOptions.Asynchronous, 0, 0, pipeSecurity);

            bool pipeForwarded = false;
            try
            {
                await pipe.WaitForConnectionAsync(cts.Token);

                _ = StartClientThread(pipe);

                pipeForwarded = true;
            }
            catch (IOException ex)
            {
                // IOException that is raised if the pipe is broken or disconnected.
                Logger.TraceEvent(TraceEventType.Verbose, 0, "[procgov monitor] broken named pipe: " + ex);
            }
            catch (Exception ex) when (ex is OperationCanceledException || (
                    ex is AggregateException && ex.InnerException is TaskCanceledException))
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0, "[procgov monitor] cancellation: " + ex);
            }
            finally
            {
                if (!pipeForwarded)
                {
                    pipe.Dispose();
                }
            }
        }

        async Task StartClientThread(NamedPipeServerStream pipe)
        {
            PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid);
            bool disposePipeOnExit = true;
            try
            {
                var readBuffer = new ArrayBufferWriter<byte>(1024);
                var writeBuffer = new ArrayBufferWriter<byte>(256);

                while (!cts.IsCancellationRequested && pipe.IsConnected &&
                        await pipe.ReadAsync(readBuffer.GetMemory(), cts.Token) is var bytesRead && bytesRead > 0)
                {
                    readBuffer.Advance(bytesRead);

                    var processedBytes = 0;
                    while (processedBytes < readBuffer.WrittenCount)
                    {
                        var msg = MessagePackSerializer.Deserialize<IMonitorRequest>(readBuffer.WrittenMemory[processedBytes..],
                                        bytesRead: out var deserializedBytes, cancellationToken: cts.Token);

                        writeBuffer.ResetWrittenCount();

                        switch (msg)
                        {
                            case GetJobNameReq { ProcessId: var processId }:
                                {
                                    var resp = new GetJobNameResp(monitoredJobs.TryGetJobAssignedToProcess(processId,
                                        out var jobData) ? jobData.Job.Name : "");
                                    MessagePackSerializer.Serialize<IMonitorResponse>(writeBuffer, resp, cancellationToken: cts.Token);
                                    await pipe.WriteAsync(writeBuffer.WrittenMemory, cts.Token);
                                    break;
                                }
                            case GetJobSettingsReq { JobName: var jobName }:
                                {
                                    var resp = new GetJobSettingsResp(monitoredJobs.TryGetJob(jobName, out var jobData) ?
                                        jobData.JobSettings : new());
                                    MessagePackSerializer.Serialize<IMonitorResponse>(writeBuffer, resp, cancellationToken: cts.Token);
                                    await pipe.WriteAsync(writeBuffer.WrittenMemory, cts.Token);
                                    break;
                                }
                            case MonitorJobReq monitorJob:
                                {
                                    if (!monitoredJobs.TryGetJob(monitorJob.JobName, out var jobData))
                                    {
                                        var job = Win32JobModule.OpenJob(monitorJob.JobName);
                                        jobData = new(job, monitorJob.JobSettings, 0);
                                        monitoredJobs.AddOrUpdateJob(jobData);
                                        Win32JobModule.AssignIOCompletionPort(job, iocpHandle);
                                    }
                                    else
                                    {
                                        monitoredJobs.AddOrUpdateJob(jobData with { JobSettings = monitorJob.JobSettings });
                                    }

                                    if (monitorJob.SubscribeToEvents)
                                    {
                                        notifier.AddNotificationStream(jobData.Job.NativeHandle, pipe);
                                        disposePipeOnExit = false;
                                    }

                                    var resp = new MonitorJobResp(monitorJob.JobName);
                                    MessagePackSerializer.Serialize<IMonitorResponse>(writeBuffer, resp, cancellationToken: cts.Token);
                                    await pipe.WriteAsync(writeBuffer.WrittenMemory, cts.Token);
                                    break;
                                }
                            default:
                                Debug.Assert(false);
                                break;
                        }
                        processedBytes += deserializedBytes;
                    }

                    readBuffer.ResetWrittenCount();
                }
            }
            catch (IOException ex)
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0,
                    $"[procgov monitor][{clientPid}] broken named pipe: {ex}");
            }
            catch (Exception ex) when (ex is OperationCanceledException || (
                    ex is AggregateException && ex.InnerException is TaskCanceledException))
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0,
                    $"[procgov monitor][{clientPid}] cancellation: {ex}");
            }
            catch (Exception ex)
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0,
                    $"[procgov monitor][{clientPid}] error: {ex}");
            }
            finally
            {
                if (disposePipeOnExit)
                {
                    pipe.Dispose();
                }
            }
        }

        void IOCPListener()
        {
            var buffer = new ArrayBufferWriter<byte>(1024);
            var lastTimeWithJobs = DateTime.UtcNow;

            while (!cts.IsCancellationRequested)
            {
                if (monitoredJobs.Count > 0)
                {
                    lastTimeWithJobs = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - lastTimeWithJobs) > maxMonitorIdleTime)
                {
                    Logger.TraceInformation($"[process monitor] stopping monitor (no more jobs)");
                    cts.Cancel();
                }

                unsafe
                {
                    if (!PInvoke.GetQueuedCompletionStatus(iocpHandle, out uint msgIdentifier,
                        out nuint jobHandle, out var msgData, 100 /* ms */))
                    {
                        var winerr = Marshal.GetLastWin32Error();
                        if (winerr == (int)WAIT_EVENT.WAIT_TIMEOUT)
                        {
                            // regular timeout
                            continue;
                        }

                        Logger.TraceEvent(TraceEventType.Error, 0, $"[process monitor] IOCP listener failed: {winerr:x}");
                        break;
                    }

                    if (!monitoredJobs.TryGetJob(jobHandle, out var jobData))
                    {
                        // this might be expected if we already processed ExitProcess events for all the processes in the job
                        Debug.Assert(notifier.GetNotificationStreams(jobHandle) is []);
                        continue;
                    }

                    var jobName = jobData.Job.Name;
                    IMonitorResponse jobEvent = msgIdentifier switch
                    {
                        PInvoke.JOB_OBJECT_MSG_NEW_PROCESS => new NewProcessEvent(jobName, (uint)msgData),
                        PInvoke.JOB_OBJECT_MSG_EXIT_PROCESS => new ExitProcessEvent(jobName, (uint)msgData, false),
                        PInvoke.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS => new ExitProcessEvent(jobName, (uint)msgData, true),
                        PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO => new NoProcessesInJobEvent(jobName),
                        PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_LIMIT => new JobLimitExceededEvent(jobName, LimitType.ActiveProcessNumber),
                        PInvoke.JOB_OBJECT_MSG_JOB_MEMORY_LIMIT => new JobLimitExceededEvent(jobName, LimitType.Memory),
                        PInvoke.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT => new ProcessLimitExceededEvent(jobName, (uint)msgData, LimitType.Memory),
                        PInvoke.JOB_OBJECT_MSG_END_OF_PROCESS_TIME => new ProcessLimitExceededEvent(jobName, (uint)msgData, LimitType.CpuTime),
                        PInvoke.JOB_OBJECT_MSG_END_OF_JOB_TIME => new JobLimitExceededEvent(jobName, LimitType.CpuTime),
                        _ => throw new NotImplementedException()
                    };

                    Logger.TraceInformation($"[process monitor] {jobEvent}");

                    uint processCountInJob = jobEvent switch
                    {
                        NewProcessEvent { ProcessId: var processId } => monitoredJobs.ProcessAssignedToJob(processId, jobHandle),
                        ExitProcessEvent { ProcessId: var processId } => monitoredJobs.ProcessExited(processId),
                        NoProcessesInJobEvent => monitoredJobs.NoProcessInJob(jobHandle),
                        _ => 1 // does not matter until it's greater than 0
                    };

                    if (notifier.GetNotificationStreams(jobHandle) is var jobNotificationStreams && jobNotificationStreams.Length > 0)
                    {
                        MessagePackSerializer.Serialize(buffer, jobEvent, cancellationToken: cts.Token);
                        if (jobEvent is not NoProcessesInJobEvent && processCountInJob == 0)
                        {
                            // if there are no more processes in the job, we will also send NoProcessInJobEvent event
                            Logger.TraceInformation($"[process monitor] no processes in a job '{jobName}'");
                            MessagePackSerializer.Serialize(buffer, (IMonitorResponse)new NoProcessesInJobEvent(jobName), cancellationToken: cts.Token);
                        }

                        var sendTasks = jobNotificationStreams.Select(stream =>
                            stream.SafePipeHandle.IsClosed ? Task.FromException(new IOException("pipe is closed")) :
                                stream.WriteAsync(buffer.WrittenMemory, cts.Token).AsTask()).ToArray();

                        int completedTasksCount = 0;
                        while (completedTasksCount < sendTasks.Length)
                        {
                            var taskIndex = Task.WaitAny(sendTasks, ct);
                            if (sendTasks[taskIndex].IsFaulted || processCountInJob == 0)
                            {
                                if (sendTasks[taskIndex].IsFaulted)
                                {
                                    Logger.TraceEvent(TraceEventType.Warning, 0,
                                        $"[process monitor] failed to send data to the pipe: {sendTasks[taskIndex].Exception}");
                                }

                                var stream = jobNotificationStreams[taskIndex];
                                notifier.RemoveNotificationStream(jobHandle, stream);
                                stream.Dispose();
                            }
                            completedTasksCount++;
                        }
                        buffer.ResetWrittenCount();
                    }
                }
            }
        }
    }

    private sealed class Notifier : IDisposable
    {
        readonly Lock lck = new();
        readonly Dictionary<nuint, NamedPipeServerStream[]> notificationStreams = [];

        public void AddNotificationStream(nuint jobHandle, NamedPipeServerStream stream)
        {
            lock (lck)
            {
                if (!notificationStreams.TryGetValue(jobHandle, out var streams))
                {
                    notificationStreams.Add(jobHandle, [stream]);
                }
                else
                {
                    notificationStreams[jobHandle] = [.. streams, stream];
                }
            }
        }

        public void RemoveNotificationStream(nuint jobHandle, NamedPipeServerStream stream)
        {
            lock (lck)
            {
                if (notificationStreams.TryGetValue(jobHandle, out var streams))
                {
                    var newStreams = streams.Where(s => s != stream).ToArray();
                    if (newStreams.Length > 0)
                    {

                        notificationStreams[jobHandle] = newStreams;
                    }
                    else
                    {
                        notificationStreams.Remove(jobHandle);
                    }
                }
            }
        }

        public NamedPipeServerStream[] GetNotificationStreams(nuint jobHandle)
        {
            lock (lck)
            {
                return notificationStreams.TryGetValue(jobHandle, out var streams) ? streams : [];
            }
        }

        public void Dispose()
        {
            lock (lck)
            {
                foreach (var s in notificationStreams.Values.SelectMany(s => s))
                {
                    try { s.Dispose(); } catch { }
                }
                notificationStreams.Clear();
            }
        }
    }

    sealed class MonitoredJobs : IDisposable
    {
        readonly Lock lck = new();
        readonly Dictionary<nuint, MonitoredJobData> monitoredJobs = [];
        readonly Dictionary<string, nuint> monitoredJobNames = [];
        readonly Dictionary<uint, nuint> processJobMap = [];

        public int AddOrUpdateJob(MonitoredJobData jobData)
        {
            lock (lck)
            {
                var job = jobData.Job;
                if (monitoredJobNames.TryGetValue(job.Name, out var jobHandle))
                {
                    Debug.Assert(job.NativeHandle == jobHandle);
                    Debug.Assert(monitoredJobs.ContainsKey(jobHandle));
                    monitoredJobs[jobHandle] = jobData;
                }
                else
                {
                    Debug.Assert(!monitoredJobs.ContainsKey(jobHandle));
                    monitoredJobs.Add(job.NativeHandle, jobData);
                    monitoredJobNames.Add(job.Name, job.NativeHandle);
                }
                return monitoredJobs.Count;
            }
        }

        public uint NoProcessInJob(nuint jobHandle)
        {
            lock (lck)
            {
                if (monitoredJobs.Remove(jobHandle, out var jobData))
                {
                    monitoredJobNames.Remove(jobData.Job.Name);

                    jobData.Job.Dispose();

                    if (jobData.ProcessCount > 0)
                    {
                        // should be very rare - only when we missed ProcessExit notifications
                        Logger.TraceEvent(TraceEventType.Warning, 0, $"[process monitor] removed job contained assigned processes");
                        foreach (var pid in processJobMap.Where(kv => kv.Value == jobHandle).Select(kv => kv.Key))
                        {
                            processJobMap.Remove(pid);
                        }
                    }
                }
                return 0;
            }
        }

        public uint ProcessAssignedToJob(uint processId, nuint jobHandle)
        {
            lock (lck)
            {
                processJobMap.Add(processId, jobHandle);

                Debug.Assert(monitoredJobs.ContainsKey(jobHandle));
                var jobData = monitoredJobs[jobHandle];
                var newProcessCount = jobData.ProcessCount + 1;
                monitoredJobs[jobHandle] = jobData with { ProcessCount = newProcessCount };
                return newProcessCount;
            }
        }

        public uint ProcessExited(uint processId)
        {
            lock (lck)
            {
                if (processJobMap.Remove(processId, out var jobHandle))
                {
                    Debug.Assert(monitoredJobs.ContainsKey(jobHandle));

                    if (monitoredJobs[jobHandle] is var jobData && jobData.ProcessCount <= 1)
                    {
                        monitoredJobs[jobHandle] = jobData with { ProcessCount = 0 };
                        return NoProcessInJob(jobHandle);
                    }

                    var newProcessCount = jobData.ProcessCount - 1;
                    monitoredJobs[jobHandle] = jobData with { ProcessCount = newProcessCount };
                    return newProcessCount;
                }
                return 0;
            }
        }

        public int Count
        {
            get
            {
                lock (lck) { return monitoredJobs.Count; }
            }
        }

        public bool TryGetJob(nuint jobHandle, [NotNullWhen(true)] out MonitoredJobData? job)
        {
            lock (lck)
            {
                return monitoredJobs.TryGetValue(jobHandle, out job);
            }
        }

        public bool TryGetJob(string jobName, [NotNullWhen(true)] out MonitoredJobData? job)
        {
            lock (lck)
            {
                job = null;
                return monitoredJobNames.TryGetValue(jobName, out var jobHandle) &&
                    monitoredJobs.TryGetValue(jobHandle, out job);
            }
        }

        public bool TryGetJobAssignedToProcess(uint processId, [NotNullWhen(true)] out MonitoredJobData? job)
        {
            lock (lck)
            {
                if (processJobMap.TryGetValue(processId, out var jobHandle))
                {
                    return monitoredJobs.TryGetValue(jobHandle, out job);
                }
                job = null;
                return false;
            }
        }

        public void Dispose()
        {
            lock (lck)
            {
                foreach (var jobData in monitoredJobs.Values)
                {
                    jobData.Job.Dispose();
                }
                monitoredJobs.Clear();
            }
        }
    }

    sealed record MonitoredJobData(Win32Job Job, JobSettings JobSettings, uint ProcessCount);
}
