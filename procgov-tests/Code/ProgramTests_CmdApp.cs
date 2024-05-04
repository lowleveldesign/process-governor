using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;

namespace ProcessGovernor.Tests.Code;

[TestFixture]
public static partial class ProgramTests
{

    [Test]
    public static async Task CmdAppProcessStartFailure()
    {
        using var cts = new CancellationTokenSource(10000);

        using var pipe = await StartMonitor(cts.Token);

        var exception = Assert.CatchAsync<Win32Exception>(async () =>
        {
            await Program.Execute(new RunAsCmdApp(new JobSettings(), new LaunchProcess(
                ["____wrong-executable.exe"], false), [], [], LaunchConfig.Quiet, ExitBehavior.WaitForJobCompletion), CancellationToken.None);
        });
        Assert.That(exception?.NativeErrorCode, Is.EqualTo(2));
    }

    [Test]
    public static async Task CmdAppLaunchProcessExitCodeForwarding()
    {
        using var cts = new CancellationTokenSource(10000);

        using var pipe = await StartMonitor(cts.Token);

        var exitCode = await Program.Execute(new RunAsCmdApp(new JobSettings(), new LaunchProcess(
            ["cmd.exe", "/c", "exit 5"], false), [], [], LaunchConfig.Quiet, ExitBehavior.WaitForJobCompletion), cts.Token);
        Assert.That(exitCode, Is.EqualTo(5));
    }

    [Test]
    public static async Task CmdAppAttachProcessExitCodeForwarding()
    {
        using var cts = new CancellationTokenSource(10000);

        var (cmd1, cmd1MainThreadHandle) = ProcessModule.CreateSuspendedProcess(["cmd.exe", "/c", "exit 5"], false, []);
        var (cmd2, cmd2MainThreadHandle) = ProcessModule.CreateSuspendedProcess(["cmd.exe", "/c", "exit 6"], false, []);

        var runTask = Task.Run(() => Program.Execute(new RunAsCmdApp(new JobSettings(), new AttachToProcess(
            [cmd1.Id, cmd2.Id]), [], [], LaunchConfig.Quiet | LaunchConfig.NoMonitor, ExitBehavior.WaitForJobCompletion), cts.Token));

        // give time to start for the job
        await Task.Delay(1000);

        PInvoke.ResumeThread(cmd1MainThreadHandle);
        PInvoke.ResumeThread(cmd2MainThreadHandle);

        cmd1MainThreadHandle.Dispose();
        cmd2MainThreadHandle.Dispose();

        var exitCode = await runTask;

        try
        {
            // procgov does not forward exit codes when attaching to processes
            Assert.That(exitCode, Is.EqualTo(0));
        }
        finally
        {
            cmd1.Dispose();
            cmd2.Dispose();
        }
    }

    [Test]
    public static async Task CmdAppAttachProcessAndUpdateJob()
    {
        using var cts = new CancellationTokenSource(10000);

        var (cmd, cmdMainThreadHandle) = ProcessModule.CreateSuspendedProcess(["cmd.exe"], false, []);
        try
        {
            PInvoke.ResumeThread(cmdMainThreadHandle);
            cmdMainThreadHandle.Dispose();

            // start the monitor so the run command won't hang
            var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(Program.DefaultMaxMonitorIdleTime), cts.Token));
            using (var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                while (!pipe.IsConnected && !cts.IsCancellationRequested)
                {
                    try { await pipe.ConnectAsync(cts.Token); } catch (TimeoutException) { }
                }
            }

            JobSettings jobSettings = new(maxProcessMemory: 1024 * 1024 * 1024);

            await Program.Execute(new RunAsCmdApp(jobSettings, new AttachToProcess([cmd.Id]),
                [], [], LaunchConfig.Quiet, ExitBehavior.DontWaitForJobCompletion), cts.Token);

            // let's give the IOCP some time to arrive
            await Task.Delay(500);

            Assert.That(await SharedApi.TryGetJobSettingsFromMonitor(cmd.Id, cts.Token), Is.EqualTo(jobSettings));

            jobSettings = new(maxProcessMemory: jobSettings.MaxProcessMemory, cpuMaxRate: 50);
            await Program.Execute(new RunAsCmdApp(jobSettings, new AttachToProcess([cmd.Id]),
                [], [], LaunchConfig.Quiet, ExitBehavior.DontWaitForJobCompletion), cts.Token);

            Assert.That(await SharedApi.TryGetJobSettingsFromMonitor(cmd.Id, cts.Token), Is.EqualTo(jobSettings));

            cts.Cancel();
        }
        finally
        {
            ProcessModule.TerminateProcess(cmd.Handle, 0);
            cmd.Dispose();
        }
    }

    static void UpdateProcessEnvironmentVariables(Process proc)
    {
        try
        {
            Dictionary<string, string> expectedEnvVars = new()
            {
                ["TESTVAR1"] = "TESTVAR1_VAL",
                ["TESTVAR2"] = "TESTVAR2_VAL"
            };

            ProcessModule.SetProcessEnvironmentVariables(proc.SafeHandle, expectedEnvVars);

            foreach (var (k, v) in expectedEnvVars)
            {
                var actualEnvValue = ProcessModule.GetProcessEnvironmentVariable(proc.SafeHandle, k);

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
    public static async Task CmdAppUpdateProcessEnvironmentVariablesSameBittness()
    {
        using var proc = Process.Start("winver.exe");

        await Task.Delay(1000);

        UpdateProcessEnvironmentVariables(proc);
    }

    [Test]
    public static async Task CmdAppUpdateProcessEnvironmentVariablesWow64()
    {
        if (!Environment.Is64BitProcess)
        {
            Assert.Ignore("This test makes sense for 64-bit processes only.");
        }

        using var proc = Process.Start(Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.SystemX86), "winver.exe"));

        await Task.Delay(1000);

        UpdateProcessEnvironmentVariables(proc);
    }
}
