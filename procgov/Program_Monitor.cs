using Nerdbank.MessagePack;
using ProcessGovernor.Library;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Windows.Win32;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

namespace ProcessGovernor;

public static partial class Program
{
    private static readonly SecurityIdentifier UserIdentifier = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User!;

    public static async Task<int> Execute(RunAsMonitor monitor, CancellationToken ct)
    {
        if (monitor.NoGui)
        {
            PInvoke.FreeConsole();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        SynchronizedPriorityQueue<RunningJobId, DateTime> createdEmptyJobs = new();
        ConcurrentDictionary<RunningJobId, SafeHandle> jobHandles = [];
        ConcurrentDictionary<RunningJobId, ImmutableArray<ChannelWriter<IMonitorNotification>>> clients = [];
        // modified by the notifier
        ConcurrentDictionary<uint, RunningJobId> governedProcesses = [];

        using var iocp = new Win32JobIoCompletionPort();

        int clientCount = 0;
        ManualResetEventSlim zeroClientsEvent = new(true);

        try
        {
            var notificationTask = HandleNotifications(cts.Token);

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

                var pipe = NamedPipeServerStreamAcl.Create(DefaultPipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.WriteThrough | PipeOptions.Asynchronous, 0, 0, pipeSecurity);

                try
                {
                    await pipe.WaitForConnectionAsync(cts.Token);

                    _ = StartClientThread(pipe, cts.Token);

                    pipe = null;
                }
                catch (IOException ex)
                {
                    // IOException that is raised if the pipe is broken or disconnected.
                    Logger.TraceError("[monitor] Server pipe got broken: {0}", ex);
                }
                catch (Exception ex) when (ex.IsCancelledException())
                {
                    Logger.TraceVerbose("[monitor] Server was cancelled.");
                }
                finally
                {
                    pipe?.Dispose();
                }
            }

            Logger.TraceVerbose("[monitor] Server is waiting for notifier to finish.");
            await notificationTask;

            Logger.TraceVerbose("[monitor] Server is waiting for all Clients to finish.");
            if (!zeroClientsEvent.Wait(1000, CancellationToken.None))
            {
                Logger.TraceInformation("[monitor] Server timed out when waiting for Clients to finish.");
            }

            Logger.TraceVerbose("[monitor] Server finished.");

            return 0;
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : -1;
        }
        finally
        {
            foreach (var jobHandle in jobHandles.Values)
            {
                jobHandle.Dispose();
            }
        }

        // Helpers (Execute)

        async Task StartClientThread(NamedPipeServerStream pipe, CancellationToken ct)
        {
            var subscribedJobIds = new HashSet<RunningJobId>();

            if (!PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid))
            {
                Logger.TraceWarning("[monitor] Could not retrieve the client PID for the pipe");
            }

            var clientChannel = Channel.CreateBounded<IMonitorNotification>(new BoundedChannelOptions(20)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            }, (n) =>
            {
                Logger.TraceWarning("[monitor] Client#{0} channel is full. Dropped notification: {1}", clientPid, n);
            });
            ChannelReader<IMonitorNotification> reader = clientChannel;

            try
            {
                if (Interlocked.Increment(ref clientCount) == 1)
                {
                    zeroClientsEvent.Reset();
                }

                var readBuffer = new ArrayBufferWriter<byte>(1024);

                Task<int>? pipeTask = null;
                Task<IMonitorNotification>? channelReaderTask = null;

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    pipeTask ??= pipe.ReadAsync(readBuffer.GetMemory(), ct).AsTask();
                    channelReaderTask ??= reader.ReadAsync(ct).AsTask();

                    if (await Task.WhenAny(pipeTask, channelReaderTask) == pipeTask)
                    {
                        if (pipeTask.Result == 0)
                        {
                            // no bytes were read
                            break;
                        }
                        await HandleClientRequest(pipeTask.Result);
                        pipeTask = null;
                    }
                    else
                    {
                        await SendNotification(channelReaderTask.Result, ct);
                        channelReaderTask = null;
                    }
                }

                // Helpers (StartClientThread)

                async Task HandleClientRequest(int bytesRead)
                {
                    readBuffer.Advance(bytesRead);

                    var processedBytes = 0;
                    while (processedBytes < readBuffer.WrittenCount)
                    {
                        var msgPackReader = new MessagePackReader(readBuffer.WrittenMemory[processedBytes..]);
                        var msg = MsgPackSerializer.Deserialize<IMonitorRequest>(ref msgPackReader, cancellationToken: ct);
                        processedBytes += (int)msgPackReader.Consumed;

                        switch (msg)
                        {
                            case GetJobIdReq { ProcessId: var processId }:
                                {
                                    Logger.TraceVerbose("[monitor] Client#{0} GetJobIdReq: {1}", clientPid, processId);
                                    var resp = new GetJobIdResp(
                                        governedProcesses.TryGetValue(processId, out var jobId) ? jobId : new RunningJobId());
                                    await MsgPackSerializer.SerializeAsync<IMonitorResponse>(pipe, resp, cancellationToken: ct);
                                    break;
                                }
                            case MonitorJobReq { JobId: var jobId }:
                                {
                                    Logger.TraceVerbose("[monitor] Client#{0} MonitorJobReq: {1}", clientPid, jobId);

                                    bool isSuccess;
                                    if (jobId.GetJobName() is { } jobName)
                                    {
                                        try
                                        {
                                            _ = jobHandles.AddOrUpdate(jobId,
                                                (jobId) =>
                                                {
                                                    var jobHandle = Win32JobModule.OpenJob(jobName);
                                                    iocp.AssignJob(jobHandle, jobId);
                                                    subscribedJobIds.Add(jobId);
                                                    // we enqueue a job as an empty job - it has MaxMonitorIdleTime to get a process assigned
                                                    createdEmptyJobs.Enqueue(jobId, DateTime.UtcNow + monitor.MaxMonitorIdleTime);

                                                    return jobHandle;
                                                },
                                                (_, jobHandle) => jobHandle);

                                            isSuccess = true;
                                        }
                                        catch (Win32Exception ex)
                                        {
                                            Logger.TraceError("[monitor] Client#{0} run into an error  when starting to monitor a job {1}: {2}",
                                                    clientPid, jobId, ex);
                                            isSuccess = false;
                                        }
                                    }
                                    else
                                    {
                                        Logger.TraceError("[monitor] Client#{0} tried to monitor an anonymous job {0}.", clientPid, jobId);
                                        isSuccess = false;
                                    }

                                    var resp = new AckResp(IsSuccess: isSuccess);
                                    await MsgPackSerializer.SerializeAsync<IMonitorResponse>(pipe, resp, cancellationToken: ct);
                                    break;
                                }
                            case SubscribeToNotificationsReq { JobId: var jobId }:
                                {
                                    Logger.TraceVerbose("[monitor] Client#{0} SubscribeToNotificationsReq: {1}", clientPid, jobId);
                                    var resp = new AckResp(IsSuccess: jobHandles.ContainsKey(jobId));

                                    await MsgPackSerializer.SerializeAsync<IMonitorResponse>(pipe, resp, cancellationToken: ct);

                                    if (resp.IsSuccess)
                                    {
                                        clients.AddOrUpdate(jobId, _ => [clientChannel],
                                            (_, channels) => [.. channels, clientChannel]
                                        );
                                    }
                                    break;
                                }
                            default:
                                Debug.Assert(false);
                                break;
                        }
                    }

                    readBuffer.ResetWrittenCount();
                }

                async Task SendNotification(IMonitorNotification notification, CancellationToken ct)
                {
                    Logger.TraceVerbose("[monitor] Client#{0} sending notifcation: {1}", clientPid, notification);


                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(2000);

                    try
                    {
                        await MsgPackSerializer.SerializeAsync<IMonitorResponse>(pipe, notification, cancellationToken: ct);
                    }
                    catch (Exception ex) when (ex.IsCancelledException())
                    {
                        Logger.TraceWarning("[monitor] Client#{0} writing notification {1} to a named pipe timed out ", clientPid,
                            notification);
                    }

                    if (notification is NoProcessesInJobEvent n)
                    {
                        var jobId = n.JobId;
                        Debug.Assert(subscribedJobIds.Contains(jobId));
                        subscribedJobIds.Remove(jobId);

                        // notifier will remove all the channels assigned to a given job - no need to do anything here
                    }
                }
                Logger.TraceVerbose("[monitor] Client#{0} finished.", clientPid);
            }
            catch (IOException ex)
            {
                Logger.TraceVerbose("[monitor] Client#{0} finished because of a broken named pipe: {1}", clientPid, ex);
            }
            catch (Exception ex) when (ex.IsCancelledException())
            {
                Logger.TraceVerbose("[monitor] Client#{0} was cancelled: {1}", clientPid, ex);
            }
            catch (Exception ex)
            {
                Logger.TraceVerbose("[monitor] Client#{0} finished with error: {1}", clientPid, ex);
            }
            finally
            {
                if (Interlocked.Decrement(ref clientCount) == 0)
                {
                    zeroClientsEvent.Set();
                }

                foreach (var jobId in subscribedJobIds)
                {
                    if (!TryRemovingClientChannel(jobId))
                    {
                        Logger.TraceWarning("[monitor][{0}] Could not remove the channel for job '{0}'", clientPid, jobId);
                    }
                }

                pipe.Dispose();


                bool TryRemovingClientChannel(RunningJobId jobId)
                {
                    int tryCount = 3;
                    while (tryCount > 0 && clients.TryGetValue(jobId, out var channels) &&
                        !clients.TryUpdate(jobId, channels.Remove(clientChannel), channels))
                    {
                        tryCount--;
                    }
                    return tryCount > 0;
                }
            }
        }

        async Task HandleNotifications(CancellationToken ct)
        {
            Dictionary<RunningJobId, List<uint>> jobProcesses = [];
            var lastActivityCheckTimeUtc = DateTime.UtcNow;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // remove jobs which did not get any processes attached in maxMonitorIdleTime
                    while (!ct.IsCancellationRequested && createdEmptyJobs.TryDequeue(DateTime.UtcNow, out var jobId))
                    {
                        Logger.TraceInformation("[monitor] Notifier found job '{0}' with no processes assigned.", jobId);
                        RemoveEmptyJob(jobId);
                    }

                    if (!jobHandles.IsEmpty || !zeroClientsEvent.IsSet)
                    {
                        lastActivityCheckTimeUtc = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - lastActivityCheckTimeUtc) > monitor.MaxMonitorIdleTime)
                    {
                        Logger.TraceInformation("[monitor] Notifier detected no jobs and clients - stopping.");
                        break;
                    }

                    while (iocp.TryRead(out var notification))
                    {
                        Logger.TraceVerbose("[monitor] Notification received: {0}", notification);

                        if (clients.TryGetValue(notification.JobId, out var channels))
                        {
                            foreach (var channel in channels)
                            {
                                channel.TryWrite(notification);
                            }
                        }

                        var jobId = notification.JobId;
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
                    }

                    await Task.Delay(500, ct);

                    // Helpers

                    void OnNewProcessEvent(RunningJobId jobId, uint processId)
                    {
                        createdEmptyJobs.Remove(jobId);

                        governedProcesses[processId] = jobId;
                        if (jobProcesses.TryGetValue(jobId, out var processes))
                        {
                            processes.Add(processId);
                        }
                        else { jobProcesses.Add(jobId, [processId]); }
                    }

                    void OnExitProcessEvent(RunningJobId jobId, uint processId)
                    {
                        governedProcesses.TryRemove(processId, out _);
                        if (jobProcesses.TryGetValue(jobId, out var processes))
                        {
                            processes.Remove(processId);
                        }
                    }

                    void OnEmptyJobEvent(RunningJobId jobId)
                    {
                        // when using TerminateJob we might not receive the ExitProcess events
                        // so there might be some processes to remove
                        if (jobProcesses.TryGetValue(jobId, out var processes))
                        {
                            foreach (var processId in processes)
                            {
                                governedProcesses.TryRemove(processId, out _);
                            }
                        }
                        RemoveEmptyJob(jobId);
                    }
                }
                Logger.TraceInformation("[monitor] Notifier stopped.");
            }
            catch (Exception ex) when (ex.IsCancelledException())
            {
                Logger.TraceInformation("[monitor] Notifier stopped because of cancellation.");
            }
            catch (Exception ex)
            {
                Logger.TraceInformation("[monitor] Notifier stopped because of an error: {0}", ex);
            }
            finally
            {
                // notifier is critical and if the application was not stopping we should stop it
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
            }

            // Helpers

            void RemoveEmptyJob(RunningJobId jobId)
            {
                Debug.Assert(!governedProcesses.Any(kv => kv.Value == jobId));

                createdEmptyJobs.Remove(jobId);
                clients.TryRemove(jobId, out _);
                jobProcesses.Remove(jobId);

                if (jobHandles.TryRemove(jobId, out var jobHandle))
                {
                    iocp.UnassignJob(jobHandle);
                    jobHandle.Dispose();
                }
            }

        }
    }
}
