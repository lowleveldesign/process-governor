using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGovernor.Tests.Application;

public static class ServiceTests
{
    // Tests are not using NativeAOT so copying procgov.exe is not enough to install the service. We will
    // instead use the BaseDirectory and this way skip copying.
    static readonly string ServicePath = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    [Test]
    public static async Task ServiceSetupAndRemoval()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("This test requires elevated privileges");
        }

        string[] monitoredExecutablePaths = ["test1.exe", "test2.exe"];

        using var cts = new CancellationTokenSource(60000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        try
        {
            foreach (var monitoredExecutablePath in monitoredExecutablePaths)
            {
                var psi = new ProcessStartInfo(procgovExecutablePath)
                {
                    Arguments = $"--install -c 0x1 --service-path \"{ServicePath}\" {monitoredExecutablePath}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var procgov = Process.Start(psi)!;
                await procgov.WaitForExitAsync(cts.Token);
                TestContext.Out.WriteLine(await procgov.StandardOutput.ReadToEndAsync(cts.Token));
            }

            Assert.That(WindowsServiceModule.IsServiceInstalled(Program.ServiceName));

            using (var serviceControl = new ServiceController(Program.ServiceName))
            {
                Assert.That(serviceControl.Status, Is.EqualTo(ServiceControllerStatus.Running));

                // let's remove the first executable - the service should keep running
                var psi = new ProcessStartInfo(procgovExecutablePath)
                {
                    Arguments = $"--uninstall --service-path \"{ServicePath}\" {monitoredExecutablePaths[0]}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var procgov = Process.Start(psi)!;
                await procgov.WaitForExitAsync(cts.Token);
                TestContext.Out.WriteLine(await procgov.StandardOutput.ReadToEndAsync(cts.Token));

                await Task.Delay(1000, cts.Token);

                Assert.That(serviceControl.Status, Is.EqualTo(ServiceControllerStatus.Running));
            }

            {
                // remove the second executable - the service should be uninstalled
                var psi = new ProcessStartInfo(procgovExecutablePath)
                {
                    Arguments = $"--uninstall --service-path \"{ServicePath}\" {monitoredExecutablePaths[1]}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var procgov = Process.Start(psi)!;
                await procgov.WaitForExitAsync(cts.Token);
                TestContext.Out.WriteLine(await procgov.StandardOutput.ReadToEndAsync(cts.Token));
            }

            await Task.Delay(2000, cts.Token);

            Assert.That(WindowsServiceModule.IsServiceInstalled(Program.ServiceName), Is.False);
        }
        finally
        {
            if (WindowsServiceModule.IsServiceInstalled(Program.ServiceName))
            {
                await Process.Start(procgovExecutablePath, $"--uninstall-all --service-path \"{ServicePath}\"").WaitForExitAsync();
            }
        }
    }

    [Test]
    public static async Task ServiceSetupAndMonitoredProcessLaunch()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("This test requires elevated privileges");
        }

        ProcessGovernorTestContext.Initialize();

        const string monitoredExecutablePath = "winver.exe";

        using var cts = new CancellationTokenSource(90000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        try
        {
            var jobName = Program.GenerateNewJobName();
            var psi = new ProcessStartInfo(procgovExecutablePath)
            {
                Arguments = $"--install --job-name=\"{jobName}\" -c 0x1 --service-path \"{ServicePath}\" {monitoredExecutablePath}",
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var procgov = Process.Start(psi)!;
            await procgov.WaitForExitAsync(cts.Token);
            TestContext.Out.WriteLine(await procgov.StandardOutput.ReadToEndAsync(cts.Token));

            // give it some time to start (it enumerates running processes)
            await Task.Delay(2000, cts.Token);

            // the monitor should start with the first monitored process
            using var monitoredProcess = Process.Start(monitoredExecutablePath);

            // give it time to discover new processes
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            try
            {
                var defaultGroup = SharedApi.GetDefaultProcessorGroup();
                // the pipe name will be the one from the system service
                var localSystemIdentifier = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var pipeName = $"procgov-{localSystemIdentifier.Value}_elevated";
                var job = await SharedApi.TryGetJobDataFromMonitor(pipeName, (uint)monitoredProcess.Id, cts.Token);
                Assert.That(job, Is.Not.Null);

                Assert.Multiple(() =>
                {
                    var (receivedJobName, receivedJobSettings) = job.Value;
                    Assert.That(receivedJobName, Is.EqualTo(jobName));
                    Assert.That(receivedJobSettings, Is.EqualTo(new JobSettings(cpuAffinity: [new(defaultGroup.Number, 0x1)])));
                });
            }
            finally
            {
                monitoredProcess.CloseMainWindow();
                if (!monitoredProcess.WaitForExit(500))
                {
                    monitoredProcess.Kill();
                }
            }
        }
        finally
        {
            using var _ = Process.Start(procgovExecutablePath,
                $"--uninstall --service-path \"{ServicePath}\" {monitoredExecutablePath}");
        }

        await Task.Delay(2000, cts.Token);

        Assert.That(WindowsServiceModule.IsServiceInstalled(Program.ServiceName), Is.False);
    }
}
