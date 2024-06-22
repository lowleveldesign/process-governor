using MessagePack;
using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Threading;
using Windows.Win32.Foundation;
using static ProcessGovernor.NtApi;

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
        List<Win32Job> monitoredJobs = [];

        using var iocpHandle = CheckWin32Result(PInvoke.CreateIoCompletionPort(new SafeFileHandle(HANDLE.INVALID_HANDLE_VALUE, true),
                                                    new SafeFileHandle(nint.Zero, true), nuint.Zero, 0));
        Task jobMonitoringTask = Task.CompletedTask;

        // FIXME: we should stop if there are no more jobs to monitor in the last,for example, 10 min.
        while (!ct.IsCancellationRequested)
        {
            // FIXME: set pipe security = current user + admins
            using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.WriteThrough | PipeOptions.Asynchronous);

            try
            {
                // Wait for a client to connect
                await pipe.WaitForConnectionAsync(ct);

                if (!PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid))
                {
                    var err = Marshal.GetLastWin32Error();
                    Logger.TraceEvent(TraceEventType.Warning, 0, $"[{PipeName}] failed to read client PID: {err:x}");
                }

                switch (await MessagePackSerializer.DeserializeAsync<IMonitorRequest>(pipe, cancellationToken: ct))
                {
                    case MonitorJob monitorJob:
                        var job = Win32JobModule.TryOpen(monitorJob.JobName, out var jobHandle);
                        Win32JobModule.AssignIOCompletionPort(job, iocpHandle);
                        // FIXME assign job to the process
                        monitoredJobs.Add(job);
                        await MessagePackSerializer.SerializeAsync(pipe, new JobMonitored(), cancellationToken: ct);
                        break;
                    //    case IsProcessGoverned isProcessGoverned:
                    //        using (var processHandle = OpenProcess(createJobForProcess.ProcessId))
                    //        {
                    //            var job = Win32JobModule.TryOpen();
                    //            Win32JobModule.SetLimits(job, createJobForProcess.JobSettings, );
                    //            // FIXME: start the monitoring task (if it's not started yet)
                    //            await MessagePackSerializer.SerializeAsync(pipe, new JobAssigned(job.JobId), cancellationToken: ct);
                    //            break;
                    //        }
                    default:
                        break;
                }
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

        static Task MonitoringTask()
        {
            var shouldTerminate = false;

            // terminate if there are no jobs
            while (!shouldTerminate)
            {
                if (!PInvoke.GetQueuedCompletionStatus(, out var msgIdentifier,
                    out _, out var lpOverlapped, 100 /* ms */))
                {
                    if (Marshal.GetLastWin32Error() == 735 /* ERROR_ABANDONED_WAIT_0 */)
                    {
                        throw new Win32Exception();
                    }

                    // otherwise timeout
                    if (ClockTimeLimitInMilliseconds > 0 &&
                        processRunningTime.ElapsedMilliseconds > ClockTimeLimitInMilliseconds)
                    {
                        logger.TraceEvent(TraceEventType.Information, 0, "Clock time limit passed - terminating.");
                        PInvoke.TerminateJobObject(hJob, 1);
                        shouldTerminate = true;
                    }
                }
                else
                {
                    shouldTerminate = LogCompletionPacketAndCheckIfTerminating(msgIdentifier, (IntPtr)lpOverlapped);
                }
            }
        }
    }
}
