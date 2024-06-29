using MessagePack;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace ProcessGovernor.Tests;

public static class MonitorTests
{
    static MonitorTests()
    {
        ProcessGovernorTestContext.Initialize();
    }

    static async Task<string> DownloadSysinternalsTestLimit(CancellationToken ct)
    {
        var testLimitPath = Path.Combine(AppContext.BaseDirectory, "testlimit.exe");
        if (!File.Exists(testLimitPath))
        {
            using var client = new HttpClient();
            using var fileStream = File.Create(testLimitPath);
            await (await client.GetStreamAsync("https://live.sysinternals.com/Testlimit.exe", ct)).CopyToAsync(fileStream, ct);
        }
        return testLimitPath;
    }

    [Test]
    public static async Task StartExitProcessEvents()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = new JobSettings(PropagateOnChildProcesses: false);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName(), jobSettings.ClockTimeLimitInMilliseconds);

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

        await RunAndMonitorProcessUnderJob(job, "cmd.exe /c \"exit 1\"", NotificationCheck, cts.Token);

        // job is not signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_TIMEOUT));

        Assert.That(notificationNumber, Is.EqualTo(3));
    }


    [Test]
    public static async Task StartExitProcessWithoutMonitoring()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = new JobSettings(PropagateOnChildProcesses: false);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName(), jobSettings.ClockTimeLimitInMilliseconds);

        Win32JobModule.SetLimits(job, jobSettings);

        await RunAndMonitorProcessUnderJob(job, "cmd.exe /c \"exit 1\"", null, cts.Token);

        // job is not signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_TIMEOUT));
    }

    [Test]
    public static async Task TerminateJobEvents()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = new JobSettings(ClockTimeLimitInMilliseconds: 2000, PropagateOnChildProcesses: false);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName(), jobSettings.ClockTimeLimitInMilliseconds);

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

        var task = RunAndMonitorProcessUnderJob(job, "winver.exe", NotificationCheck, cts.Token);

        await Task.Delay((int)jobSettings.ClockTimeLimitInMilliseconds);

        Win32JobModule.TerminateJob(job, 0);

        // monitor should terminate with the job
        await task;

        // job should albo be signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_OBJECT_0));

        Assert.That(notificationNumber, Is.EqualTo(2));
    }

    [Test]
    public static async Task ActiveProcessNumberExceeded()
    {
        using var cts = new CancellationTokenSource(15000);

        var testLimitPath = await DownloadSysinternalsTestLimit(cts.Token);

        var jobSettings = new JobSettings(ActiveProcessLimit: 3, PropagateOnChildProcesses: true);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName(), jobSettings.ClockTimeLimitInMilliseconds);

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
            await RunAndMonitorProcessUnderJob(job, $"\"{testLimitPath}\" -p 5", NotificationCheck, cts.Token);
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
    public static async Task ProcessMemoryLimitExceeded()
    {
        using var cts = new CancellationTokenSource(10000);

        var testLimitPath = await DownloadSysinternalsTestLimit(cts.Token);

        // memory limit per process is 2MB
        var jobSettings = new JobSettings(MaxProcessMemory: 2 * 1024 * 1024, PropagateOnChildProcesses: true);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName(), jobSettings.ClockTimeLimitInMilliseconds);

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
            await RunAndMonitorProcessUnderJob(job, $"\"{testLimitPath}\" -m 1", NotificationCheck, cts.Token);
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

    async static Task RunAndMonitorProcessUnderJob(Win32Job job, string processArgs,
        Action<IMonitorResponse>? processNotification, CancellationToken ct)
    {
        var monitorTask = Task.CompletedTask;
        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        // is monitor already running?
        try { await pipe.ConnectAsync(10, ct); }
        catch
        {
            monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(), ct));

            while (!pipe.IsConnected && !ct.IsCancellationRequested)
            {
                try { await pipe.ConnectAsync(ct); } catch { }
            }
        }

        var (pid, processHandle, threadHandle) = CreateSuspendedProcess(processArgs);

        var buffer = new ArrayBufferWriter<byte>(1024);

        MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new IsProcessGoverned(pid), cancellationToken: ct);
        await pipe.WriteAsync(buffer.WrittenMemory, ct);
        buffer.ResetWrittenCount();

        int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
        Assert.That(readBytes > 0);
        buffer.Advance(readBytes);

        Assert.That(MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory,
            bytesRead: out var deseralizedBytes, cancellationToken: ct) is ProcessStatus { JobName: "" });
        Assert.That(readBytes, Is.EqualTo(deseralizedBytes));
        buffer.ResetWrittenCount();

        MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new MonitorJob(job.Name, processNotification is not null),
            cancellationToken: ct);
        await pipe.WriteAsync(buffer.WrittenMemory, ct);
        buffer.ResetWrittenCount();

        var notificationListenerTask = processNotification is not null ? NotificationListener() : Task.CompletedTask;

        Win32JobModule.AssignProcess(job, processHandle);

        NtApi.CheckWin32Result(PInvoke.ResumeThread(threadHandle));

        // monitor should exit as there are no more jobs to monitor
        await monitorTask;

        // notification listener should finish as the pipe gets diconnected
        await notificationListenerTask;

        async Task NotificationListener()
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

        static (uint pid, SafeFileHandle processHandle, SafeFileHandle threadHandle) CreateSuspendedProcess(string processArgs)
        {
            var processCreationFlags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;

            var processArgsPtr = Marshal.StringToHGlobalUni(processArgs);
            try
            {
                var pi = new PROCESS_INFORMATION();
                var si = new STARTUPINFOW() { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };

                unsafe
                {
                    NtApi.CheckWin32Result(PInvoke.CreateProcess(null, (char*)processArgsPtr, null, null,
                        false, processCreationFlags, null, null, &si, &pi));
                }

                return (pi.dwProcessId, new(pi.hProcess, true), new(pi.hThread, true));
            }
            finally
            {
                Marshal.FreeHGlobal(processArgsPtr);
            }
        }
    }
}
