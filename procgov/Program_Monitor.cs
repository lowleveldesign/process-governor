using MessagePack;
using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Threading;
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

        Task jobMonitoringTask = Task.CompletedTask;
        // FIXME: create IOCP used to get job notifications

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
                        monitoredJobs.Add(job);
                        await MessagePackSerializer.SerializeAsync(pipe, new JobMonitored(), cancellationToken: ct);
                        break;
                    case IsProcessGoverned isProcessGoverned:
                        using (var processHandle = OpenProcess(createJobForProcess.ProcessId))
                        {
                            var job = Win32JobModule.TryOpen();
                            Win32JobModule.SetLimits(job, createJobForProcess.JobSettings, );
                            // FIXME: start the monitoring task (if it's not started yet)
                            await MessagePackSerializer.SerializeAsync(pipe, new JobAssigned(job.JobId), cancellationToken: ct);
                            break;
                        }
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

        SafeFileHandle OpenProcess(int pid)
        {
            using var currentProcessHandle = PInvoke.GetCurrentProcess_SafeHandle();
            var dbgpriv = AccountPrivilegeModule.EnablePrivileges((uint)Environment.ProcessId, currentProcessHandle, ["SeDebugPrivilege"],
                            TraceEventType.Information);
            try
            {
                return CheckWin32Result(PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION |
                    PROCESS_ACCESS_RIGHTS.PROCESS_SET_QUOTA | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE |
                    PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)pid));
            }
            finally
            {
                AccountPrivilegeModule.RestorePrivileges((uint)Environment.ProcessId, currentProcessHandle, dbgpriv, TraceEventType.Information);
            }
        }
    }
}
