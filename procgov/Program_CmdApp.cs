using MessagePack;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;
using static ProcessGovernor.Win32.Helpers;

namespace ProcessGovernor;

static partial class Program
{
    public static async Task<int> Execute(RunAsCmdApp app, CancellationToken ct)
    {
        if (app.LaunchConfig.HasFlag(LaunchConfig.NoGui))
        {
            PInvoke.ShowWindow(PInvoke.GetConsoleWindow(), SHOW_WINDOW_CMD.SW_HIDE);
        }

        var quiet = app.LaunchConfig.HasFlag(LaunchConfig.Quiet);
        if (!quiet)
        {
            Logger.Listeners.Add(new ConsoleTraceListener());

            ShowHeader();
        }

        // We can't enforce clock time limit without waiting for job completion
        Debug.Assert(!(app.ExitBehavior == ExitBehavior.DontWaitForJobCompletion && app.JobSettings.ClockTimeLimitInMilliseconds != 0));

        var buffer = new ArrayBufferWriter<byte>(1024);

        AccountPrivilegeModule.EnableProcessPrivileges(PInvoke.GetCurrentProcess_SafeHandle(), [("SeDebugPrivilege", false)]);

        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var noMonitor = app.LaunchConfig.HasFlag(LaunchConfig.NoMonitor);
        if (!noMonitor)
        {
            await SetupMonitorConnection();
        }

        if (app.JobTarget is LaunchProcess l)
        {
            using var job = await OpenOrCreateJob(app.JobName);

            // in launch mode, we always freeze to setup the privileges before the process starts execution
            Win32JobModule.SetJobFreezeStatus(job, true);

            if (!noMonitor)
            {
                await StartOrUpdateMonitoring(job);
            }

            var targetProcess = ProcessModule.CreateProcessWithJobAssigned(
                l.Procargs, l.NewConsole, app.Environment, job.Handle);

            SetupProcessPrivileges(targetProcess);

            if (app.StartBehavior != StartBehavior.Freeze)
            {
                Win32JobModule.SetJobFreezeStatus(job, false);
            }

            if (!quiet)
            {
                ShowLimits(app.JobSettings);
            }

            if (app.ExitBehavior != ExitBehavior.DontWaitForJobCompletion)
            {
                await WaitForJobCompletion(job);
            }

            return PInvoke.GetExitCodeProcess(targetProcess.Handle, out var rc) ? (int)rc : 0;
        }
        else
        {
            Debug.Assert(app.JobTarget is AttachToProcess);
            var targetProcesses = ((AttachToProcess)app.JobTarget).Pids.Select(pid => ProcessModule.OpenProcess(pid, app.Environment.Count != 0)).ToArray();

            foreach (var targetProcess in targetProcesses)
            {
                ProcessModule.SetProcessEnvironmentVariables(targetProcess.Handle, app.Environment);

                SetupProcessPrivileges(targetProcess);
            }

            using var job = await AttachJobToProcesses(targetProcesses);

            if (app.StartBehavior != StartBehavior.None)
            {
                Win32JobModule.SetJobFreezeStatus(job, app.StartBehavior == StartBehavior.Freeze);
            }

            if (!quiet)
            {
                ShowLimits(app.JobSettings);
            }

            if (app.ExitBehavior != ExitBehavior.DontWaitForJobCompletion)
            {
                await WaitForJobCompletion(job);
            }

            return 0;
        }

        ////////////////////////////////////////////////////////

        async Task SetupMonitorConnection()
        {
            try { await pipe.ConnectAsync(10, ct); }
            catch
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0, "Launching monitor...");
                StartMonitor();

                while (!pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await pipe.ConnectAsync(100, ct);
                        Logger.TraceEvent(TraceEventType.Verbose, 0, "Waiting for monitor to start...");
                    }
                    catch { }
                }
            }


            static unsafe void StartMonitor()
            {
                if (Environment.ProcessPath is null)
                {
                    throw new InvalidOperationException("Can't launch monitor process because the ProcessPath is unknown.");
                }

                // we always enable verbose logs for the monitor since it uses ETW or Debug output
                string cmdline = $"{Environment.ProcessPath} --monitor --verbose --nogui";

                var pi = new PROCESS_INFORMATION();
                var si = new STARTUPINFOW();

                fixed (char* cmdlinePtr = cmdline)
                {
                    CheckWin32Result(PInvoke.CreateProcess(null, new PWSTR(cmdlinePtr), null, null, false,
                        0, null, null, &si, &pi));
                }

                PInvoke.CloseHandle(pi.hProcess);
                PInvoke.CloseHandle(pi.hThread);
            }
        }

        void SetupProcessPrivileges(Win32Process proc)
        {
            foreach (var (privilegeName, isSuccess) in AccountPrivilegeModule.EnableProcessPrivileges(proc.Handle,
                [.. app.Privileges.Select(name => (name, Required: false))]).Where(ap => !ap.IsSuccess))
            {
                Logger.TraceEvent(TraceEventType.Error, 0, $"Setting privilege {privilegeName} for process {proc.Id} failed.");
            }
        }

        async Task StartOrUpdateMonitoring(Win32Job job)
        {
            var jobSettings = app.JobSettings;

            buffer.ResetWrittenCount();
            bool subscribeToEvents = app.ExitBehavior != ExitBehavior.DontWaitForJobCompletion;
            MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new MonitorJobReq(job.Name,
                subscribeToEvents, jobSettings), cancellationToken: ct);
            await pipe.WriteAsync(buffer.WrittenMemory, ct);

            buffer.ResetWrittenCount();
            int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
            buffer.Advance(readBytes);

            switch (MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory,
                        bytesRead: out var deserializedBytes, cancellationToken: ct))
            {
                case MonitorJobResp { JobName: var jobName } when job.Name == jobName: break;
                default: throw new InvalidOperationException("Unexpected monitor response");
            }
        }

        async Task<Win32Job> AttachJobToProcesses(Win32Process[] targetProcesses)
        {
            Debug.Assert(app.JobTarget is AttachToProcess);

            if (noMonitor)
            {
                var job = await OpenOrCreateJob(app.JobName);

                // try to assign a new job to processes
                Array.ForEach(targetProcesses, targetProcess => Win32JobModule.AssignProcess(job, targetProcess.Handle));

                return job;
            }
            else
            {
                var assignedJobNames = await GetProcessJobNames(((AttachToProcess)app.JobTarget).Pids);
                Debug.Assert(assignedJobNames.Length == targetProcesses.Length);

                var existingJobName = assignedJobNames.Index().Aggregate("", (foundJobName, processJob) =>
                {
                    if (processJob.Item != "")
                    {
                        if (foundJobName != "" && foundJobName != processJob.Item)
                        {
                            throw new ArgumentException($"processes belong to different Process Governor jobs (found '{foundJobName}', " +
                                $"but {targetProcesses[processJob.Index].Id} is assigned to '{processJob.Item}')");
                        }
                        else
                        {
                            return processJob.Item;
                        }
                    }
                    else
                    {
                        return foundJobName;
                    }
                });

                var jobName = existingJobName switch
                {
                    "" => app.JobName,
                    _ when app.JobName is null => existingJobName,
                    _ when app.JobName == existingJobName => existingJobName,
                    _ => throw new ArgumentException(
                        $"requested job name is '{app.JobName}' but one of the processes is already assigned to job '{existingJobName}'")
                };

                var job = await OpenOrCreateJob(jobName);
                Logger.TraceEvent(TraceEventType.Verbose, 0, $"The job name is '{job.Name}'");

                await StartOrUpdateMonitoring(job);

                // assign job to all the processes which do not have a job assigned
                for (int i = 0; i < targetProcesses.Length; i++)
                {
                    var targetProcess = targetProcesses[i];

                    if (assignedJobNames[i] == "")
                    {
                        Win32JobModule.AssignProcess(job, targetProcess.Handle);
                    }
                }

                return job;
            }


            async Task<string[]> GetProcessJobNames(uint[] processIds)
            {
                var jobNames = new string[processIds.Length];
                for (int i = 0; i < jobNames.Length; i++)
                {
                    buffer.ResetWrittenCount();
                    MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new GetJobNameReq(processIds[i]), cancellationToken: ct);
                    await pipe.WriteAsync(buffer.WrittenMemory, ct);

                    buffer.ResetWrittenCount();
                    int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
                    buffer.Advance(readBytes);

                    jobNames[i] = MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory,
                        bytesRead: out var deseralizedBytes, cancellationToken: ct) switch
                    {
                        GetJobNameResp { JobName: var jobName } => jobName,
                        _ => throw new InvalidOperationException("Unexpected monitor response")
                    };
                }
                return jobNames;
            }
        }

        async ValueTask<Win32Job> OpenOrCreateJob(string? jobNameOrNull)
        {
            var (job, jobSettings) = (jobNameOrNull is { } jobName && await GetJobSettings(jobName) is { } js) ?
                (Win32JobModule.OpenJob(jobName), js.Merge(app.JobSettings)) :
                (Win32JobModule.CreateJob(jobNameOrNull ?? GenerateNewJobName()), app.JobSettings);

            Win32JobModule.SetLimits(job, jobSettings);

            return job;


            async ValueTask<JobSettings?> GetJobSettings(string jobName)
            {
                buffer.ResetWrittenCount();
                MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new GetJobSettingsReq(jobName), cancellationToken: ct);
                await pipe.WriteAsync(buffer.WrittenMemory, ct);

                buffer.ResetWrittenCount();
                int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
                buffer.Advance(readBytes);

                return MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory,
                    bytesRead: out var deseralizedBytes, cancellationToken: ct) switch
                {
                    GetJobSettingsResp { JobName: "" } => null, // job does not exist
                    GetJobSettingsResp { JobSettings: var jobSettings } => jobSettings,
                    _ => throw new InvalidOperationException("Unexpected monitor response")
                };
            }
        }

        async Task WaitForJobCompletion(Win32Job job)
        {
            if (!quiet)
            {
                if (app.ExitBehavior == ExitBehavior.TerminateJobOnExit)
                {
                    Console.WriteLine("Press Ctrl-C to end execution and terminate the job.");
                }
                else
                {
                    Console.WriteLine("Press Ctrl-C to end execution without terminating the process.");
                }
                Console.WriteLine();
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (app.JobSettings.ClockTimeLimitInMilliseconds > 0)
            {
                cts.CancelAfter((int)app.JobSettings.ClockTimeLimitInMilliseconds);
            }

            if (noMonitor)
            {
                Win32JobModule.WaitForTheJobToComplete(job, cts.Token);
            }
            else
            {
                await MonitorJobUntilCompletion(cts.Token);
            }

            if (ct.IsCancellationRequested && app.ExitBehavior == ExitBehavior.TerminateJobOnExit ||
                !ct.IsCancellationRequested && cts.IsCancellationRequested && app.JobSettings.ClockTimeLimitInMilliseconds > 0)
            {
                Logger.TraceEvent(TraceEventType.Information, 0, $"Terminating job {job.Name}.");
                Win32JobModule.TerminateJob(job, 0x1f);
            }


            async Task MonitorJobUntilCompletion(CancellationToken ct)
            {
                try
                {
                    while (pipe.IsConnected && await pipe.ReadAsync(buffer.GetMemory(), ct) is var bytesRead && bytesRead > 0)
                    {
                        buffer.Advance(bytesRead);

                        var processedBytes = 0;
                        while (processedBytes < buffer.WrittenCount)
                        {
                            var notification = MessagePackSerializer.Deserialize<IMonitorResponse>(
                                buffer.WrittenMemory[processedBytes..], out var deserializedBytes, ct);

                            switch (notification)
                            {
                                case NewProcessEvent ev:
                                    Logger.TraceEvent(TraceEventType.Information, 11, $"New process {ev.ProcessId} in job.");
                                    break;
                                case ExitProcessEvent ev:
                                    Logger.TraceEvent(TraceEventType.Information, 12, $"Process {ev.ProcessId} exited.");
                                    break;
                                case JobLimitExceededEvent ev:
                                    Logger.TraceEvent(TraceEventType.Information, 13, $"Job limit '{ev.ExceededLimit}' exceeded.");
                                    break;
                                case ProcessLimitExceededEvent:
                                    Logger.TraceEvent(TraceEventType.Information, 14, $"Process limit exceeded.");
                                    break;
                                case NoProcessesInJobEvent:
                                    Logger.TraceEvent(TraceEventType.Information, 15, "No processes in job.");
                                    return;
                            }

                            processedBytes += deserializedBytes;
                        }

                        buffer.ResetWrittenCount();
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || (
                        ex is AggregateException && ex.InnerException is TaskCanceledException))
                {
                    Logger.TraceEvent(TraceEventType.Verbose, 0, "Stopping monitoring because of cancellation.");
                }
            }
        }
    }
}

