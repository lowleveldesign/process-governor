using ProcessGovernor.Library;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Win32ProcessModule = ProcessGovernor.Library.Win32ProcessModule;

namespace ProcessGovernor.Tests.Code;

public static partial class ProgramTests
{

    [Test]
    public static async Task CmdAppProcessStartFailure()
    {
        var exception = Assert.CatchAsync<Win32Exception>(async () =>
        {
            await Program.Execute(new RunAsCmdApp(JobSettings.Empty,
                new LaunchProcess(["____wrong-executable.exe"], false), LaunchConfig.Quiet, StartBehavior.None,
                ExitBehavior.WaitForJobCompletion),
                CancellationToken.None);
        });
        Assert.That(exception?.NativeErrorCode, Is.EqualTo(2));
    }

    [Test]
    public static async Task CmdAppLaunchProcessExitCodeForwarding()
    {
        var exitCode = await Program.Execute(new RunAsCmdApp(JobSettings.Empty,
            new LaunchProcess(["cmd.exe", "/c", "exit 5"], false), LaunchConfig.Quiet, StartBehavior.None,
            ExitBehavior.WaitForJobCompletion), CancellationToken.None);
        Assert.That(exitCode, Is.EqualTo(5));
    }

    [Test]
    public static async Task CmdAppLaunchProcessWithEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("TESTEMPTY", "SOMETHING");

        using var cts = new CancellationTokenSource(7000);
        ImmutableDictionary<string, string> configuredEnvVars = [
            new("TESTVAR1", "TESTVAR1_VAL;%USERPROFILE%"),
            new ("TESTVAR2", "TESTVAR2_VAL"),
            new("TESTEMPTY", "")
        ];

        using var cmd = Win32ProcessModule.StartProcess("cmd.exe /c timeout 4",
            ProcessCreationFlags.None, new Win32ProcessSettings([], configuredEnvVars, EfficiencyMode.Undefined),
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
            Assert.That(actualEnvValue, Is.EqualTo(v));
        }

        Win32ProcessModule.WaitForTheProcessToExit(cmd.Handle, cts.Token);
        if (cts.IsCancellationRequested)
        {
            Win32ProcessModule.TerminateProcess(cmd.Handle, 0);
        }
    }

    [Test]
    public static async Task CmdAppAttachProcessExitCodeForwarding()
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
        Assert.That(exitCode, Is.EqualTo(0));
    }

    [Test]
    public static async Task CmdAppAttachProcessAndUpdateJob()
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

            Assert.That((await SharedApi.GetJobIdFromMonitor(cmd.Id, cts.Token)).GetJobName(), Is.EqualTo(jobName));

            using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            Assert.That(Win32JobModule.QueryJobSettings(jobHandle), Is.EqualTo((Win32JobSettings)jobSettings));

            jobSettings = JobSettings.Empty with
            {
                CpuMaxRate = 50,
                RunMode = new RunInNamedJob(jobName)
            }; // we remove the maxProcessMemory limit

            await Program.Execute(new RunAsCmdApp(jobSettings, new AttachToProcess([cmd.Id]),
                LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.DontWaitForJobCompletion), cts.Token);

            Assert.That((await SharedApi.GetJobIdFromMonitor(cmd.Id, cts.Token)).GetJobName(), Is.EqualTo(jobName));
            Assert.That(Win32JobModule.QueryJobSettings(jobHandle), Is.EqualTo((Win32JobSettings)jobSettings));

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
    public static async Task CmdAppLaunchProcessAndUpdateJob()
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
            Assert.That(Win32JobModule.QueryJobSettings(jobHandle), Is.EqualTo((Win32JobSettings)jobSettings));

            // starting new process with the same name should update job settings
            jobSettings = JobSettings.Empty with
            {
                MaxProcessMemory = jobSettings.MaxProcessMemory,
                CpuMaxRate = 50,
                RunMode = jobSettings.RunMode
            };
            await Program.Execute(new RunAsCmdApp(jobSettings, new LaunchProcess(["cmd.exe", "/c", "timeout 3"], false),
                LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.DontWaitForJobCompletion), cts.Token);

            Assert.That(Win32JobModule.QueryJobSettings(jobHandle), Is.EqualTo((Win32JobSettings)jobSettings));

            cts.Cancel();

            await monitorTask;
        }
        finally
        {
            pipe.Dispose();
        }
    }

    [Test]
    public static async Task CmdAppUpdateProcessEnvironmentVariables(
        [Values(
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86)
        ]Environment.SpecialFolder systemFolder)
    {
        Environment.SetEnvironmentVariable("TESTEMPTY", "SOMETHING");

        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");
        using var proc = Process.Start(executablePath);

        await Task.Delay(1000);

        try
        {
            Dictionary<string, string> configuredEnvVars = new()
            {
                ["TESTVAR1"] = "TESTVAR1_VAL;%USERPROFILE%",
                ["TESTVAR2"] = "TESTVAR2_VAL",
                ["TESTEMPTY"] = ""
            };


            Dictionary<string, string?> expectedEnvVars = new()
            {
                ["TESTVAR1"] = Environment.ExpandEnvironmentVariables(configuredEnvVars["TESTVAR1"]!),
                ["TESTVAR2"] = "TESTVAR2_VAL",
                ["TESTEMPTY"] = null
            };

            Win32ProcessModule.SetProcessEnvironmentVariables(proc.SafeHandle, configuredEnvVars);

            foreach (var (k, v) in expectedEnvVars)
            {
                var actualEnvValue = Win32ProcessModule.GetProcessEnvironmentVariable(proc.SafeHandle, k);

                Assert.That(actualEnvValue, Is.EqualTo(v));
            }
        }
        finally
        {
            proc.CloseMainWindow();

            if (!proc.WaitForExit(2000))
            {
                proc.Kill();
            }
        }
    }

    [Test]
    public static async Task CmdAppLaunchProcessFreezeAndThaw()
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
            Assert.That(exitCode, Is.EqualTo(NTSTATUS.STILL_ACTIVE.Value));

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
            Assert.That(exitCode, Is.EqualTo(0));

            cts.Cancel();
            await monitorTask;
        }
        finally
        {
            pipe.Dispose();
        }
    }

    [Test]
    public static async Task CmdAppLaunchProcessWithLimits()
    {
        using var cts = new CancellationTokenSource(5000);

        var startTime = DateTime.Now;
        var cmdLaunchTask = Task.Run(() => Program.Execute(new RunAsCmdApp(
            JobSettings.Empty with
            {
                RunMode = new RunInAnonymousSharedJob(),
                EfficiencyMode = EfficiencyMode.On
            },
            new LaunchProcess(["cmd.exe", "/c", "timeout 3 && exit 5"], false), LaunchConfig.Quiet,
            StartBehavior.None, ExitBehavior.WaitForJobCompletion), cts.Token)
        );

        await Task.Delay(1000, cts.Token);

        var cmd = Process.GetProcessesByName("cmd").FirstOrDefault(p => p.StartTime > startTime);
        while (!cts.IsCancellationRequested && cmd is null)
        {
            cmd = Process.GetProcessesByName("cmd").FirstOrDefault(p => p.StartTime > startTime);
        }
        Debug.Assert(cmd is not null);

        var cmdHandle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)cmd.Id);
        try
        {
            Assert.That(GetEfficiencyMode(cmdHandle), Is.EqualTo(EfficiencyMode.On));
        }
        finally
        {
            PInvoke.CloseHandle(cmdHandle);
        }

        Assert.That(await cmdLaunchTask, Is.EqualTo(5));

        cts.Cancel();

        // Helpers (CmdAppLaunchProcessWithLimits)

        unsafe static EfficiencyMode GetEfficiencyMode(HANDLE processHandle)
        {
            PROCESS_POWER_THROTTLING_STATE state = new()
            {
                Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            };
            if (!PInvoke.GetProcessInformation(processHandle, PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, &state,
                (uint)sizeof(PROCESS_POWER_THROTTLING_STATE)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return state.ControlMask == 0 ? EfficiencyMode.Auto : (state.StateMask != 0 ? EfficiencyMode.On : EfficiencyMode.Off);
        }

    }
}
