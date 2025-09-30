using MessagePack;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace ProcessGovernor.Tests.Code;

public static partial class ProgramTests
{
    [Test]
    public static async Task MonitorNoProcessStarted()
    {
        using var cts = new CancellationTokenSource(10000);

        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(Program.DefaultMaxMonitorIdleTime, false), cts.Token));

        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        // is monitor already running?
        while (!pipe.IsConnected && !cts.IsCancellationRequested)
        {
            try { await pipe.ConnectAsync(cts.Token); } catch { }
        }

        await Task.Delay(Program.DefaultMaxMonitorIdleTime + TimeSpan.FromSeconds(1), cts.Token);

        Assert.That(monitorTask.IsCompletedSuccessfully);
    }

    [Test]
    public static async Task MonitorStartExitProcessEvents()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = new JobSettings(propagateOnChildProcesses: false);
        using var job = Win32JobModule.CreateJob(Program.GenerateNewJobName());

        Win32JobModule.SetLimits(job, jobSettings);

        int notificationNumber = 0;
        void NotificationCheck(IMonitorResponse resp)
        {
            switch (resp)
            {
                case NewProcessEvent ev:
                    Assert.That(notificationNumber, Is.EqualTo(0));
                    Assert.That(ev.JobName, Is.EqualTo(job.Name));
                    break;
                case ExitProcessEvent ev:
                    Assert.That(notificationNumber, Is.EqualTo(1));
                    Assert.That(ev.JobName, Is.EqualTo(job.Name));
                    break;
                case NoProcessesInJobEvent ev:
                    Assert.That(notificationNumber, Is.EqualTo(2));
                    Assert.That(ev.JobName, Is.EqualTo(job.Name));
                    break;
                default:
                    Assert.Fail($"Unexpected event: {resp}");
                    break;
            }
            notificationNumber++;
        }

        await H.RunAndMonitorProcessUnderJob(job, jobSettings, "cmd.exe /c \"exit 1\"", NotificationCheck, cts.Token);

        // job is not signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_TIMEOUT));

        Assert.That(notificationNumber, Is.EqualTo(3));
    }


    [Test]
    public static async Task MonitorStartExitProcessWithoutMonitoring()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = new JobSettings(propagateOnChildProcesses: false);
        using var job = Win32JobModule.CreateJob(Program.GenerateNewJobName());

        Win32JobModule.SetLimits(job, jobSettings);

        await H.RunAndMonitorProcessUnderJob(job, jobSettings, "cmd.exe /c \"exit 1\"", null, cts.Token);

        // job is not signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_TIMEOUT));
    }

    [Test]
    public static async Task MonitorTerminateJobEvents()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = new JobSettings(clockTimeLimitInMilliseconds: 2000, propagateOnChildProcesses: false);
        using var job = Win32JobModule.CreateJob(Program.GenerateNewJobName());

        Win32JobModule.SetLimits(job, jobSettings);

        int notificationNumber = 0;
        void NotificationCheck(IMonitorResponse resp)
        {
            switch (resp)
            {
                case NewProcessEvent ev:
                    Assert.That(notificationNumber, Is.EqualTo(0));
                    Assert.That(ev.JobName, Is.EqualTo(job.Name));
                    break;
                case NoProcessesInJobEvent ev:
                    // we are not getting the usual process exit event when job is terminated
                    Assert.That(notificationNumber, Is.EqualTo(1));
                    Assert.That(ev.JobName, Is.EqualTo(job.Name));
                    break;
                default:
                    Assert.Fail($"Unexpected event: {resp}");
                    break;
            }
            notificationNumber++;
        }

        var task = H.RunAndMonitorProcessUnderJob(job, jobSettings, "winver.exe", NotificationCheck, cts.Token);

        await Task.Delay((int)jobSettings.ClockTimeLimitInMilliseconds);

        Win32JobModule.TerminateJob(job, 0);

        // monitor should terminate with the job
        await task;

        // job should albo be signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_OBJECT_0));

        Assert.That(notificationNumber, Is.EqualTo(2));
    }

    [Test]
    public static async Task MonitorActiveProcessNumberExceeded()
    {
        using var cts = new CancellationTokenSource(15000);

        var testLimitPath = await H.DownloadSysinternalsTestLimit(cts.Token);

        var jobSettings = new JobSettings(activeProcessLimit: 3, propagateOnChildProcesses: true);
        using var job = Win32JobModule.CreateJob(Program.GenerateNewJobName());

        Win32JobModule.SetLimits(job, jobSettings);

        int notificationNumber = 0;
        void NotificationCheck(IMonitorResponse resp)
        {
            switch (resp)
            {
                case NewProcessEvent ev:
                    Assert.That(notificationNumber, Is.GreaterThanOrEqualTo(0).And.LessThan(jobSettings.ActiveProcessLimit));
                    Assert.That(ev.JobName, Is.EqualTo(job.Name));
                    break;
                case JobLimitExceededEvent jobLimit:
                    Assert.That(notificationNumber, Is.EqualTo(jobSettings.ActiveProcessLimit));
                    Assert.That(jobLimit.ExceededLimit, Is.EqualTo(LimitType.ActiveProcessNumber));
                    Assert.That(jobLimit.JobName, Is.EqualTo(job.Name));

                    // The job does not allow more processes to be created and is still active. Therefore,
                    // we need to stop the monitor manually.
                    cts.Cancel();
                    break;
                default:
                    Assert.Fail($"Unexpected event: {resp}");
                    break;
            }
            notificationNumber++;
        }

        try
        {
            await H.RunAndMonitorProcessUnderJob(job, jobSettings, $"\"{testLimitPath}\" -p 5", NotificationCheck, cts.Token);
        }
        catch (TaskCanceledException) { }
        finally
        {
            Win32JobModule.TerminateJob(job, 0);
        }

        // job is not signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_TIMEOUT));

        Assert.That(notificationNumber, Is.EqualTo(4));
    }

    [Test]
    public static async Task MonitorProcessMemoryLimitExceeded()
    {
        using var cts = new CancellationTokenSource(10000);

        var testLimitPath = await H.DownloadSysinternalsTestLimit(cts.Token);

        // memory limit per process is 2MB
        var jobSettings = new JobSettings(maxProcessMemory: 100 * 1024 * 1024, propagateOnChildProcesses: true);
        using var job = Win32JobModule.CreateJob(Program.GenerateNewJobName());

        Win32JobModule.SetLimits(job, jobSettings);

        int notificationNumber = 0;
        void NotificationCheck(IMonitorResponse resp)
        {
            switch (resp)
            {
                case NewProcessEvent ev:
                    Assert.That(notificationNumber, Is.EqualTo(0));
                    Assert.That(ev.JobName, Is.EqualTo(job.Name));
                    break;
                case ProcessLimitExceededEvent jobLimit:
                    Assert.That(notificationNumber, Is.EqualTo(1));
                    Assert.That(jobLimit.ExceededLimit, Is.EqualTo(LimitType.Memory));
                    Assert.That(jobLimit.JobName, Is.EqualTo(job.Name));

                    // The job is still active. Therefore, we need to stop the monitor manually.
                    cts.Cancel();
                    break;
                default:
                    Assert.Fail($"Unexpected event: {resp}");
                    break;
            }
            notificationNumber++;
        }

        try
        {
            await H.RunAndMonitorProcessUnderJob(job, jobSettings, $"\"{testLimitPath}\" -m 1", NotificationCheck, cts.Token);
        }
        catch (TaskCanceledException) { }
        finally
        {
            Win32JobModule.TerminateJob(job, 0);
        }

        // job is not signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_TIMEOUT));

        Assert.That(notificationNumber, Is.EqualTo(2));
    }


    [Test]
    public static async Task MonitorJobMetadata()
    {
        using var cts = new CancellationTokenSource(10000);

        var buffer = new ArrayBufferWriter<byte>(1024);

        var (pipe, monitorTask) = await H.StartMonitor(cts.Token);
        try
        {
            var p1JobName = Program.GenerateNewJobName();
            var p1 = Program.Execute(new RunAsCmdApp(p1JobName, new JobSettings(), new LaunchProcess(
                ["cmd.exe", "/c", "timeout 4"], false), [], [], LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.WaitForJobCompletion), cts.Token);

            await Task.Delay(1000);

            var ct = cts.Token;

            // job names check
            MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new GetJobNamesReq(true), cancellationToken: ct);
            await pipe.WriteAsync(buffer.WrittenMemory, ct);
            buffer.ResetWrittenCount();

            int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
            Assert.That(readBytes > 0);
            buffer.Advance(readBytes);

            var resp = MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory, out var deserializedBytes, ct);
            buffer.ResetWrittenCount();

            var jobNames = resp is GetJobNamesResp { JobNames: var n } ? n : [];
            Assert.That(jobNames, Has.Length.EqualTo(1));
            Assert.That(readBytes, Is.EqualTo(deserializedBytes));

            Assert.That(p1JobName, Is.EqualTo(jobNames[0]));

            var p2JobName = Program.GenerateNewJobName();
            var p2 = Program.Execute(new RunAsCmdApp(p2JobName, new JobSettings(), new LaunchProcess(
                ["cmd.exe", "/c", "timeout 4"], false), [], [], LaunchConfig.Quiet, StartBehavior.None, ExitBehavior.WaitForJobCompletion), cts.Token);

            int notificationNumber = 0;

            void NotificationCheck(IMonitorResponse resp)
            {
                switch (resp)
                {
                    case NewOrUpdatedJobEvent ev:
                        Assert.That(notificationNumber, Is.EqualTo(0));
                        Assert.That(ev.JobName, Is.EqualTo(p2JobName));
                        break;
                    case NewProcessEvent ev:
                        Assert.That(notificationNumber, Is.EqualTo(1));
                        Assert.That(ev.JobName, Is.EqualTo(p2JobName));
                        break;
                    case NoProcessesInJobEvent ev:
                        Assert.That(ev.JobName, Is.EqualTo(p1JobName).Or.EqualTo(p2JobName));
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


        async Task NotificationListener(Action<IMonitorResponse> processNotification, CancellationToken ct)
        {
            try
            {
                while (pipe.IsConnected && await pipe.ReadAsync(buffer.GetMemory(), ct) is var bytesRead && bytesRead > 0)
                {
                    buffer.Advance(bytesRead);

                    var processedBytes = 0;
                    while (processedBytes < buffer.WrittenCount)
                    {
                        var notification = MessagePackSerializer.Deserialize<IMonitorResponse>(
                            buffer.WrittenMemory[processedBytes..], out var deserializedBytes, ct);

                        processNotification(notification);

                        processedBytes += deserializedBytes;
                    }

                    buffer.ResetWrittenCount();
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
