using MessagePack;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using static ProcessGovernor.NtApi;
using System.Diagnostics.CodeAnalysis;

namespace ProcessGovernor;

static partial class Program
{
    // main pipe used to receive commands from the client and respond to them
    const string PipeName = "procgov";

    public static async Task<int> Execute(RunAsMonitor monitor, CancellationToken ct)
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var monitoredJobs = new MonitoredJobs();
        using var notifier = new Notifier();
        var processes = new GovernedProcesses();

        using var iocpHandle = CheckWin32Result(PInvoke.CreateIoCompletionPort(new SafeFileHandle(HANDLE.INVALID_HANDLE_VALUE, true),
                                                    new SafeFileHandle(nint.Zero, true), nuint.Zero, 0));
        var iocpListener = Task.Factory.StartNew(IOCPListener, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // FIXME: we should stop if there are no more jobs to monitor in the last,for example, 10 min.
        while (!cts.IsCancellationRequested)
        {
            // FIXME: set pipe security = current user + admins
            using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.WriteThrough | PipeOptions.Asynchronous);

            try
            {
                // Wait for a client to connect
                await pipe.WaitForConnectionAsync(cts.Token);

                _ = StartClientThread(pipe);
            }
            // IOException that is raised if the pipe is broken or disconnected.
            catch (IOException ex)
            {
                Debug.WriteLine("[procgov monitor] broken named pipe: " + ex);
            }
            catch (Exception ex) when (ex is OperationCanceledException || (
                    ex is AggregateException && ex.InnerException is TaskCanceledException))
            {
                Debug.WriteLine("[procgov monitor] cancellation: " + ex);
            }
        }

        async Task StartClientThread(NamedPipeServerStream pipe)
        {
            PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid);
            try
            {
                while (!cts.IsCancellationRequested && pipe.IsConnected)
                {
                    switch (await MessagePackSerializer.DeserializeAsync<IMonitorRequest>(pipe, cancellationToken: cts.Token))
                    {
                        case MonitorJob monitorJob:
                            var job = Win32JobModule.OpenJob(monitorJob.JobName);
                            Win32JobModule.AssignIOCompletionPort(job, iocpHandle);
                            monitoredJobs.AddJob(job);
                            if (monitorJob.SubscribeToEvents)
                            {
                                notifier.AddNotificationStream(job.NativeHandle, pipe);
                            }
                            await MessagePackSerializer.SerializeAsync(pipe, new JobMonitored(job.JobName),
                                cancellationToken: ct);
                            break;
                        case IsProcessGoverned { ProcessId: var processId }:
                            var jobName = processes.TryGetJobAssignedToProcess(processId, out var jobHandle) && 
                                            monitoredJobs.TryGetJob(jobHandle, out job) ? job.JobName : "";
                            await MessagePackSerializer.SerializeAsync(pipe, new ProcessStatus(jobName), cancellationToken: cts.Token);
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (IOException ex)
            {
                Logger.TraceEvent(TraceEventType.Warning, 0,
                    $"[procgov monitor][{clientPid}] broken named pipe: {ex}");
            }
            catch (Exception ex) when (ex is OperationCanceledException || (
                    ex is AggregateException && ex.InnerException is TaskCanceledException))
            {
                Logger.TraceEvent(TraceEventType.Warning, 0,
                    $"[procgov monitor][{clientPid}] cancellation: {ex}");
            }
            catch (Exception ex)
            {
                Logger.TraceEvent(TraceEventType.Warning, 0,
                    $"[procgov monitor][{clientPid}] error: {ex}");
            }
            finally
            {
                pipe.Close();
            }
        }

        void IOCPListener()
        {
            // terminate the task if there are no jobs monitored
            while (!cts.IsCancellationRequested)
            {
                unsafe
                {
                    if (!PInvoke.GetQueuedCompletionStatus(iocpHandle, out uint msgIdentifier,
                        out nuint jobHandle, out var msgData, 100 /* ms */))
                    {
                        var winerr = Marshal.GetLastWin32Error();
                        Logger.TraceEvent(TraceEventType.Error, 0, $"[process monitor] IOCP listener failed: {winerr:x}");
                        return;
                    }

                    switch (msgIdentifier)
                    {
                        case PInvoke.JOB_OBJECT_MSG_NEW_PROCESS:
                            Logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, $"Process {(uint)msgData} has started");
                            break;
                        case PInvoke.JOB_OBJECT_MSG_EXIT_PROCESS:
                        case PInvoke.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS:
                            Logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, $"Process {(int)msgData} exited");
                            break;
                        case PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO:
                            Logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, "no active processes in the job.");
                            break; // ALWAYS EXIT - no more processes running in the job
                        case PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_LIMIT:
                            break;
                        case PInvoke.JOB_OBJECT_MSG_JOB_MEMORY_LIMIT:
                        case PInvoke.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT:
                            Logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, 
                                $"Process {(int)msgData} exceeded its memory limit");
                            break;
                        case PInvoke.JOB_OBJECT_MSG_END_OF_PROCESS_TIME:
                            Logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier,
                                $"Process {(int)msgData} exceeded its user-mode execution limit");
                            break; // EXIT when single process - we hit the process user-time limit
                        case PInvoke.JOB_OBJECT_MSG_END_OF_JOB_TIME:
                            Logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier,
                                "Job exceeded its user-mode execution limit");
                            break; // ALWAYS EXIT - we hit the job user-time limit
                        default:
                            Trace.TraceInformation($"Unknown message: {msgIdentifier}");
                            break;
                    }
                }
            }
        }
    }

    private sealed class GovernedProcesses
    {
        readonly object lck = new();
        readonly Dictionary<int, nint> processJobMap = [];

        public void JobAssignedToProcess(int processId, nint jobHandle)
        {
            lock (lck)
            {
                processJobMap.Add(processId, jobHandle);
            }
        }

        public void ProcessExited(int processId)
        {
            lock (lck)
            {
                processJobMap.Remove(processId);
            }
        }

        public bool TryGetJobAssignedToProcess(int processId, out nint jobHandle)
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
        readonly Dictionary<nint, Stream[]> notificationStreams = [];

        public void AddNotificationStream(nint jobHandle, Stream stream)
        {
            lock (lck)
            {
                if (!notificationStreams.TryGetValue(jobHandle, out var streams))
                {
                    streams = [stream];
                    notificationStreams.Add(jobHandle, streams);
                }
                else
                {
                    notificationStreams[jobHandle] = [.. streams, stream];
                }
            }
        }

        public void RemoveNotificationStream(nint jobHandle, Stream stream)
        {
            lock (lck)
            {
                if (notificationStreams.TryGetValue(jobHandle, out var streams))
                {
                    notificationStreams[jobHandle] = streams.Where(s => s != stream).ToArray();
                }
            }
        }

        public Stream[] GetNotificationStreams(nint jobHandle)
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

        public void AddJob(Win32Job job)
        {
            lock (lck)
            {
                monitoredJobs.Add(job.NativeHandle, job);
            }
        }

        public void RemoveJob(Win32Job job)
        {
            lock (lck)
            {
                monitoredJobs.Remove(job.NativeHandle);
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
