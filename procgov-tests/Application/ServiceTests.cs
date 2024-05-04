using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGovernor.Tests.Application;

public static class ServiceTests
{
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
                    Arguments = $"--install -c 0x1 --service-path \"{Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)}\" {monitoredExecutablePath}",
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
                    Arguments = $"--uninstall --service-path \"{Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)}\" {monitoredExecutablePaths[0]}",
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
                    Arguments = $"--uninstall --service-path \"{Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)}\" {monitoredExecutablePaths[1]}",
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
                await Process.Start(procgovExecutablePath, $"--uninstall-all --service-path \"{Path.TrimEndingDirectorySeparator(
                        AppContext.BaseDirectory)}\"").WaitForExitAsync();
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
            var psi = new ProcessStartInfo(procgovExecutablePath)
            {
                Arguments = $"--install -c 0x1 --service-path \"{Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)}\" {monitoredExecutablePath}",
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
                Assert.That(await SharedApi.TryGetJobSettingsFromMonitor((uint)monitoredProcess.Id, cts.Token),
                    Is.EqualTo(new JobSettings(cpuAffinity: [new(defaultGroup.Number, 0x1)])));
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
                $"--uninstall --service-path \"{Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)}\" {monitoredExecutablePath}");
        }

        await Task.Delay(2000, cts.Token);

        Assert.That(WindowsServiceModule.IsServiceInstalled(Program.ServiceName), Is.False);
    }
}
