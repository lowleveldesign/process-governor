using Microsoft.Win32.SafeHandles;
using ProcessGovernor.Library;
using System.Diagnostics;
using Windows.Win32;

namespace ProcessGovernor.Tests.Application;

public class CmdAppTests
{
    private static RunningJobId GenerateNewNamedJobId() =>
        new(RunModes.NamedJob($"procgov-{Guid.NewGuid():D}"), new(""));

    [Test]
    [MatrixDataSource]
    public async Task LaunchProcessInNamedJob(
        [Matrix(Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86)] Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");
        using var cts = new CancellationTokenSource(60000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        var jobId = GenerateNewNamedJobId();
        await Assert.That(jobId.IsNamedJob).IsTrue();
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
            await Assert.That(await SharedApi.GetJobIdFromMonitor((uint)winver.Id, cts.Token)).IsEqualTo(jobId);

            var defaultGroup = SharedApi.GetDefaultProcessorGroup();

            using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo(
                    (Win32JobSettings)(JobSettings.Empty with { CpuAffinity = [new(defaultGroup.Number, 0x1)] }));
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

        await Assert.That(await SharedApi.IsMonitorListening(cts.Token)).IsFalse();
    }

    [Test]
    [MatrixDataSource]
    public async Task LaunchProcessInAnonymousJob(
        [Matrix(Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86)] Environment.SpecialFolder systemFolder)
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
        await Assert.That(await SharedApi.IsMonitorListening(cts.Token)).IsFalse();

        var winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > procgov.StartTime);
        while (!cts.IsCancellationRequested && winver == null)
        {
            winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > procgov.StartTime);
        }
        Debug.Assert(winver is not null);

        try
        {
            await Assert.That(Win32JobModule.IsProcessInJob(winver.SafeHandle, new SafeFileHandle())).IsTrue();
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
    [MatrixDataSource]
    public async Task LaunchProcessInNamedJobAndUpdate(
        [Matrix(Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86)] Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");

        using var cts = new CancellationTokenSource(60000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        var jobId = GenerateNewNamedJobId();
        await Assert.That(jobId.IsNamedJob).IsTrue();
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
            await Assert.That(await SharedApi.GetJobIdFromMonitor((uint)winver.Id, cts.Token)).IsEqualTo(jobId);

            var defaultGroup = SharedApi.GetDefaultProcessorGroup();

            using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo((Win32JobSettings)(JobSettings.Empty with
            {
                CpuAffinity = [new(defaultGroup.Number, 0x1)]
            }));

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
            await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo((Win32JobSettings)(JobSettings.Empty with
            {
                CpuAffinity = [new(defaultGroup.Number, defaultGroup.AffinityMask & 0x3)]
            }));
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

        await Assert.That(await SharedApi.IsMonitorListening(cts.Token)).IsFalse();
    }

    [Test]
    [MatrixDataSource]
    public async Task AttachProcessToAnonymousJob(
        [Matrix(Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86)] Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");

        using var cts = new CancellationTokenSource(60000);
        using var winver = Process.Start(executablePath)!;

        try
        {
            var defaultGroup = SharedApi.GetDefaultProcessorGroup();

            TestContext.Current!.Output.WriteLine($"winver PID: {winver.Id}");

            using var procgov = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "procgov.exe"))
            {
                Arguments = $"-c 0x1 --nowait -p \"{winver.Id}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true
            })!;

            _ = procgov.StandardOutput.ReadToEndAsync(cts.Token).ContinueWith(t => TestContext.Current!.Output.WriteLine(t.Result), cts.Token);

            await procgov.WaitForExitAsync(cts.Token);

            // no monitor should be running
            await Assert.That(await SharedApi.IsMonitorListening(cts.Token)).IsFalse();

            await Assert.That(Win32JobModule.IsProcessInJob(winver.SafeHandle, new SafeFileHandle())).IsTrue();
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
    [MatrixDataSource]
    public async Task AttachMultipleProcessesToNamedJob(
        [Matrix(Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86)] Environment.SpecialFolder systemFolder)
    {
        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");

        using var cts = new CancellationTokenSource(60000);

        using var winver1 = Process.Start(executablePath)!;
        using var winver2 = Process.Start(executablePath)!;

        await Task.Delay(2000);

        var jobId = GenerateNewNamedJobId();
        await Assert.That(jobId.IsNamedJob).IsTrue();
        string jobName = jobId.GetJobName()!;

        using var procgov = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "procgov.exe"))
        {
            Arguments = $"--job-name {jobName} -c 0x1 -p {winver1.Id} -p {winver2.Id}",
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;

        try
        {
            _ = procgov.StandardOutput.ReadToEndAsync(cts.Token).ContinueWith(t => TestContext.Current!.Output.WriteLine(t.Result), cts.Token);

            // give the monitor some time to process the job start event
            await Task.Delay(2000);

            // check if the monitor is running
            await Assert.That(await SharedApi.GetJobIdFromMonitor((uint)winver1.Id, cts.Token)).IsEqualTo(jobId);

            using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            var defaultGroup = SharedApi.GetDefaultProcessorGroup();
            await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo((Win32JobSettings)(JobSettings.Empty with
            {
                CpuAffinity = [new(defaultGroup.Number, 0x1)]
            }));

        }
        finally
        {
            winver1.CloseMainWindow();
            if (!winver1.WaitForExit(2000))
            {
                winver1.Kill();
            }

            await Assert.That(await SharedApi.IsMonitorListening(cts.Token)).IsTrue();

            winver2.CloseMainWindow();
            if (!winver2.WaitForExit(2000))
            {
                winver2.Kill();
            }
        }

        await procgov.WaitForExitAsync(cts.Token);

        // give the monitor some time to process the process exit event
        await Task.Delay(Program.DefaultMaxMonitorIdleTime + TimeSpan.FromSeconds(1), cts.Token);

        await Assert.That(await SharedApi.IsMonitorListening(cts.Token)).IsFalse();
    }
}
