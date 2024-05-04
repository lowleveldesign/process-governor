using NUnit.Framework.Internal;
using ProcessGovernor.Tests.Code;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGovernor.Tests.Application;

public static class CmdAppTests
{
    [Test]
    public static async Task LaunchProcess(
        [Values(
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86)
        ]Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");
        using var cts = new CancellationTokenSource(60000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        var psi = new ProcessStartInfo(procgovExecutablePath)
        {
            Arguments = $"-c 0x1 \"{executablePath}\"",
            UseShellExecute = false
        };
        using var procgov = Process.Start(psi)!;

        // give the monitor some time to process the job start event
        await Task.Delay(2000);

        var winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > procgov.StartTime);
        while (!cts.IsCancellationRequested && winver == null)
        {
            winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > procgov.StartTime);
        }
        Debug.Assert(winver is not null);

        try
        {

            // check if the monitor is running
            var settings = await SharedApi.TryGetJobSettingsFromMonitor((uint)winver.Id, cts.Token);

            var defaultGroup = SharedApi.GetDefaultProcessorGroup();
            Assert.That(settings, Is.EqualTo(new JobSettings(cpuAffinity: [new(defaultGroup.Number, 0x1)])));

        }
        finally
        {
            winver.CloseMainWindow();
            if (!winver.WaitForExit(2000))
            {
                winver.Kill();
            }
        }

        await procgov.WaitForExitAsync(cts.Token);

        // give the monitor some time to process the process exit event
        await Task.Delay(Program.DefaultMaxMonitorIdleTime.Add(TimeSpan.FromSeconds(1)), cts.Token);

        Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.False);
    }

    [Test]
    public static async Task LaunchProcessNoWaitAndUpdate(
        [Values(
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86)
        ]Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");

        using var cts = new CancellationTokenSource(60000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        var psi = new ProcessStartInfo(procgovExecutablePath)
        {
            Arguments = $"-c 0x1 -v --nowait \"{executablePath}\"",
            UseShellExecute = false
        };
        var startTime = DateTime.Now;
        using (var procgov = Process.Start(psi)!)
        {
            await procgov.WaitForExitAsync(cts.Token);
        }

        // give the monitor some time to process the job start event
        await Task.Delay(2000);

        var winver = Process.GetProcessesByName(
            Path.GetFileNameWithoutExtension(executablePath)).FirstOrDefault(p => p.StartTime > startTime);
        while (!cts.IsCancellationRequested && winver == null)
        {
            winver = Process.GetProcessesByName(
                Path.GetFileNameWithoutExtension(executablePath)).FirstOrDefault(p => p.StartTime > startTime);
        }

        Debug.Assert(winver is not null);

        try
        {
            // check if the monitor is running
            var settings = await SharedApi.TryGetJobSettingsFromMonitor((uint)winver.Id, cts.Token);

            var defaultGroup = SharedApi.GetDefaultProcessorGroup();
            Assert.That(settings, Is.EqualTo(new JobSettings(cpuAffinity: [new(defaultGroup.Number, 0x1)])));

            // try to update the job settings
            psi = new ProcessStartInfo(procgovExecutablePath)
            {
                Arguments = $"-c 0x3 -v --nowait --pid {winver.Id}",
                UseShellExecute = false,
            };
            using (var procgov = Process.Start(psi)!)
            {
                await procgov.WaitForExitAsync(cts.Token);
            }

            // check if settings were updated
            settings = await SharedApi.TryGetJobSettingsFromMonitor((uint)winver.Id, cts.Token);
            Assert.That(settings, Is.EqualTo(new JobSettings(cpuAffinity: [new(defaultGroup.Number, defaultGroup.AffinityMask & 0x3)])));

        }
        finally
        {
            winver.CloseMainWindow();
            if (!winver.WaitForExit(2000))
            {
                winver.Kill();
            }
        }

        // give the monitor some time to process the process exit event
        await Task.Delay(Program.DefaultMaxMonitorIdleTime + TimeSpan.FromSeconds(1), cts.Token);

        Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.False);
    }

    [Test]
    public static async Task AttachToProcess(
        [Values(
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86)
        ]Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");

        using var cts = new CancellationTokenSource(60000);
        using var winver = Process.Start(executablePath)!;

        try
        {
            await Task.Delay(1000);

            var defaultGroup = SharedApi.GetDefaultProcessorGroup();

            TestContext.Out.WriteLine($"winver PID: {winver.Id}");

            using (var procgov = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "procgov.exe"))
            {
                Arguments = $"-c 0x1 --nowait -p \"{winver.Id}\"",
                UseShellExecute = false
            })!)
            {
                await procgov.WaitForExitAsync(cts.Token);

                var settings = await SharedApi.TryGetJobSettingsFromMonitor((uint)winver.Id, cts.Token);
                Assert.That(settings, Is.EqualTo(new JobSettings(cpuAffinity: [new(defaultGroup.Number, 0x1)])));
            }

            // update the job settings
            using (var procgov = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "procgov.exe"))
            {
                Arguments = $"-c 0x2 --nowait -p \"{winver.Id}\"",
                UseShellExecute = false
            })!)
            {
                await procgov.WaitForExitAsync(cts.Token);

                // check if the monitor is running
                var settings = await SharedApi.TryGetJobSettingsFromMonitor((uint)winver.Id, cts.Token);
                Assert.That(settings, Is.EqualTo(new JobSettings(cpuAffinity: [new(defaultGroup.Number, defaultGroup.AffinityMask & 0x2)])));
            }
        }
        finally
        {
            winver.CloseMainWindow();
            if (!winver.WaitForExit(2000))
            {
                winver.Kill();
            }
        }

        // give the monitor some time to process the process exit event
        await Task.Delay(Program.DefaultMaxMonitorIdleTime + TimeSpan.FromSeconds(1), cts.Token);

        Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.False);
    }

    [Test]
    public static async Task AttachToMultipleProcesses(
        [Values(
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86)
        ]Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");

        using var cts = new CancellationTokenSource(60000);

        using var winver1 = Process.Start(executablePath)!;
        using var winver2 = Process.Start(executablePath)!;

        await Task.Delay(2000);

        using var procgov = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "procgov.exe"))
        {
            Arguments = $"-c 0x1 -p {winver1.Id} -p {winver2.Id}",
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;

        try
        {
            _ = procgov.StandardOutput.ReadToEndAsync(cts.Token).ContinueWith(s => TestContext.Out.WriteLine(s.Result), cts.Token);

            // give the monitor some time to process the job start event
            await Task.Delay(2000);

            // check if the monitor is running
            var settings = await SharedApi.TryGetJobSettingsFromMonitor((uint)winver1.Id, cts.Token);

            var defaultGroup = SharedApi.GetDefaultProcessorGroup();
            Assert.That(settings, Is.EqualTo(new JobSettings(cpuAffinity: [new(defaultGroup.Number, 0x1)])));

        }
        finally
        {
            winver1.CloseMainWindow();
            if (!winver1.WaitForExit(2000))
            {
                winver1.Kill();
            }

            Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.True);

            winver2.CloseMainWindow();
            if (!winver2.WaitForExit(2000))
            {
                winver2.Kill();
            }
        }

        await procgov.WaitForExitAsync(cts.Token);

        // give the monitor some time to process the process exit event
        await Task.Delay(Program.DefaultMaxMonitorIdleTime + TimeSpan.FromSeconds(1), cts.Token);

        Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.False);
    }
}
