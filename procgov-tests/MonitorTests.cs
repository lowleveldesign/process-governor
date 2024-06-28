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
    // FIXME: test start without processing events
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

        Win32JobModule.SetLimits(job, jobSettings, ProcessModule.GetSystemOrProcessorGroupAffinity());

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
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0) == WAIT_EVENT.WAIT_TIMEOUT);

        Assert.That(notificationNumber, Is.EqualTo(3));
    }

    [Test]
    public static async Task TerminateJobEvents()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = new JobSettings(ClockTimeLimitInMilliseconds: 2000, PropagateOnChildProcesses: false);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName(), jobSettings.ClockTimeLimitInMilliseconds);

        Win32JobModule.SetLimits(job, jobSettings, ProcessModule.GetSystemOrProcessorGroupAffinity());

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
                    // we are not getting the usual process exit event when job
                    // it terminated
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
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0) == WAIT_EVENT.WAIT_OBJECT_0);

        Assert.That(notificationNumber, Is.EqualTo(2));
    }

    [Test]
    public static async Task ActiveProcessNumberExceeded()
    {
        using var cts = new CancellationTokenSource(10000);

        var testLimitPath = await DownloadSysinternalsTestLimit(cts.Token);

        var jobSettings = new JobSettings(ActiveProcessLimit: 3, PropagateOnChildProcesses: true);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName(), jobSettings.ClockTimeLimitInMilliseconds);

        Win32JobModule.SetLimits(job, jobSettings, ProcessModule.GetSystemOrProcessorGroupAffinity());

        int notificationNumber = 0;
        void NotificationCheck(IMonitorResponse resp)
        {
            switch (resp)
            {
                case JobLimitExceededEvent jobLimit:
                    Assert.That(notificationNumber, Is.EqualTo(jobSettings.ActiveProcessLimit));
                    Assert.That(jobLimit.ExceededLimit, Is.EqualTo(LimitType.ActiveProcessNumber));
                    Assert.That(jobLimit.JobName, Is.EqualTo(job.Name));

                    cts.Cancel();
                    break;
                case NewProcessEvent ev:
                    Assert.That(notificationNumber, Is.GreaterThanOrEqualTo(0).And.LessThan(jobSettings.ActiveProcessLimit));
                    Assert.That(ev.JobName, Is.EqualTo(job.Name));
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
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0) == WAIT_EVENT.WAIT_TIMEOUT);

        Assert.That(notificationNumber, Is.EqualTo(4));
    }

    async static Task RunAndMonitorProcessUnderJob(Win32Job job, string processArgs,
        Action<IMonitorResponse> processNotification, CancellationToken ct)
    {
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(), ct));

        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        while (!pipe.IsConnected && !ct.IsCancellationRequested)
        {
            try { await pipe.ConnectAsync(ct); } catch { }
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

        MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new MonitorJob(job.Name, true), cancellationToken: ct);
        await pipe.WriteAsync(buffer.WrittenMemory, ct);
        buffer.ResetWrittenCount();

        var notificationListenerTask = NotificationListener();

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
