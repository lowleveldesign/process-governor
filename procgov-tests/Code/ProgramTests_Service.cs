using Microsoft.Win32;
using NUnit.Framework.Internal;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGovernor.Tests.Code;

public static partial class ProgramTests
{
    [Test]
    public static void ServiceProcessSavedSettings()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("This test requires elevated privileges");
        }

        var settings = new Program.ProcessSavedSettings(
            JobName: $"procgov-test-{Guid.NewGuid():D}",
            JobSettings: new(
                maxProcessMemory: 100 * 1024 * 1024,
                cpuAffinity: [new(0, 0x1)],
                activeProcessLimit: 10
            ),
            Environment: new Dictionary<string, string>() {
                { "TESTVAR1", "TESTVAL1" },
                { "TESTVAR2", "TESTVAL2" }
            },
            Privileges: ["TestPriv1", "TestPriv2"]
        );

        Program.SaveProcessSettings("test.exe", settings);
        Program.SaveProcessSettings("broken.exe", new Program.ProcessSavedSettings(
            "", JobSettings: new(), Environment: [], Privileges: []));

        try
        {
            // manually break the settings
            using (var procgovKey = Registry.LocalMachine.OpenSubKey(Program.RegistrySubKeyPath))
            {
                Assert.That(procgovKey, Is.Not.Null);
                using var processKey = procgovKey!.OpenSubKey("broken.exe", true);
                Assert.That(processKey, Is.Not.Null);

                processKey!.SetValue("JobSettings", new byte[] { 1 }, RegistryValueKind.Binary);
                processKey.SetValue("Environment", new string[] { "TESTVAR1" }, RegistryValueKind.MultiString);
                processKey.SetValue("Privileges", "TestPriv", RegistryValueKind.String);
            }

            var savedSettings = Program.GetProcessesSavedSettings();

            Assert.That(savedSettings, Contains.Key("test.exe"));
            var testExeSettings = savedSettings["test.exe"];

            Assert.Multiple(() =>
            {
                Assert.That(testExeSettings.JobName, Is.EqualTo(settings.JobName));
                Assert.That(testExeSettings.JobSettings, Is.EqualTo(settings.JobSettings));
                Assert.That(testExeSettings.Environment, Is.EquivalentTo(settings.Environment));
                Assert.That(testExeSettings.Privileges, Is.EquivalentTo(settings.Privileges));
            });

            Assert.That(savedSettings, Contains.Key("broken.exe"));
            var brokenExeSettings = savedSettings["broken.exe"];
            Assert.Multiple(() =>
            {
                Assert.That(brokenExeSettings.JobName, Is.Null);
                Assert.That(brokenExeSettings.JobSettings, Is.EqualTo(new JobSettings()));
                Assert.That(brokenExeSettings.Environment, Is.EquivalentTo(ImmutableDictionary<string, string>.Empty));
                Assert.That(brokenExeSettings.Privileges, Is.EquivalentTo(ImmutableArray<string>.Empty));
            });
        }
        finally
        {
            Program.RemoveSavedProcessSettings("test.exe");
            Program.RemoveSavedProcessSettings("broken.exe");
        }
    }

    [Test]
    public static async Task ServiceNewProcessStart()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("This test requires elevated privileges");
        }

        ProcessGovernorTestContext.Initialize();

        const string executablePath = "winver.exe";

        using var cts = new CancellationTokenSource(30000);

        // start the monitor so the run command (started by service) won't hang
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(TimeSpan.FromSeconds(5), true), cts.Token));
        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token);
        Assert.That(monitorTask.IsCompleted, Is.False); // should be running

        var settings = new Program.ProcessSavedSettings(
            null,
            JobSettings: new(maxProcessMemory: 100 * 1024 * 1024, activeProcessLimit: 10),
            Environment: [],
            Privileges: []
        );

        Program.SaveProcessSettings(executablePath, settings);

        try
        {
            var svc = new Program.ProcessGovernorService();

            svc.Start();

            // give it some time to start (it enumerates running processes) and we need to make
            // sure that it will treat our newly started process as new
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            // the monitor should start with the first monitored process
            using var monitoredProcess = Process.Start(executablePath);

            // give it time to discover a new process
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds, cts.Token);

            Assert.That(monitorTask.IsCompleted, Is.False); // should be running

            try
            {
                var job = await SharedApi.TryGetJobDataFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                for (int i = 0; i < 6 && job == null; i++)
                {
                    await Task.Delay(1000);
                    job = await SharedApi.TryGetJobDataFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                }
                Assert.That(job, Is.Not.Null);

                var (jobName, jobSettings) = job.Value;

                Assert.Multiple(() =>
                {
                    Assert.That(jobName, Is.Not.Null); // it was automatically generated
                    Assert.That(jobSettings, Is.EqualTo(settings.JobSettings));
                });

                svc.Stop();
            }
            finally
            {
                monitoredProcess.CloseMainWindow();
                if (!monitoredProcess.WaitForExit(500))
                {
                    monitoredProcess.Kill();
                }
            }

            await monitorTask;
        }
        finally
        {
            Program.RemoveSavedProcessSettings("winver.exe");
        }
    }

    [Test]
    public static async Task ServiceClockTimeLimitOnProcess()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("This test requires elevated privileges");
        }

        ProcessGovernorTestContext.Initialize();

        const string executablePath = "winver.exe";

        using var cts = new CancellationTokenSource(30000);

        // start the monitor so the run command (started by service) won't hang
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(TimeSpan.FromSeconds(5), true), cts.Token));
        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token);
        Assert.That(monitorTask.IsCompleted, Is.False); // should be running

        const int WaitTimeMilliseconds = Program.ServiceProcessObserverIntervalInMilliseconds * 5;
        var settings = new Program.ProcessSavedSettings(
            JobName: Program.GenerateNewJobName(),
            JobSettings: new(clockTimeLimitInMilliseconds: WaitTimeMilliseconds),
            Environment: [],
            Privileges: []
        );

        Program.SaveProcessSettings(executablePath, settings);

        try
        {
            var svc = new Program.ProcessGovernorService();

            svc.Start();

            // give it some time to start (it enumerates running processes) and we need to make
            // sure that it will treat our newly started process as new
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            // the monitor should start with the first monitored process
            using var monitoredProcess = Process.Start(executablePath);

            // give it time to discover a new process
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds, cts.Token);

            Assert.That(monitorTask.IsCompleted, Is.False); // should be running

            try
            {
                var job = await SharedApi.TryGetJobDataFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                for (int i = 0; i < 3 && job == null; i++)
                {
                    await Task.Delay(1000);
                    job = await SharedApi.TryGetJobDataFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                }
                Assert.That(job, Is.Not.Null);

                var (jobName, jobSettings) = job.Value;

                Assert.Multiple(() =>
                {
                    Assert.That(jobName, Is.EqualTo(settings.JobName));
                    Assert.That(jobSettings, Is.EqualTo(settings.JobSettings));
                });

                await Task.Delay(WaitTimeMilliseconds);

                Assert.That(monitoredProcess.HasExited, Is.True);

                svc.Stop();
            }
            finally
            {
                monitoredProcess.Kill();
            }

            await monitorTask;
        }
        finally
        {
            Program.RemoveSavedProcessSettings("winver.exe");
        }
    }

    [Test]
    public static async Task ServiceChildProcessLimit()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("This test requires elevated privileges");
        }

        ProcessGovernorTestContext.Initialize();

        using var cts = new CancellationTokenSource(30000);

        // start the monitor so the run command (started by service) won't hang
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(TimeSpan.FromSeconds(5), true), cts.Token));
        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token);
        Assert.That(monitorTask.IsCompleted, Is.False); // should be running

        var settings = new Program.ProcessSavedSettings(
            JobName: Program.GenerateNewJobName(),
            JobSettings: new(maxProcessMemory: 100 * 1024 * 1024, propagateOnChildProcesses: true),
            Environment: [],
            Privileges: []
        );

        Program.SaveProcessSettings("cmd.exe", settings);

        try
        {
            var svc = new Program.ProcessGovernorService();

            svc.Start();

            // give it some time to start (it enumerates running processes) and we need to make
            // sure that it will treat our newly started process as new
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            const int ChildProcessTimeoutSeconds = Program.ServiceProcessObserverIntervalInMilliseconds * 3 / 1000;
            // the monitor should start with the first monitored process
            using var monitoredProcess = Process.Start(new ProcessStartInfo()
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /T {ChildProcessTimeoutSeconds} && winver.exe",
                UseShellExecute = true
            });
            Debug.Assert(monitoredProcess != null);
            Program.Logger.TraceInformation($"[test] {nameof(monitoredProcess)}.Id = {monitoredProcess.Id}");

            // give it time to discover a new process
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds, cts.Token);

            Assert.That(monitorTask.IsCompleted, Is.False); // should be running

            try
            {
                var jobName = await SharedApi.TryGetJobNameFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                for (int i = 0; i < 10 && string.IsNullOrEmpty(jobName); i++)
                {
                    await Task.Delay(1000);
                    jobName = await SharedApi.TryGetJobNameFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                }

                Assert.That(jobName, Is.EqualTo(settings.JobName));

                await Task.Delay((ChildProcessTimeoutSeconds + 1) * 1000, cts.Token);

                using var childProcess = Process.GetProcessesByName("winver").First(p => p.StartTime >= monitoredProcess.StartTime);
                Program.Logger.TraceInformation($"[test] {nameof(childProcess)}.Id = {childProcess.Id}");

                try
                {
                    var otherJobName = await SharedApi.TryGetJobNameFromMonitor(pipe, (uint)childProcess.Id, cts.Token);
                    for (int i = 0; i < 3 && otherJobName == null; i++)
                    {
                        await Task.Delay(1000);
                        otherJobName = await SharedApi.TryGetJobNameFromMonitor(pipe, (uint)childProcess.Id, cts.Token);
                    }
                    Assert.That(otherJobName, Is.EqualTo(jobName));

                    svc.Stop();
                }
                finally
                {
                    childProcess.Kill();
                }
            }
            finally
            {
                monitoredProcess.Kill();
            }

            await monitorTask;
        }
        finally
        {
            Program.RemoveSavedProcessSettings("winver.exe");
        }
    }


    [Test]
    public static async Task ServiceTwoProcessesInSameJob()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("This test requires elevated privileges");
        }

        ProcessGovernorTestContext.Initialize();

        const string winverPath = "winver.exe";
        const string cmdPath = "cmd.exe";

        using var cts = new CancellationTokenSource(30000);

        // start the monitor so the run command (started by service) won't hang
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(TimeSpan.FromSeconds(5), true), cts.Token));
        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token);
        Assert.That(monitorTask.IsCompleted, Is.False); // should be running

        var jobName = "procgov-test-winver-job";
        var settings = new Program.ProcessSavedSettings(
            JobName: jobName,
            JobSettings: new(maxProcessMemory: 100 * 1024 * 1024, activeProcessLimit: 10),
            Environment: [],
            Privileges: []
        );

        Program.SaveProcessSettings(winverPath, settings);

        // same job name for cmd
        Program.SaveProcessSettings(cmdPath, new Program.ProcessSavedSettings(
            JobName: jobName,
            JobSettings: new(),
            Environment: [],
            Privileges: []
        ));

        try
        {
            var svc = new Program.ProcessGovernorService();

            svc.Start();

            // give it some time to start (it enumerates running processes) and we need to make
            // sure that it will treat our newly started process as new
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            // the monitor should start with the first monitored process
            using var winverProcess = Process.Start(winverPath);
            using var cmdProcess = Process.Start(cmdPath);

            // give it time to discover a new process
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds, cts.Token);

            Assert.That(monitorTask.IsCompleted, Is.False); // should be running

            try
            {
                var winverJob = await SharedApi.TryGetJobDataFromMonitor(pipe, (uint)winverProcess.Id, cts.Token);
                var cmdJob = await SharedApi.TryGetJobDataFromMonitor(pipe, (uint)cmdProcess.Id, cts.Token);
                for (int i = 0; i < 3 && (cmdJob is null || winverJob is null); i++)
                {
                    await Task.Delay(1000);
                    winverJob = await SharedApi.TryGetJobDataFromMonitor(pipe, (uint)winverProcess.Id, cts.Token);
                    cmdJob = await SharedApi.TryGetJobDataFromMonitor(pipe, (uint)cmdProcess.Id, cts.Token);
                }
                Assert.Multiple(() =>
                {
                    Assert.That(winverJob, Is.Not.Null);
                    Assert.That(cmdJob, Is.Not.Null);
                });

                var (winverJobName, winverJobSettings) = winverJob.Value;
                var (cmdJobName, cmdJobSettings) = cmdJob.Value;

                Assert.Multiple(() =>
                {
                    Assert.That(winverJobName, Is.EqualTo(jobName));
                    Assert.That(cmdJobName, Is.EqualTo(jobName));
                    Assert.That(winverJobSettings, Is.EqualTo(settings.JobSettings));
                    Assert.That(cmdJobSettings, Is.EqualTo(winverJobSettings));
                });

                svc.Stop();
            }
            finally
            {
                winverProcess.CloseMainWindow();
                if (!winverProcess.WaitForExit(500))
                {
                    winverProcess.Kill();
                }
                cmdProcess.Kill();
            }

            await monitorTask;
        }
        finally
        {
            Program.RemoveSavedProcessSettings(winverPath);
            Program.RemoveSavedProcessSettings(cmdPath);
        }
    }
}
