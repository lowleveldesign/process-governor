using ProcessGovernor.Library;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Win32ProcessModule = ProcessGovernor.Library.Win32ProcessModule;

namespace ProcessGovernor.Tests.Code;

public partial class ProgramTests
{
    [Test]
    public async Task CmdAppLaunchProcessFailure()
    {
        var exception = await Assert.ThrowsExactlyAsync<Win32Exception>(async () =>
        {
            await Program.Execute(new RunAsCmdApp(JobSettings.Empty,
                new LaunchProcess(["____wrong-executable.exe"], false), LaunchConfig.Quiet, StartBehavior.None,
                ExitBehavior.WaitForJobCompletion),
                CancellationToken.None);
        });
        await Assert.That(exception?.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    public async Task CmdAppLaunchProcessExitCodeForwarding()
    {
        var exitCode = await Program.Execute(new RunAsCmdApp(JobSettings.Empty,
            new LaunchProcess(["cmd.exe", "/c", "exit 5"], false), LaunchConfig.Quiet, StartBehavior.None,
            ExitBehavior.WaitForJobCompletion), CancellationToken.None);
        await Assert.That(exitCode).IsEqualTo(5);
    }

    [Test]
    public async Task CmdAppAttachProcessExitCodeNotForwarding()
    {
        using var cts = new CancellationTokenSource(10000);

        using var cmd1 = Win32ProcessModule.StartProcess("cmd.exe /c exit 5", ProcessCreationFlags.Suspended,
            new(), out var cmd1MainThreadHandle);
        using var cmd2 = Win32ProcessModule.StartProcess("cmd.exe /c exit 6", ProcessCreationFlags.Suspended,
            new(), out var cmd2MainThreadHandle);

        var runTask = Task.Run(() => Program.Execute(new RunAsCmdApp(JobSettings.Empty,
            new AttachToProcess([cmd1.Id, cmd2.Id]), LaunchConfig.Quiet, StartBehavior.None,
            ExitBehavior.WaitForJobCompletion), cts.Token));

        // give time to start for the job
        await Task.Delay(1000);

        PInvoke.ResumeThread(cmd1MainThreadHandle);
        PInvoke.ResumeThread(cmd2MainThreadHandle);

        cmd1MainThreadHandle.Dispose();
        cmd2MainThreadHandle.Dispose();

        var exitCode = await runTask;

        // procgov does not forward exit codes when attaching to processes
        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task CmdAppLaunchProcessWithEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("TESTEMPTY", "SOMETHING");

        using var cts = new CancellationTokenSource(7000);
        ImmutableDictionary<string, string> configuredEnvVars = [
            new("TESTVAR1", "TESTVAR1_VAL;%USERPROFILE%"),
            new ("TESTVAR2", "TESTVAR2_VAL"),
            new("TESTEMPTY", "")
        ];

        using var cmd = Win32ProcessModule.StartProcess("cmd.exe /c timeout 4",
            ProcessCreationFlags.None, new Win32ProcessSettings([], configuredEnvVars, PowerThrottling.Undefined),
            out var cmdMainThreadHandle);

        cmdMainThreadHandle.Dispose();

        await Task.Delay(1000);

        Dictionary<string, string?> expectedEnvVars = new()
        {
            ["TESTVAR1"] = Environment.ExpandEnvironmentVariables(configuredEnvVars["TESTVAR1"]!),
            ["TESTVAR2"] = "TESTVAR2_VAL",
            ["TESTEMPTY"] = null,
        };
        foreach (var (k, v) in expectedEnvVars)
        {
            var actualEnvValue = Win32ProcessModule.GetProcessEnvironmentVariable(cmd.Handle, k);
            await Assert.That(actualEnvValue).IsEqualTo(v);
        }

        Win32ProcessModule.WaitForTheProcessToExit(cmd.Handle, cts.Token);
        if (cts.IsCancellationRequested)
        {
            Win32ProcessModule.TerminateProcess(cmd.Handle, 0);
        }
    }

    [Test]
    public async Task CmdAppAttachProcessAndUpdateJob()
    {
        using var cts = new CancellationTokenSource(10000);

        using var cmd = Win32ProcessModule.StartProcess("cmd.exe /c timeout 4", ProcessCreationFlags.Suspended,
            new(), out var cmdMainThreadHandle);
        var (pipe, monitorTask) = await SharedApi.StartMonitor(Program.DefaultMaxMonitorIdleTime, cts.Token);
        try
        {
            PInvoke.ResumeThread(cmdMainThreadHandle);
            cmdMainThreadHandle.Dispose();

            var jobName = Guid.NewGuid().ToString();
            JobSettings jobSettings = JobSettings.Empty with
            {
                MaxProcessMemory = 1024 * 1024 * 1024,
                RunMode = new RunInNamedJob(jobName)
            };

            await Program.Execute(new RunAsCmdApp(jobSettings, new AttachToProcess([cmd.Id]),
                LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.DontWaitForJobCompletion), cts.Token);

            // let's give the IOCP some time to arrive
            await Task.Delay(500);

            await Assert.That((await SharedApi.GetJobIdFromMonitor(cmd.Id, cts.Token)).GetJobName()).IsEqualTo(jobName);

            using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo((Win32JobSettings)jobSettings);

            jobSettings = JobSettings.Empty with
            {
                CpuMaxRate = 50,
                RunMode = new RunInNamedJob(jobName)
            }; // we remove the maxProcessMemory limit

            await Program.Execute(new RunAsCmdApp(jobSettings, new AttachToProcess([cmd.Id]),
                LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.DontWaitForJobCompletion), cts.Token);

            await Assert.That((await SharedApi.GetJobIdFromMonitor(cmd.Id, cts.Token)).GetJobName()).IsEqualTo(jobName);
            await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo((Win32JobSettings)jobSettings);

            cts.Cancel();

            await monitorTask;
        }
        finally
        {
            Win32ProcessModule.TerminateProcess(cmd.Handle, 0);
            pipe.Dispose();
        }
    }


    [Test]
    public async Task CmdAppLaunchProcessAndUpdateJob()
    {
        using var cts = new CancellationTokenSource(10000);

        var (pipe, monitorTask) = await SharedApi.StartMonitor(Program.DefaultMaxMonitorIdleTime, cts.Token);
        try
        {
            var jobName = Guid.NewGuid().ToString();
            var jobSettings = JobSettings.Empty with
            {
                MaxProcessMemory = 1024 * 1024 * 1024,
                MaxBandwidth = 1024 * 1024 * 1024,
                RunMode = new RunInNamedJob(jobName)
            };

            await Program.Execute(new RunAsCmdApp(jobSettings, new LaunchProcess(["cmd.exe", "/c", "timeout 10"], false),
                LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.DontWaitForJobCompletion), cts.Token);

            // let's give the IOCP some time to arrive
            await Task.Delay(500);

            using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo((Win32JobSettings)jobSettings);

            // starting new process with the same name should update job settings
            jobSettings = JobSettings.Empty with
            {
                MaxProcessMemory = jobSettings.MaxProcessMemory,
                CpuMaxRate = 50,
                RunMode = jobSettings.RunMode
            };
            await Program.Execute(new RunAsCmdApp(jobSettings, new LaunchProcess(["cmd.exe", "/c", "timeout 3"], false),
                LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.DontWaitForJobCompletion), cts.Token);

            await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo((Win32JobSettings)jobSettings);

            cts.Cancel();

            await monitorTask;
        }
        finally
        {
            pipe.Dispose();
        }
    }

    [Test]
    public async Task CmdAppLaunchProcessFreezeAndThaw()
    {
        using var cts = new CancellationTokenSource(10000);

        var (pipe, monitorTask) = await SharedApi.StartMonitor(Program.DefaultMaxMonitorIdleTime, cts.Token);
        try
        {
            var startTime = DateTime.Now;
            var jobName = Guid.NewGuid().ToString();
            var exitCode = await Program.Execute(new RunAsCmdApp(
                JobSettings.Empty with { RunMode = new RunInNamedJob(jobName) },
                new LaunchProcess(["cmd.exe", "/c", "exit 5"], false), LaunchConfig.Quiet,
                StartBehavior.Freeze, ExitBehavior.DontWaitForJobCompletion), cts.Token);
            await Assert.That(exitCode).IsEqualTo(NTSTATUS.STILL_ACTIVE.Value);

            await Task.Delay(2000, cts.Token);

            var cmd = Process.GetProcessesByName("cmd").FirstOrDefault(p => p.StartTime > startTime);
            while (!cts.IsCancellationRequested && cmd == null)
            {
                cmd = Process.GetProcessesByName("cmd").FirstOrDefault(p => p.StartTime > startTime);
            }
            Debug.Assert(cmd is not null);

            exitCode = await Program.Execute(new RunAsCmdApp(
                JobSettings.Empty with { RunMode = new RunInNamedJob(jobName) },
                new AttachToProcess([(uint)cmd.Id]), LaunchConfig.Quiet, StartBehavior.Thaw,
                ExitBehavior.WaitForJobCompletion), cts.Token);
            await Assert.That(exitCode).IsEqualTo(0);

            cts.Cancel();
            await monitorTask;
        }
        finally
        {
            pipe.Dispose();
        }
    }

    [Test]
    public async Task CmdAppLaunchProcessInEfficiencyMode()
    {
        // tests both power throttling and priority class configuration
        using var cts = new CancellationTokenSource(8000);
        var (pipe, monitorTask) = await SharedApi.StartMonitor(Program.DefaultMaxMonitorIdleTime, cts.Token);
        try
        {
            var jobSettings = JobSettings.Empty with
            {
                RunMode = new RunInNamedJob("pg-test-effmode-job"),
                PowerThrottling = PowerThrottling.On,
                PriorityClass = PriorityClass.Idle
            };

            var startTime = DateTime.Now;
            var cmdLaunchTask = Task.Run(() => Program.Execute(new RunAsCmdApp(
                jobSettings, new LaunchProcess(["cmd.exe", "/c", "timeout 5 && exit 5"], false), LaunchConfig.Quiet,
                StartBehavior.None, ExitBehavior.WaitForJobCompletion), cts.Token)
            );

            await Task.Delay(800);

            var cmd = Process.GetProcessesByName("cmd").FirstOrDefault(p => p.StartTime > startTime);
            while (!cts.IsCancellationRequested && cmd is null)
            {
                cmd = Process.GetProcessesByName("cmd").FirstOrDefault(p => p.StartTime > startTime);
            }
            await Assert.That(cmd).IsNotNull();

            using var cmdHandle = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)cmd.Id);
            await Assert.That(H.GetEfficiencySettings(cmdHandle)).IsEqualTo((PowerThrottling.On, PriorityClass.Idle));

            await Program.Execute(new RunAsCmdApp(
                jobSettings with { PowerThrottling = PowerThrottling.Auto, PriorityClass = PriorityClass.Normal },
                new AttachToProcess([(uint)cmd.Id]), LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.DontWaitForJobCompletion), cts.Token);
            await Assert.That(H.GetEfficiencySettings(cmdHandle)).IsEqualTo((PowerThrottling.Auto, PriorityClass.Normal));

            await Assert.That(await cmdLaunchTask).IsEqualTo(5);

            cts.Cancel();

            await monitorTask;
        }
        finally
        {
            pipe.Dispose();
        }
    }

    [Test]
    public async Task CmdAppAttachProcessAndUpdateEfficiencyMode()
    {
        // tests both power throttling and priority class configuration
        using var cts = new CancellationTokenSource(5000);
        var (pipe, monitorTask) = await SharedApi.StartMonitor(Program.DefaultMaxMonitorIdleTime, cts.Token);
        try
        {
            var jobSettings = JobSettings.Empty with
            {
                RunMode = new RunInNamedJob("pg-test-effmode-job"),
                PowerThrottling = PowerThrottling.On,
                PriorityClass = PriorityClass.Idle
            };

            using var cmd = Win32ProcessModule.StartProcess("cmd.exe /c timeout 4", ProcessCreationFlags.None,
                new(), out var cmdMainThreadHandle);
            cmdMainThreadHandle.Dispose();

            await Program.Execute(new RunAsCmdApp(jobSettings, new AttachToProcess([cmd.Id]),
               LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.DontWaitForJobCompletion), cts.Token);

            await Assert.That(H.GetEfficiencySettings(cmd.Handle)).IsEqualTo((PowerThrottling.On, PriorityClass.Idle));

            await Program.Execute(new RunAsCmdApp(jobSettings with { PriorityClass = PriorityClass.BelowNormal },
                new AttachToProcess([cmd.Id]), LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.DontWaitForJobCompletion), cts.Token);
            await Assert.That(H.GetEfficiencySettings(cmd.Handle)).IsEqualTo((PowerThrottling.On, PriorityClass.BelowNormal));

            var cmdAttachTask = Task.Run(() => Program.Execute(new RunAsCmdApp(
                jobSettings with { PowerThrottling = PowerThrottling.Auto, PriorityClass = PriorityClass.Normal },
                new AttachToProcess([cmd.Id]),
               LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.WaitForJobCompletion), cts.Token));
            await Task.Delay(500);
            await Assert.That(H.GetEfficiencySettings(cmd.Handle)).IsEqualTo((PowerThrottling.Auto, PriorityClass.Normal));

            // exit code is not forwarded
            await Assert.That(await cmdAttachTask).IsEqualTo(0);

            cts.Cancel();

            await monitorTask;
        }
        finally
        {
            pipe.Dispose();
        }
    }
}
