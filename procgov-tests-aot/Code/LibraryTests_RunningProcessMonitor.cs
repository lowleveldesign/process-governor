using ProcessGovernor.Library;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;

namespace ProcessGovernor.Tests.Code;

[ParallelGroup("LibraryTests")]
public partial class LibraryTests
{
    [Test]
    public async Task TestProcessDetection()
    {
        var unusedNotificationSink = Channel.CreateBounded<IMonitorNotification>(1);

        using var monitor = new ProcessGovernorInstance(unusedNotificationSink, null, 0);

        // set the basic snapshot
        monitor.GetNewProcessesToMonitor(Win32ProcessModule.GetRunningProcesses());

        ConfigJobId test1JobId = new("test1");
        ConfigJobId test2JobId = new("test2");
        monitor.AutoAssignJobSettings = [
            new(test1JobId, JobSettings.Empty with {
                CpuMaxRate = 50, PropagateOnChildProcesses = true, RunMode = RunModes.SharedJob }),
            new(test2JobId, JobSettings.Empty with {
                PropagateOnChildProcesses = false, RunMode = RunModes.SharedJob })
        ];

        monitor.AutoAssignProcessSettings = [
            new ("cmd.exe", test1JobId),
            new ("winver.exe", test2JobId)
        ];

        // start some new processes
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var cmd = new Process { StartInfo = startInfo };

        // Wire up asynchronous output reading to avoid deadlocks
        cmd.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null) Console.WriteLine($"> {e.Data}");
        };
        cmd.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null) Console.Error.WriteLine($"ERR: {e.Data}");
        };

        cmd.Start();

        // Begin async reading
        cmd.BeginOutputReadLine();
        cmd.BeginErrorReadLine();

        using (StreamWriter sw = cmd.StandardInput)
        {
            await Assert.That(sw.BaseStream.CanWrite).IsTrue();

            sw.WriteLine("winver.exe");
            Thread.Sleep(1000);

            var snapshot = Win32ProcessModule.GetRunningProcesses();
            var newProcesses = monitor.GetNewProcessesToMonitor(snapshot);

            await Assert.That(newProcesses).ContainsKey((uint)cmd.Id);
            await Assert.That(newProcesses[(uint)cmd.Id].Id.GetConfigId()).IsEqualTo(test1JobId);

            var (winverPid, _) = snapshot.First(kv => kv.Value.ParentId == (uint)cmd.Id &&
                "winver.exe".Equals(kv.Value.ProcessName, StringComparison.OrdinalIgnoreCase));

            await Assert.That(newProcesses[winverPid].Id.GetConfigId()).IsEqualTo(test1JobId);

            using (var winver = Process.GetProcessById((int)winverPid))
            {
                winver.CloseMainWindow();
            }

            sw.WriteLine("exit");

            using (var winver = Process.Start("winver.exe"))
            {
                Thread.Sleep(1000);

                snapshot = Win32ProcessModule.GetRunningProcesses();
                newProcesses = monitor.GetNewProcessesToMonitor(snapshot);

                await Assert.That(newProcesses[(uint)winver.Id].Id.GetConfigId()).IsEqualTo(test2JobId);

                winver.CloseMainWindow();
            }
        }

        cmd.WaitForExit();
    }

    [Test]
    [MatrixDataSource]
    public async Task TestProcessMonitorEvents([Matrix(true, false)] bool useNamedJobs)
    {
        using var cts = new CancellationTokenSource(10000);

        var channel = Channel.CreateUnbounded<IMonitorNotification>();

        ConfigJobId test2JobId = new("test2");

        NamedPipeClientStream? externalMonitorPipe = null;
        Task externalMonitorTask = Task.CompletedTask;
        if (useNamedJobs)
        {
            (externalMonitorPipe, externalMonitorTask) = await SharedApi.StartMonitor(TimeSpan.FromSeconds(2), cts.Token);
        }

        using var pg = new ProcessGovernorInstance(channel, (ct) => externalMonitorPipe, 1000, cts.Token)
        {
            // it may affect some processes already in the system so I don't want to enforce any limits
            AutoAssignJobSettings = [
                new(test2JobId, JobSettings.Empty with {
                    RunMode = useNamedJobs ? RunModes.NamedJob(test2JobId.Id) : RunModes.SharedJob,
                    Environment = [..((KeyValuePair<string, string>[])[new("VAR1", "VAL1")])]
                })
            ],
            AutoAssignProcessSettings = [new("winver.exe", test2JobId)],
            ProcessMonitorIntervalMilliseconds = 1000
        };

        // start some new processes
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        List<IMonitorNotification> expectedEvents = [];

        using (var cmd = new Process { StartInfo = startInfo })
        {
            // Wire up asynchronous output reading to avoid deadlocks
            cmd.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null) Console.WriteLine($"> {e.Data}");
            };
            cmd.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) Console.Error.WriteLine($"ERR: {e.Data}");
            };

            await Assert.That(cmd.Start()).IsTrue();

            var test1RunningJobId = new RunningJobId(useNamedJobs ?
                RunModes.NamedJob("procgov-test1") : RunModes.SharedJob, new("procgov-test1"));

            using (var test1JobHandle = pg.GetOrCreateJobObject(test1RunningJobId,
                JobSettings.Empty with { PropagateOnChildProcesses = true }, out _))
            {
                Win32JobModule.AssignProcess(test1JobHandle.Value, cmd.SafeHandle);
            }

            await Task.Delay(pg.ProcessMonitorIntervalMilliseconds * 2);

            expectedEvents.Add(new NewProcessEvent(test1RunningJobId, (uint)cmd.Id));

            cmd.BeginOutputReadLine();
            cmd.BeginErrorReadLine();

            using (StreamWriter sw = cmd.StandardInput)
            {
                await Assert.That(sw.BaseStream.CanWrite).IsTrue();

                var timestamp = DateTime.UtcNow;

                sw.WriteLine("winver.exe");
                await Task.Delay(pg.ProcessMonitorIntervalMilliseconds * 2);

                using (var winver = Process.GetProcessesByName("winver").First(p => p.StartTime.ToUniversalTime() >= timestamp))
                {
                    expectedEvents.Add(new NewProcessEvent(test1RunningJobId, (uint)winver.Id));

                    winver.CloseMainWindow();

                    await winver.WaitForExitAsync(cts.Token);

                    expectedEvents.Add(new ExitProcessEvent(test1RunningJobId, (uint)winver.Id, false));
                }

                sw.WriteLine("exit");
            }

            await cmd.WaitForExitAsync(cts.Token);

            expectedEvents.Add(new ExitProcessEvent(test1RunningJobId, (uint)cmd.Id, false));
            expectedEvents.Add(new NoProcessesInJobEvent(test1RunningJobId));
        }

        string? var1Value = null;

        using (var winver = Process.Start("winver.exe"))
        {
            await Task.Delay(pg.ProcessMonitorIntervalMilliseconds * 2);

            var1Value = Win32ProcessModule.GetProcessEnvironmentVariable(winver.SafeHandle, "VAR1");

            var test2RunMode = useNamedJobs ? RunModes.NamedJob(test2JobId.Id) : RunModes.SharedJob;
            var test2RunningJobId = new RunningJobId(test2RunMode, test2JobId);

            expectedEvents.Add(new NewJobEvent(test2RunningJobId, JobSettings.Empty with
            {
                RunMode = test2RunMode,
                Environment = [.. ((KeyValuePair<string, string>[])[new("VAR1", "VAL1")])]
            }));
            expectedEvents.Add(new NewProcessEvent(test2RunningJobId, (uint)winver.Id));

            winver.CloseMainWindow();

            await winver.WaitForExitAsync(cts.Token);

            expectedEvents.Add(new ExitProcessEvent(test2RunningJobId, (uint)winver.Id, false));
            expectedEvents.Add(new NoProcessesInJobEvent(test2RunningJobId));
        }

        await Task.Delay(pg.ProcessMonitorIntervalMilliseconds * 2);

        // the external monitor should stop when the last connection is closed
        externalMonitorPipe?.Dispose();

        await externalMonitorTask;

        cts.Cancel();

        await Assert.That(await pg.WaitForStop()).IsTrue();

        // the environment variable should be set if process is in the job
        await Assert.That(var1Value).IsEqualTo("VAL1");

        List<IMonitorNotification> receivedNotifications = [];
        while (channel.Reader.TryRead(out var n))
        {
            receivedNotifications.Add(n);
        }

        await Assert.That(receivedNotifications).Count().IsEqualTo(expectedEvents.Count);
        foreach (var expectedEvent in expectedEvents)
        {
            await Assert.That(receivedNotifications).Contains(expectedEvent);
            receivedNotifications.Remove(expectedEvent);
        }
    }
}
