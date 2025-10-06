using ProcessGovernor.Library;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ProcessGovernor;

public static partial class Program
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
        Debug.Assert(!(app.ExitBehavior == ExitBehavior.DontWaitForJobCompletion && app.JobSettings?.JobClockTimeLimitInMilliseconds != 0));

        ProcessGovernorLibraryApi.TryEnablingDebugPrivilege();

        var notificationChannel = Channel.CreateUnbounded<IMonitorNotification>();

        var jobId = new RunningJobId(app.JobSettings?.RunMode ?? RunModes.Default, new(""));

        using var externalMonitorPipe = jobId.IsNamedJob() ? await StartMonitor(ct) : null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var processGovernor = new ProcessGovernorInstance(notificationChannel, (ct) => externalMonitorPipe, 0, cts.Token);

        using var jobHandle = processGovernor.GetOrCreateJobObject(jobId, app.JobSettings ?? JobSettings.Empty, out _);

        if (app.JobSettings is not null && !quiet)
        {
            ShowLimits(app.JobSettings);
        }

        if (app.JobTarget is LaunchProcess l)
        {
            if (app.StartBehavior == StartBehavior.Freeze)
            {
                Debug.Assert(app.JobSettings?.RunMode is RunInNamedJob);
                Win32JobModule.SetJobFreezeStatus(jobHandle.Value, true);
            }

            var processArgs = string.Join(" ", l.Procargs.Select((s) => s.Contains(' ') ? "\"" + s + "\"" : s));
            var flags = l.NewConsole ? ProcessCreationFlags.NewConsole : 0;

            using var processObject = processGovernor.StartProcessInJob(processArgs, flags, jobId);

            if (app.ExitBehavior != ExitBehavior.DontWaitForJobCompletion)
            {
                await WaitForJobCompletion(jobHandle.Value, ct);
            }

            return PInvoke.GetExitCodeProcess(processObject.Handle, out var rc) ? (int)rc : 0;
        }
        else
        {
            if (app.StartBehavior != StartBehavior.None)
            {
                Debug.Assert(app.JobSettings?.RunMode is RunInNamedJob);
                Win32JobModule.SetJobFreezeStatus(jobHandle.Value, app.StartBehavior == StartBehavior.Freeze);
            }

            Debug.Assert(app.JobTarget is AttachToProcess);
            foreach (var pid in ((AttachToProcess)app.JobTarget).Pids)
            {
                processGovernor.AssignExistingProcessToJob(pid, jobId);
            }

            if (app.ExitBehavior != ExitBehavior.DontWaitForJobCompletion)
            {
                await WaitForJobCompletion(jobHandle.Value, ct);
            }

            return 0;
        }

        // Execute: helper functions

        async Task WaitForJobCompletion(SafeHandle jobHandle, CancellationToken ct)
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

            _ = MonitorJobUntilCompletion(cts.Token);

            Win32JobModule.WaitForTheJobToComplete(jobHandle, cts.Token);

            if (ct.IsCancellationRequested && app.ExitBehavior == ExitBehavior.TerminateJobOnExit)
            {
                Logger.TraceInformation("[cmd] Terminating job.");
                Win32JobModule.TerminateJob(jobHandle, 0x1f);
            }

            // stop all background tasks
            cts.Cancel();

            if (!await processGovernor.WaitForStop(TimeSpan.FromSeconds(2)))
            {
                Logger.TraceVerbose("[cmd] The governor task timed out.");
            }

            async Task MonitorJobUntilCompletion(CancellationToken ct)
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        switch (await notificationChannel.Reader.ReadAsync(ct))
                        {
                            case NewProcessEvent ev:
                                Logger.TraceInformation("[cmd] New process {0} in job.", ev.ProcessId);
                                break;
                            case ExitProcessEvent ev:
                                Logger.TraceInformation("[cmd] Process {0} exited.", ev.ProcessId);
                                break;
                            case JobLimitExceededEvent ev:
                                Logger.TraceInformation("[cmd] Job limit '{0}' exceeded.", ev.ExceededLimit);
                                break;
                            case ProcessLimitExceededEvent:
                                Logger.TraceInformation("[cmd] Process limit exceeded.");
                                break;
                            case NoProcessesInJobEvent:
                                Logger.TraceInformation("[cmd] No processes in job.");
                                return;
                        }
                    }
                }
                catch (Exception ex) when (ex.IsCancelledException())
                {
                    Logger.TraceVerbose("[cmd] Stopping monitoring because of cancellation.");
                }
            }
        }

        static async Task<NamedPipeClientStream?> StartMonitor(CancellationToken ct)
        // async static Task<> StartMonitor(RunningJobId jobId, ChannelWriter<IMonitorNotification> writer, CancellationToken ct)
        {
            // passed to StartForwardingNotifications and disposed there
            var pipe = new NamedPipeClientStream(".", ProcessGovernorLibraryApi.DefaultPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                try
                {
                    await pipe.ConnectAsync(10, ct);
                    return pipe;
                }
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
                    return pipe;
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
                        if (!PInvoke.CreateProcess(null, new PWSTR(cmdlinePtr), null, null, false, 0, null, null, &si, &pi))
                        {
                            throw new Win32Exception();
                        }
                    }

                    PInvoke.CloseHandle(pi.hProcess);
                    PInvoke.CloseHandle(pi.hThread);
                }
            }
            catch (Exception ex) when (ex.IsCancelledException())
            {
                Logger.TraceError("[cmd] Failed to start the monitor process: {0}", ex);

                pipe.Dispose();

                return null;
            }
        }
    }
}

