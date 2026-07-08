using Nerdbank.MessagePack;
using ProcessGovernor.Library;
using System.Buffers;
using System.ComponentModel;
using System.IO.Pipes;
using Windows.Win32;
using Windows.Win32.Foundation;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

namespace ProcessGovernor.Tests.Code;

public partial class ProgramTests
{
    [Test]
    public async Task MonitorNoProcessStarted()
    {
        using var cts = new CancellationTokenSource(10000);

        TimeSpan maxIdleTime = TimeSpan.FromSeconds(2);

        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(maxIdleTime, false), cts.Token));

        using (var pipe = new NamedPipeClientStream(".", ProcessGovernorLibraryApi.DefaultPipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous))
        {

            // is monitor already running?
            while (!pipe.IsConnected && !cts.IsCancellationRequested)
            {
                try { await pipe.ConnectAsync(cts.Token); } catch { }
            }
        }

        await Task.Delay(maxIdleTime + TimeSpan.FromSeconds(1), cts.Token);

        await Assert.That(monitorTask.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task MonitorStartExitProcessEvents()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = JobSettings.Empty with { PropagateOnChildProcesses = false };
        var jobName = Guid.NewGuid().ToString();
        var jobId = new RunningJobId(RunModes.NamedJob(jobName), new(jobName));

        using var jobHandle = Win32JobModule.CreateJob(jobName);

        Win32JobModule.SetLimits(jobHandle, jobSettings);

        int notificationNumber = 0;
        async Task NotificationCheck(IMonitorNotification resp)
        {
            switch (resp)
            {
                case NewProcessEvent ev:
                    await Assert.That(notificationNumber).IsEqualTo(0);
                    await Assert.That(ev.JobId).IsEqualTo(jobId);
                    break;
                case ExitProcessEvent ev:
                    await Assert.That(notificationNumber).IsEqualTo(1);
                    await Assert.That(ev.JobId).IsEqualTo(jobId);
                    break;
                case NoProcessesInJobEvent ev:
                    await Assert.That(notificationNumber).IsEqualTo(2);
                    await Assert.That(ev.JobId).IsEqualTo(jobId);
                    break;
                default:
                    Assert.Fail($"Unexpected event: {resp}");
                    break;
            }
            notificationNumber++;
        }

        await H.StartAndAssignProcessToNamedJob(jobHandle, jobId, "cmd.exe /c \"exit 1\"", NotificationCheck, cts.Token);

        // job is not signaled
        await Assert.That(PInvoke.WaitForSingleObject(jobHandle, 0)).IsEqualTo(WAIT_EVENT.WAIT_TIMEOUT);

        await Assert.That(notificationNumber).IsEqualTo(3);
    }

    [Test]
    public async Task MonitorProcessStartFailure()
    {
        var maxMonitorIdleTime = TimeSpan.FromSeconds(2);

        // 2x because the first timeout is counted for the broken job and then next timeout for empty job collection
        // + 3s because we have the timeout when waiting for the write operation to complete
        using var cts = new CancellationTokenSource(2 * maxMonitorIdleTime + TimeSpan.FromSeconds(3));

        var (pipe, monitorTask) = await SharedApi.StartMonitor(maxMonitorIdleTime, cts.Token);

        var jobSettings = JobSettings.Empty with { PropagateOnChildProcesses = false };

        var jobName = Guid.NewGuid().ToString();
        var jobId = new RunningJobId(RunModes.NamedJob(jobName), new(jobName));

        using var jobHandle = Win32JobModule.CreateJob(jobName);

        Win32JobModule.SetLimits(jobHandle, jobSettings);


        // start monitoring our job
        await MsgPackSerializer.SerializeAsync<IMonitorRequest>(pipe, new MonitorJobReq(jobId), cancellationToken: cts.Token);

        var buffer = new ArrayBufferWriter<byte>(1024);
        var readBytes = await pipe.ReadAsync(buffer.GetMemory(), cts.Token);
        buffer.Advance(readBytes);

        var msgPackReader = new MessagePackReader(buffer.WrittenMemory);
        var resp = MsgPackSerializer.Deserialize<IMonitorResponse>(ref msgPackReader, cts.Token);
        await Assert.That(resp is AckResp { IsSuccess: true }).IsTrue();
        buffer.ResetWrittenCount();

        Assert.Throws<Win32Exception>(() =>
        {
            Win32ProcessModule.StartProcessInJob("non_existing.exe", ProcessCreationFlags.None, new(), jobHandle);
        });

        pipe.Dispose();

        await Assert.That(Task.WaitAny([monitorTask], cts.Token) >= 0).IsTrue();
    }

    [Test]
    public async Task MonitorStartExitProcessWithoutNotifications()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = JobSettings.Empty with { PropagateOnChildProcesses = false };
        var jobName = Guid.NewGuid().ToString();
        var jobId = new RunningJobId(RunModes.NamedJob(jobName), new(jobName));

        using var jobHandle = Win32JobModule.CreateJob(jobName);

        Win32JobModule.SetLimits(jobHandle, jobSettings);

        await H.StartAndAssignProcessToNamedJob(jobHandle, jobId, "cmd.exe /c \"exit 1\"", null, cts.Token);

        // job is not signaled
        await Assert.That(PInvoke.WaitForSingleObject(jobHandle, 0)).IsEqualTo(WAIT_EVENT.WAIT_TIMEOUT);
    }

    [Test]
    public async Task MonitorTerminateJobEvents()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = JobSettings.Empty with { JobClockTimeLimitInMilliseconds = 2000, PropagateOnChildProcesses = false };
        var jobName = Guid.NewGuid().ToString();
        var jobId = new RunningJobId(RunModes.NamedJob(jobName), new(jobName));
        using var jobHandle = Win32JobModule.CreateJob(jobName);

        Win32JobModule.SetLimits(jobHandle, jobSettings);

        int notificationNumber = 0;
        async Task NotificationCheck(IMonitorNotification resp)
        {
            switch (resp)
            {
                case NewProcessEvent ev:
                    await Assert.That(notificationNumber).IsEqualTo(0);
                    await Assert.That(ev.JobId).IsEqualTo(jobId);
                    break;
                case NoProcessesInJobEvent ev:
                    // we are not getting the usual process exit event when job is terminated
                    await Assert.That(notificationNumber).IsEqualTo(1);
                    await Assert.That(ev.JobId).IsEqualTo(jobId);
                    break;
                default:
                    Assert.Fail($"Unexpected event: {resp}");
                    break;
            }
            notificationNumber++;
        }

        var monitorTask = Task.CompletedTask;
        try
        {
            monitorTask = H.StartAndAssignProcessToNamedJob(jobHandle, jobId, "winver.exe", NotificationCheck, cts.Token);

            await Task.Delay((int)jobSettings.JobClockTimeLimitInMilliseconds);
        }
        finally
        {
            Win32JobModule.TerminateJob(jobHandle, 0);
        }

        // monitor should terminate with the job
        await monitorTask;

        // job should albo be signaled
        await Assert.That(PInvoke.WaitForSingleObject(jobHandle, 0)).IsEqualTo(WAIT_EVENT.WAIT_OBJECT_0);

        await Assert.That(notificationNumber).IsEqualTo(2);
    }

    [Test]
    public async Task MonitorActiveProcessNumberExceeded()
    {
        const int maxActiveProcessLimit = 3;
        using var cts = new CancellationTokenSource(15000);

        var jobSettings = JobSettings.Empty with { ActiveProcessLimit = maxActiveProcessLimit, PropagateOnChildProcesses = true };
        var jobName = Guid.NewGuid().ToString();
        var jobId = new RunningJobId(RunModes.NamedJob(jobName), new(jobName));
        using var jobHandle = Win32JobModule.CreateJob(jobName);

        Win32JobModule.SetLimits(jobHandle, jobSettings);

        async Task NotificationCheck(IMonitorNotification resp)
        {
            switch (resp)
            {
                case NewProcessEvent ev:
                    await Assert.That(ev.JobId).IsEqualTo(jobId);
                    break;
                case JobLimitExceededEvent jobLimit:
                    await Assert.That(jobLimit.ExceededLimit).IsEqualTo(LimitType.ActiveProcessNumber);
                    await Assert.That(jobLimit.JobId).IsEqualTo(jobId);

                    // The job does not allow more processes to be created and is still active. Therefore,
                    // we need to stop the monitor manually.
                    cts.Cancel();
                    break;
                default:
                    Assert.Fail($"Unexpected event: {resp}");
                    break;
            }
        }

        var batchFilePath = Path.GetTempFileName() + ".bat";
        await File.WriteAllLinesAsync(batchFilePath, Enumerable.Range(0, maxActiveProcessLimit + 1).Select(_ => "start winver.exe"));
        try
        {
            await H.StartAndAssignProcessToNamedJob(jobHandle, jobId, $"cmd.exe /c \"{batchFilePath}\"", NotificationCheck, cts.Token);
        }
        catch (TaskCanceledException) { }
        finally
        {
            Win32JobModule.TerminateJob(jobHandle, 0);
            File.Delete(batchFilePath);
        }

        // job is not signaled
        await Assert.That(PInvoke.WaitForSingleObject(jobHandle, 0)).IsEqualTo(WAIT_EVENT.WAIT_TIMEOUT);
    }

    [Test]
    public async Task MonitorProcessMemoryLimitExceeded()
    {
        using var cts = new CancellationTokenSource(10000);

        var testLimitPath = await H.DownloadSysinternalsTestLimit(cts.Token);

        // memory limit per process is 2MB
        var jobSettings = JobSettings.Empty with { MaxProcessMemory = 500 * 1024 * 1024, PropagateOnChildProcesses = true };
        var jobName = Guid.NewGuid().ToString();
        var jobId = new RunningJobId(RunModes.NamedJob(jobName), new(jobName));
        using var jobHandle = Win32JobModule.CreateJob(jobName);

        Win32JobModule.SetLimits(jobHandle, jobSettings);

        bool notificationReceived = false;
        async Task NotificationCheck(IMonitorNotification resp)
        {
            switch (resp)
            {
                case NewProcessEvent ev:
                    await Assert.That(ev.JobId).IsEqualTo(jobId);
                    break;
                case ProcessLimitExceededEvent jobLimit:
                    await Assert.That(jobLimit.ExceededLimit).IsEqualTo(LimitType.Memory);
                    await Assert.That(jobLimit.JobId).IsEqualTo(jobId);

                    notificationReceived = true;

                    // The job is still active. Therefore, we need to stop the monitor manually.
                    cts.Cancel();
                    break;
                default:
                    Assert.Fail($"Unexpected event: {resp}");
                    break;
            }
        }

        try
        {
            await H.StartAndAssignProcessToNamedJob(jobHandle, jobId, $"\"{testLimitPath}\" -m 1", NotificationCheck, cts.Token);
        }
        catch (TaskCanceledException) { }
        finally
        {
            Win32JobModule.TerminateJob(jobHandle, 0);
        }

        // job is not signaled
        await Assert.That(PInvoke.WaitForSingleObject(jobHandle, 0)).IsEqualTo(WAIT_EVENT.WAIT_TIMEOUT);

        await Assert.That(notificationReceived).IsTrue();
    }


    [Test]
    public async Task MonitorJobMetadata()
    {
        using var cts = new CancellationTokenSource(10000);

        var (pipe, monitorTask) = await SharedApi.StartMonitor(TimeSpan.FromSeconds(2), cts.Token);
        try
        {
            var p1RunMode = new RunInNamedJob(Guid.NewGuid().ToString());
            var p1JobId = new RunningJobId(p1RunMode, new(p1RunMode.JobName));
            var p1 = Program.Execute(new RunAsCmdApp(
                JobSettings.Empty with { RunMode = p1RunMode },
                new LaunchProcess(["cmd.exe", "/c", "timeout 4"], false), LaunchConfig.Quiet,
                StartBehavior.None, ExitBehavior.WaitForJobCompletion), cts.Token);

            await Task.Delay(1000);

            var ct = cts.Token;

            var p2RunMode = new RunInNamedJob(Guid.NewGuid().ToString());
            var p2JobId = new RunningJobId(p2RunMode, new(p2RunMode.JobName));
            var p2 = Program.Execute(new RunAsCmdApp(
                JobSettings.Empty with { RunMode = p2RunMode },
                new LaunchProcess(["cmd.exe", "/c", "timeout 4"], false), LaunchConfig.Quiet,
                StartBehavior.None, ExitBehavior.WaitForJobCompletion), cts.Token);

            int notificationNumber = 0;

            async Task NotificationCheck(IMonitorNotification n)
            {
                switch (n)
                {
                    case NewProcessEvent ev:
                        await Assert.That(notificationNumber).IsEqualTo(0);
                        await Assert.That(ev.JobId).IsEqualTo(p2JobId);
                        break;
                    case NoProcessesInJobEvent ev:
                        await Assert.That(ev.JobId).IsEqualTo(p1JobId).Or.IsEqualTo(p2JobId);
                        break;
                    default:
                        break;
                }
                notificationNumber++;
            }

            var notificationTask = NotificationListener(NotificationCheck, cts.Token);

            await Task.WhenAll(p1, p2);

            cts.Cancel();
            await monitorTask;
            await notificationTask;
        }
        finally
        {
            pipe.Dispose();
        }


        async Task NotificationListener(Func<IMonitorNotification, Task> processNotification, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(1024);
            try
            {
                while (pipe.IsConnected && await pipe.ReadAsync(buffer.GetMemory(), ct) is var bytesRead && bytesRead > 0)
                {
                    buffer.Advance(bytesRead);

                    int processedBytes = 0;
                    while (processedBytes < buffer.WrittenCount)
                    {
                        var msgPackReader = new MessagePackReader(buffer.WrittenMemory[processedBytes..]);
                        if (MsgPackSerializer.Deserialize<IMonitorResponse>(ref msgPackReader, ct) is IMonitorNotification notification)
                        {
                            processedBytes += (int)msgPackReader.Consumed;
                            await processNotification(notification);
                        }
                        else { Assert.Fail("serialization failed"); }
                    }

                    buffer.ResetWrittenCount();
                }
            }
            catch (Exception ex) when (ex.IsCancelledException()) { }
        }
    }
}
