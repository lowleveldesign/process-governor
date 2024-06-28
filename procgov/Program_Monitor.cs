using MessagePack;
using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

static partial class Program
{
    // main pipe used to receive commands from the client and respond to them
    public const string PipeName = "procgov";

    public static async Task<int> Execute(RunAsMonitor _, CancellationToken ct)
    {
        try
        {
            await StartMonitor(ct);
            return 0;
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : -1;
        }
    }

    static async Task StartMonitor(CancellationToken ct)
    {
        // FIXME: maybe this linked ct is not really needed and we may use the main one?
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var monitoredJobs = new MonitoredJobs();
        using var notifier = new Notifier();
        var processes = new GovernedProcesses();

        using var iocpHandle = CheckWin32Result(PInvoke.CreateIoCompletionPort(new SafeFileHandle(HANDLE.INVALID_HANDLE_VALUE, true),
                                                    new SafeFileHandle(nint.Zero, true), nuint.Zero, 0));

        // FIXME: iocpListener must be started as an async 
        var iocpListener = Task.Factory.StartNew(IOCPListener, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // FIXME: we should stop if there are no more jobs to monitor in the last,for example, 10 min.
        while (!cts.IsCancellationRequested)
        {
            // FIXME: set pipe security = current user + admins
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.WriteThrough | PipeOptions.Asynchronous);

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
                var writeBuffer = new ArrayBufferWriter<byte>(128);

                while (!cts.IsCancellationRequested && pipe.IsConnected &&
                        await pipe.ReadAsync(readBuffer.GetMemory(), cts.Token) is var bytesRead && bytesRead > 0)
                {
                    readBuffer.Advance(bytesRead);

                    var processedBytes = 0;
                    while (processedBytes < readBuffer.WrittenCount)
                    {
                        var msg = MessagePackSerializer.Deserialize<IMonitorRequest>(readBuffer.WrittenMemory[processedBytes..],
                                        bytesRead: out var deserializedBytes, cancellationToken: cts.Token);
                        switch (msg)
                        {
                            case MonitorJob monitorJob:
                                var job = Win32JobModule.OpenJob(monitorJob.JobName);
                                Win32JobModule.AssignIOCompletionPort(job, iocpHandle);
                                monitoredJobs.AddJob(job);
                                if (monitorJob.SubscribeToEvents)
                                {
                                    notifier.AddNotificationStream(job.NativeHandle, pipe);
                                    disposePipeOnExit = false;
                                }
                                else
                                {
                                    pipe.Disconnect();
                                }
                                break;
                            case IsProcessGoverned { ProcessId: var processId }:
                                var jobName = processes.TryGetJobAssignedToProcess(processId, out var jobHandle) &&
                                                monitoredJobs.TryGetJob(jobHandle, out job) ? job.Name : "";
                                MessagePackSerializer.Serialize<IMonitorResponse>(writeBuffer,
                                    new ProcessStatus(jobName), cancellationToken: cts.Token);
                                await pipe.WriteAsync(writeBuffer.WrittenMemory, cts.Token);
                                writeBuffer.ResetWrittenCount();
                                break;
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

            // terminate the task if there are no jobs monitored
            while (!cts.IsCancellationRequested)
            {
                unsafe
                {
                    // FIXME: do an async wait here - implement awaitable
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

                    if (!monitoredJobs.TryGetJob((nint)jobHandle, out var job))
                    {
                        Logger.TraceEvent(TraceEventType.Warning, 0, $"[process monitor] job not found: {jobHandle}");
                        continue;
                    }

                    IMonitorResponse jobEvent = msgIdentifier switch
                    {
                        PInvoke.JOB_OBJECT_MSG_NEW_PROCESS => new NewProcessEvent(job.Name, (uint)msgData),
                        PInvoke.JOB_OBJECT_MSG_EXIT_PROCESS => new ExitProcessEvent(job.Name, (uint)msgData, false),
                        PInvoke.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS => new ExitProcessEvent(job.Name, (uint)msgData, true),
                        PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO => new NoProcessesInJobEvent(job.Name),
                        PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_LIMIT => new JobLimitExceededEvent(job.Name, LimitType.ActiveProcessNumber),
                        PInvoke.JOB_OBJECT_MSG_JOB_MEMORY_LIMIT => new JobLimitExceededEvent(job.Name, LimitType.Memory),
                        PInvoke.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT => new ProcessLimitExceededEvent(job.Name, (uint)msgData, LimitType.Memory),
                        PInvoke.JOB_OBJECT_MSG_END_OF_PROCESS_TIME => new ProcessLimitExceededEvent(job.Name, (uint)msgData, LimitType.CpuTime),
                        PInvoke.JOB_OBJECT_MSG_END_OF_JOB_TIME => new JobLimitExceededEvent(job.Name, LimitType.CpuTime),
                        _ => throw new NotImplementedException()
                    };

                    Logger.TraceInformation($"[process monitor] {jobEvent}");

                    if (notifier.GetNotificationStreams(job.NativeHandle) is var jobNotificationStreams && jobNotificationStreams.Length > 0)
                    {
                        MessagePackSerializer.Serialize(buffer, jobEvent, cancellationToken: cts.Token);
                        var sendTasks = jobNotificationStreams.Select(stream => stream.WriteAsync(buffer.WrittenMemory, cts.Token).AsTask()).ToArray();

                        int completedTasksCount = 0;
                        while (completedTasksCount < sendTasks.Length)
                        {
                            var taskIndex = Task.WaitAny(sendTasks, ct);
                            if (sendTasks[taskIndex].IsFaulted)
                            {
                                Logger.TraceEvent(TraceEventType.Warning, 0,
                                    $"[process monitor] failed to send data to the pipe: {sendTasks[taskIndex].Exception}");

                                var stream = jobNotificationStreams[taskIndex];
                                notifier.RemoveNotificationStream(job.NativeHandle, stream);
                                stream.Disconnect();
                                stream.Dispose();
                            }
                            completedTasksCount++;
                        }
                        buffer.ResetWrittenCount();
                    }

                    // FIXME: if job is not monitored anymore, remove it from the list
                    if (jobEvent is NoProcessesInJobEvent)
                    {
                        foreach (var s in notifier.GetNotificationStreams(job.NativeHandle))
                        {
                            s.Disconnect();
                            s.Dispose();
                        }
                        // FIXME: did we received all the process exit events?
                        if (monitoredJobs.RemoveJob(job) == 0)
                        {
                            cts.Cancel();
                        }
                    }
                }
            }
        }
    }

    private sealed class GovernedProcesses
    {
        readonly object lck = new();
        readonly Dictionary<uint, nint> processJobMap = [];

        public void JobAssignedToProcess(uint processId, nint jobHandle)
        {
            lock (lck)
            {
                processJobMap.Add(processId, jobHandle);
            }
        }

        public void ProcessExited(uint processId)
        {
            lock (lck)
            {
                processJobMap.Remove(processId);
            }
        }

        public bool TryGetJobAssignedToProcess(uint processId, out nint jobHandle)
        {
            lock (lck)
            {
                return processJobMap.TryGetValue(processId, out jobHandle);
            }
        }
    }

    private sealed class Notifier : IDisposable
    {
        readonly object lck = new();
        readonly Dictionary<nint, NamedPipeServerStream[]> notificationStreams = [];

        public void AddNotificationStream(nint jobHandle, NamedPipeServerStream stream)
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

        public void RemoveNotificationStream(nint jobHandle, NamedPipeServerStream stream)
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

        public NamedPipeServerStream[] GetNotificationStreams(nint jobHandle)
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

    private sealed class MonitoredJobs : IDisposable
    {
        readonly object lck = new();
        readonly Dictionary<nint, Win32Job> monitoredJobs = [];

        public int AddJob(Win32Job job)
        {
            lock (lck)
            {
                monitoredJobs.Add(job.NativeHandle, job);
                return monitoredJobs.Count;
            }
        }

        public int RemoveJob(Win32Job job)
        {
            lock (lck)
            {
                monitoredJobs.Remove(job.NativeHandle);
                return monitoredJobs.Count;
            }
        }

        public bool TryGetJob(nint jobHandle, [NotNullWhen(true)] out Win32Job? job)
        {
            lock (lck)
            {
                return monitoredJobs.TryGetValue(jobHandle, out job);
            }
        }
        public void Dispose()
        {
            lock (lck)
            {
                foreach (var job in monitoredJobs.Values)
                {
                    try { job.Dispose(); } catch { }
                }
                monitoredJobs.Clear();
            }
        }
    }
}
