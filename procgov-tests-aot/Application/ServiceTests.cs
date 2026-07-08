using Microsoft.Win32.SafeHandles;
using ProcessGovernor.Library;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using ProcessGovernorService = ProcessGovernor.Program.ProcessGovernorService;

namespace ProcessGovernor.Tests.Application;

public class ServiceTests
{
    // Tests are not using NativeAOT so copying procgov.exe is not enough to install the service. We will
    // instead use the BaseDirectory and this way skip copying.
    static readonly string ServicePath = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    [Test]
    [AdminOnly]
    public async Task ServiceSetupAndRemoval()
    {
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
                TestContext.Current!.Output.WriteLine(await procgov.StandardOutput.ReadToEndAsync(cts.Token));
            }

            await Assert.That(ProcessGovernorService.IsServiceInstalled(Program.ServiceName)).IsTrue();

            using (var serviceControl = new ServiceController(Program.ServiceName))
            {
                await Assert.That(serviceControl.Status).IsEqualTo(ServiceControllerStatus.Running);

                // let's remove the first executable - the service should keep running
                var psi = new ProcessStartInfo(procgovExecutablePath)
                {
                    Arguments = $"--uninstall --service-path \"{ServicePath}\" {monitoredExecutablePaths[0]}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var procgov = Process.Start(psi)!;
                await procgov.WaitForExitAsync(cts.Token);
                TestContext.Current!.Output.WriteLine(await procgov.StandardOutput.ReadToEndAsync(cts.Token));

                await Task.Delay(1000, cts.Token);

                await Assert.That(serviceControl.Status).IsEqualTo(ServiceControllerStatus.Running);
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
                TestContext.Current!.Output.WriteLine(await procgov.StandardOutput.ReadToEndAsync(cts.Token));
            }

            await Task.Delay(2000, cts.Token);

            await Assert.That(ProcessGovernorService.IsServiceInstalled(Program.ServiceName)).IsFalse();
        }
        finally
        {
            if (ProcessGovernorService.IsServiceInstalled(Program.ServiceName))
            {
                await Process.Start(procgovExecutablePath, $"--uninstall-all --service-path \"{ServicePath}\"").WaitForExitAsync();
            }
        }
    }

    [Test]
    [AdminOnly]
    public async Task ServiceSetupAndMonitoredProcessLaunch()
    {
        const string monitoredExecutablePath = "winver.exe";

        using var cts = new CancellationTokenSource(90000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        try
        {
            var jobName = Guid.NewGuid().ToString();
            var jobId = new RunningJobId(RunModes.NamedJob(jobName), new(jobName));
            var psi = new ProcessStartInfo(procgovExecutablePath)
            {
                Arguments = $"--install --job-name=\"{jobName}\" -c 0x1 --service-path \"{ServicePath}\" {monitoredExecutablePath}",
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var procgov = Process.Start(psi)!;
            await procgov.WaitForExitAsync(cts.Token);
            TestContext.Current!.Output.WriteLine(await procgov.StandardOutput.ReadToEndAsync(cts.Token));

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

                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await pipe.ConnectAsync(cts.Token);
                    await Assert.That(await SharedApi.GetJobIdFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token)).IsEqualTo(jobId);
                }

                // we might not have enough rights to query the job settings, so we just check if process is in a job
                await Assert.That(Win32JobModule.IsProcessInJob(monitoredProcess.SafeHandle, new SafeFileHandle())).IsTrue();
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

        await Assert.That(ProcessGovernorService.IsServiceInstalled(Program.ServiceName)).IsFalse();
    }
}
