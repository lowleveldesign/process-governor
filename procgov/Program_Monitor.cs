using MessagePack;
using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;

using static ProcessGovernor.Win32.Helpers;

namespace ProcessGovernor;

static partial class Program
{
    private static readonly SecurityIdentifier UserIdentifier = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User!;
    internal static readonly string PipeName = Environment.IsPrivilegedProcess ?
        $"procgov-{UserIdentifier.Value}_elevated" : $"procgov-{UserIdentifier.Value}";

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
        const nuint AnyJobHandle = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        ConcurrentDictionary<nuint, MonitoredJobData> monitoredJobs = [];
        ConcurrentDictionary<string, Win32Job> jobNameWin32JobMap = [];
        ConcurrentDictionary<uint, nuint> processIdJobHandleMap = [];
        ConcurrentDictionary<nuint, NamedPipeServerStream[]> clients = [];

        using var iocpHandle = CheckWin32Result(PInvoke.CreateIoCompletionPort(new SafeFileHandle(HANDLE.INVALID_HANDLE_VALUE, true),
                                                    new SafeFileHandle(nint.Zero, true), nuint.Zero, 0));

        var iocpListener = Task.Factory.StartNew(IOCPListener, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Logger.TraceEvent(TraceEventType.Warning, 0,
                        $"[procgov monitor] Stopping monitor because IOCP listener faulted: {t.Exception}");
                    cts.Cancel();
                }
            }, cts.Token);

        while (!cts.IsCancellationRequested)
        {
            var pipeSecurity = new PipeSecurity();
            // we always add current user (the one who started monitor)
            pipeSecurity.SetAccessRule(new PipeAccessRule(UserIdentifier, PipeAccessRights.FullControl, AccessControlType.Allow));
            // and SYSTEM
            pipeSecurity.SetAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            // and administrators
            pipeSecurity.SetAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.WriteThrough | PipeOptions.Asynchronous, 0, 0, pipeSecurity);

            try
            {
                await pipe.WaitForConnectionAsync(cts.Token);

                _ = StartClientThread(pipe);

                pipe = null;
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
                pipe?.Dispose();
            }
        }


        async Task StartClientThread(NamedPipeServerStream pipe)
        {
            PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid);
            try
            {
                var readBuffer = new ArrayBufferWriter<byte>(1024);
                var writeBuffer = new ArrayBufferWriter<byte>(1024);

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
                                    var resp = new GetJobNameResp(
                                        processIdJobHandleMap.TryGetValue(processId, out var jobHandle) &&
                                        monitoredJobs.TryGetValue(jobHandle, out var jobData) ? jobData.Job.Name : "");
                                    MessagePackSerializer.Serialize<IMonitorResponse>(writeBuffer, resp, cancellationToken: cts.Token);
                                    await pipe.WriteAsync(writeBuffer.WrittenMemory, cts.Token);
                                    break;
                                }
                            case GetJobSettingsReq { JobName: var jobName }:
                                {
                                    var resp = jobNameWin32JobMap.TryGetValue(jobName, out var job) && monitoredJobs.TryGetValue(job.NativeHandle, out var jobData)
                                        ? new GetJobSettingsResp(jobName, jobData.JobSettings) : new GetJobSettingsResp("", new());
                                    MessagePackSerializer.Serialize<IMonitorResponse>(writeBuffer, resp, cancellationToken: cts.Token);
                                    await pipe.WriteAsync(writeBuffer.WrittenMemory, cts.Token);
                                    break;
                                }
                            case MonitorJobReq { JobName: var jobName, JobSettings: var jobSettings, SubscribeToEvents: var subscribe }:
                                {
                                    var job = jobNameWin32JobMap.AddOrUpdate(jobName,
                                        (jobName) => Win32JobModule.OpenJob(jobName),
                                        (jobName, existingJob) => existingJob);

                                    // notify all-job subscribers about the new job
                                    SendJobEventAndValidateStreams(AnyJobHandle, new NewOrUpdatedJobEvent(jobName, jobSettings), writeBuffer);
                                    if (subscribe)
                                    {
                                        clients.AddOrUpdate(job.NativeHandle, _ => [pipe], (_, streams) => [.. streams, pipe]);
                                    }

                                    monitoredJobs.AddOrUpdate(job.NativeHandle, _ =>
                                    {
                                        var jobData = new MonitoredJobData(job, jobSettings);
                                        Win32JobModule.AssignIOCompletionPort(jobData.Job, iocpHandle);
                                        return jobData;
                                    }, (_, jobData) => jobData with { JobSettings = jobSettings });

                                    var resp = new MonitorJobResp(jobName);
                                    MessagePackSerializer.Serialize<IMonitorResponse>(writeBuffer, resp, cancellationToken: cts.Token);
                                    await pipe.WriteAsync(writeBuffer.WrittenMemory, cts.Token);
                                    break;
                                }
                            case GetJobNamesReq ev:
                                {
                                    if (ev.SubscribeToEvents)
                                    {
                                        clients.AddOrUpdate(AnyJobHandle, _ => [pipe], (_, streams) => [.. streams, pipe]);
                                    }

                                    var resp = new GetJobNamesResp([.. jobNameWin32JobMap.Keys]);
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
                // the IOCP listener thread will remove this named pipe from the clients
                pipe.Dispose();
            }
        }

        void IOCPListener()
        {
            var buffer = new ArrayBufferWriter<byte>(1024);
            var lastTimeWithJobs = DateTime.UtcNow;

            while (!cts.IsCancellationRequested)
            {
                if (!monitoredJobs.IsEmpty)
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

                    if (!monitoredJobs.TryGetValue(jobHandle, out var jobData))
                    {
                        // this might be expected if we already processed ExitProcess events for all the processes in the job
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

                    switch (jobEvent)
                    {
                        case NewProcessEvent { ProcessId: var processId }:
                            processIdJobHandleMap[processId] = jobHandle;
                            break;
                        case ExitProcessEvent { ProcessId: var processId }:
                            processIdJobHandleMap.TryRemove(processId, out _);
                            break;
                        default:
                            break;
                    }

                    foreach (var h in (Span<nuint>)[AnyJobHandle, jobHandle])
                    {
                        SendJobEventAndValidateStreams(h, jobEvent, buffer);
                    }

                    if (jobEvent is NoProcessesInJobEvent)
                    {
                        // remove job which has no processes
                        if (monitoredJobs.TryRemove(jobHandle, out _))
                        {
                            jobNameWin32JobMap.TryRemove(jobData.Job.Name, out _);
                            jobData.Job.Dispose();
                        }
                    }
                }
            }
        }

        void SendJobEventAndValidateStreams(nuint jobHandle, IMonitorResponse jobEvent, ArrayBufferWriter<byte> buffer)
        {
            if (clients.TryGetValue(jobHandle, out var streams) && streams.Length > 0)
            {
                List<NamedPipeServerStream> validStreams = new(streams.Length);
                MessagePackSerializer.Serialize(buffer, jobEvent, cancellationToken: cts.Token);

                // some tasks will throw an exception when client disconnected
                var sendTasks = streams.Select(stream =>
                {
                    try
                    {
                        return (stream, stream.WriteAsync(buffer.WrittenMemory, cts.Token).AsTask());
                    }
                    catch (Exception ex)
                    {
                        return (stream, Task.FromException(ex));
                    }
                }).ToList();

                while (sendTasks.Count > 0)
                {
                    var taskIndex = Task.WaitAny([.. sendTasks.Select(st => st.Item2)], ct);
                    var (stream, task) = sendTasks[taskIndex];

                    if (task.IsFaulted)
                    {
                        Logger.TraceEvent(TraceEventType.Information, 0,
                            $"[process monitor] failed to send data to the pipe: {task.Exception}");
                        stream.Dispose();
                    }
                    else
                    {
                        validStreams.Add(stream);
                    }
                    sendTasks.RemoveAt(taskIndex);
                }

                buffer.ResetWrittenCount();

                if (validStreams.Count != streams.Length)
                {
                    clients[jobHandle] = [.. validStreams];
                }
            }
        }
    }

    sealed record MonitoredJobData(Win32Job Job, JobSettings JobSettings);
}
