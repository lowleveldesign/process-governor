using MessagePack;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;
using static ProcessGovernor.NtApi;

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
            using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName());

            Win32JobModule.SetLimits(job, app.JobSettings);

            if (!noMonitor)
            {
                await StartOrUpdateMonitoring(job);
            }

            var (targetProcess, mainThreadHandle) = ProcessModule.CreateSuspendedProcessWithJobAssigned(
                l.Procargs, l.NewConsole, app.Environment, job.Handle);

            SetupProcessPrivileges(targetProcess);

            CheckWin32Result(PInvoke.ResumeThread(mainThreadHandle));
            mainThreadHandle.Dispose();

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
                app.Privileges.Select(name => (name, Required: false)).ToList()).Where(ap => !ap.IsSuccess))
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
            var jobSettings = app.JobSettings;

            Debug.Assert(app.JobTarget is AttachToProcess);

            if (noMonitor)
            {
                // in noMonitor mode, we can't rely on job names as they may not exist, so we simply
                // try to assign a new job to processes
                var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName());

                Win32JobModule.SetLimits(job, jobSettings);

                Array.ForEach(targetProcesses, targetProcess => Win32JobModule.AssignProcess(job, targetProcess.Handle));

                return job;
            }
            else
            {
                var assignedJobNames = await GetProcessJobNames(((AttachToProcess)app.JobTarget).Pids);
                Debug.Assert(assignedJobNames.Length == targetProcesses.Length);

                var jobName = "";
                if (Array.FindIndex(assignedJobNames, jobName => jobName != "") is var monitoredProcessIndex && monitoredProcessIndex != -1)
                {
                    jobName = assignedJobNames[monitoredProcessIndex];

                    // check if all assigned jobs are the same
                    if (Array.FindIndex(assignedJobNames, n => n != jobName && n != "") is var idx && idx != -1)
                    {
                        throw new ArgumentException($"processes belong to different Process Governor jobs (found '{jobName}', " +
                            $"but, for example, {targetProcesses[idx].Id} is assigned to '{assignedJobNames[idx]}')");
                    }

                    jobSettings = MergeJobSettings(jobSettings, await GetProcessJobSettings(jobName));
                }

                var job = jobName != "" ? Win32JobModule.OpenJob(jobName) : Win32JobModule.CreateJob(Win32JobModule.GetNewJobName());

                Win32JobModule.SetLimits(job, jobSettings);

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

            async Task<JobSettings> GetProcessJobSettings(string jobName)
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
                    GetJobSettingsResp { JobSettings: var jobSettings } => jobSettings,
                    _ => throw new InvalidOperationException("Unexpected monitor response")
                };
            }

            static JobSettings MergeJobSettings(JobSettings current, JobSettings other)
            {
                return new JobSettings(
                    maxProcessMemory: current.MaxProcessMemory > 0 ? current.MaxProcessMemory : other.MaxProcessMemory,
                    maxJobMemory: current.MaxJobMemory > 0 ? current.MaxJobMemory : other.MaxJobMemory,
                    maxWorkingSetSize: current.MaxWorkingSetSize > 0 ? current.MaxWorkingSetSize : other.MaxWorkingSetSize,
                    minWorkingSetSize: current.MinWorkingSetSize > 0 ? current.MinWorkingSetSize : other.MinWorkingSetSize,
                    cpuAffinity: current.CpuAffinity is not null ? current.CpuAffinity : other.CpuAffinity,
                    cpuMaxRate: current.CpuMaxRate > 0 ? current.CpuMaxRate : other.CpuMaxRate,
                    maxBandwidth: current.MaxBandwidth > 0 ? current.MaxBandwidth : other.MaxBandwidth,
                    processUserTimeLimitInMilliseconds: current.ProcessUserTimeLimitInMilliseconds > 0 ?
                        current.ProcessUserTimeLimitInMilliseconds : other.ProcessUserTimeLimitInMilliseconds,
                    jobUserTimeLimitInMilliseconds: current.JobUserTimeLimitInMilliseconds > 0 ?
                        current.JobUserTimeLimitInMilliseconds : other.JobUserTimeLimitInMilliseconds,
                    // clock time limit is governed by the running cmd client, so we always overwrite its value
                    clockTimeLimitInMilliseconds: other.ClockTimeLimitInMilliseconds,
                    propagateOnChildProcesses: current.PropagateOnChildProcesses || other.PropagateOnChildProcesses,
                    activeProcessLimit: current.ActiveProcessLimit > 0 ? current.ActiveProcessLimit : other.ActiveProcessLimit,
                    priorityClass: current.PriorityClass != PriorityClass.Undefined ? current.PriorityClass : other.PriorityClass
                );
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

    static void ShowLimits(JobSettings session)
    {
        const uint ONE_MB = 1_048_576;

        if (session.CpuAffinity is { } cpuAffinity && cpuAffinity.Length > 0)
        {
            Console.WriteLine($"CPU groups and affinity masks:");
            foreach (var (group, affinity) in cpuAffinity)
            {
                Console.WriteLine($"  {group}:{affinity:X16}");
            }
        }
        if (session.CpuMaxRate > 0)
        {
            Console.WriteLine($"Max CPU rate:                               {session.CpuMaxRate / 100.0:0.00}%");
            if (session.CpuAffinity != null)
            {
                Console.WriteLine("(NOTE: the CPU rate was adjusted to the CPU affinity settings)");
            }
        }
        if (session.MaxBandwidth > 0)
        {
            Console.WriteLine($"Max bandwidth (B):                          {(session.MaxBandwidth):#,0}");
        }
        if (session.MaxProcessMemory > 0)
        {
            Console.WriteLine($"Maximum committed memory (MB):              {(session.MaxProcessMemory / ONE_MB):0,0}");
        }
        if (session.MaxJobMemory > 0)
        {
            Console.WriteLine($"Maximum job committed memory (MB):          {(session.MaxJobMemory / ONE_MB):0,0}");
        }
        if (session.MinWorkingSetSize > 0)
        {
            Debug.Assert(session.MaxWorkingSetSize > 0);
            Console.WriteLine($"Minimum WS memory (MB):                     {(session.MinWorkingSetSize / ONE_MB):0,0}");
        }
        if (session.MaxWorkingSetSize > 0)
        {
            Debug.Assert(session.MinWorkingSetSize > 0);
            Console.WriteLine($"Maximum WS memory (MB):                     {(session.MaxWorkingSetSize / ONE_MB):0,0}");
        }
        if (session.ProcessUserTimeLimitInMilliseconds > 0)
        {
            Console.WriteLine($"Process user-time execution limit (ms):     {session.ProcessUserTimeLimitInMilliseconds:0,0}");
        }
        if (session.JobUserTimeLimitInMilliseconds > 0)
        {
            Console.WriteLine($"Job user-time execution limit (ms):         {session.JobUserTimeLimitInMilliseconds:0,0}");
        }
        if (session.ClockTimeLimitInMilliseconds > 0)
        {
            Console.WriteLine($"Clock-time execution limit (ms):            {session.ClockTimeLimitInMilliseconds:0,0}");
        }
        if (session.PropagateOnChildProcesses)
        {
            Console.WriteLine();
            Console.WriteLine("All configured limits will also apply to the child processes.");
        }
        Console.WriteLine();
    }
}

