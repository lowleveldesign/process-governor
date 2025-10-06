using Microsoft.Win32.SafeHandles;
using NUnit.Framework.Internal;
using ProcessGovernor.Library;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;

namespace ProcessGovernor.Tests.Application;

public static class CmdAppTests
{
    private static RunningJobId GenerateNewNamedJobId() =>
        new(RunModes.NamedJob($"procgov-{Guid.NewGuid():D}"), new(""));

    static CmdAppTests()
    {
        SharedApi.InitializeTestContext();
    }

    [Test]
    public static async Task LaunchProcessInNamedJob(
        [Values(
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86)
        ]Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");
        using var cts = new CancellationTokenSource(60000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        var jobId = GenerateNewNamedJobId();
        Assert.That(jobId.IsNamedJob, Is.True);
        string jobName = jobId.GetJobName()!;

        var psi = new ProcessStartInfo(procgovExecutablePath)
        {
            Arguments = $"--job-name=\"{jobName}\" -c 0x1 \"{executablePath}\"",
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
            Assert.That(await SharedApi.GetJobIdFromMonitor((uint)winver.Id, cts.Token), Is.EqualTo(jobId));

            var defaultGroup = SharedApi.GetDefaultProcessorGroup();

            using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            Assert.That(Win32JobModule.QueryJobSettings(jobHandle), Is.EqualTo(
                    (Win32JobSettings)(JobSettings.Empty with { CpuAffinity = [new(defaultGroup.Number, 0x1)] })));
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
    public static async Task LaunchProcessInAnonymousJob(
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

        // no monitor should be running
        Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.False);

        var winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > procgov.StartTime);
        while (!cts.IsCancellationRequested && winver == null)
        {
            winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > procgov.StartTime);
        }
        Debug.Assert(winver is not null);

        try
        {
            Assert.That(Win32JobModule.IsProcessInJob(winver.SafeHandle, new SafeFileHandle()));
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
    }


    [Test]
    public static async Task LaunchProcessInNamedJobAndUpdate(
        [Values(
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86)
        ]Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");

        using var cts = new CancellationTokenSource(60000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        var jobId = GenerateNewNamedJobId();
        Assert.That(jobId.IsNamedJob, Is.True);
        string jobName = jobId.GetJobName()!;

        var psi = new ProcessStartInfo(procgovExecutablePath)
        {
            Arguments = $"--job-name {jobName} -c 0x1 -v --nowait \"{executablePath}\"",
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
            Assert.That(await SharedApi.GetJobIdFromMonitor((uint)winver.Id, cts.Token), Is.EqualTo(jobId));

            var defaultGroup = SharedApi.GetDefaultProcessorGroup();

            using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            Assert.That(Win32JobModule.QueryJobSettings(jobHandle), Is.EqualTo(
                    (Win32JobSettings)(JobSettings.Empty with { CpuAffinity = [new(defaultGroup.Number, 0x1)] })));

            // try to update the job settings
            psi = new ProcessStartInfo(procgovExecutablePath)
            {
                Arguments = $"--job-name {jobName} -c 0x3 -v --nowait --pid {winver.Id}",
                UseShellExecute = false,
            };
            using (var procgov = Process.Start(psi)!)
            {
                await procgov.WaitForExitAsync(cts.Token);
            }

            // check if settings were updated
            Assert.That(Win32JobModule.QueryJobSettings(jobHandle), Is.EqualTo(
                    (Win32JobSettings)(JobSettings.Empty with
                    {
                        CpuAffinity = [new(defaultGroup.Number, defaultGroup.AffinityMask & 0x3)]
                    })));
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
        await Task.Delay(Program.DefaultMaxMonitorIdleTime + TimeSpan.FromSeconds(3), cts.Token);

        Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.False);
    }

    [Test]
    public static async Task AttachProcessToAnonymousJob(
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
            var defaultGroup = SharedApi.GetDefaultProcessorGroup();

            TestContext.Out.WriteLine($"winver PID: {winver.Id}");

            using var procgov = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "procgov.exe"))
            {
                Arguments = $"-c 0x1 --nowait -p \"{winver.Id}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true
            })!;

            _ = procgov.StandardOutput.ReadToEndAsync(cts.Token).ContinueWith(t => TestContext.Out.WriteLine(t.Result), cts.Token);

            await procgov.WaitForExitAsync(cts.Token);

            // no monitor should be running
            Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.False);

            Assert.That(Win32JobModule.IsProcessInJob(winver.SafeHandle, new SafeFileHandle()));
        }
        finally
        {
            winver.CloseMainWindow();
            if (!winver.WaitForExit(2000))
            {
                winver.Kill();
            }
        }
    }

    [Test]
    public static async Task AttachMultipleProcessesToNamedJob(
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

        var jobId = GenerateNewNamedJobId();
        Assert.That(jobId.IsNamedJob, Is.True);
        string jobName = jobId.GetJobName()!;

        using var procgov = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "procgov.exe"))
        {
            Arguments = $"--job-name {jobName} -c 0x1 -p {winver1.Id} -p {winver2.Id}",
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;

        try
        {
            _ = procgov.StandardOutput.ReadToEndAsync(cts.Token).ContinueWith(t => TestContext.Out.WriteLine(t.Result), cts.Token);

            // give the monitor some time to process the job start event
            await Task.Delay(2000);

            // check if the monitor is running
            Assert.That(await SharedApi.GetJobIdFromMonitor((uint)winver1.Id, cts.Token), Is.EqualTo(jobId));

            using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            var defaultGroup = SharedApi.GetDefaultProcessorGroup();
            Assert.That(Win32JobModule.QueryJobSettings(jobHandle), Is.EqualTo(
                (Win32JobSettings)(JobSettings.Empty with { CpuAffinity = [new(defaultGroup.Number, 0x1)] })));

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
