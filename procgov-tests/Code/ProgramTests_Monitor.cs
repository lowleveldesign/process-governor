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

namespace ProcessGovernor.Tests.Code;

public static partial class ProgramTests
{
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
    public static async Task MonitorNoProcessStarted()
    {
        using var cts = new CancellationTokenSource(10000);

        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(Program.DefaultMaxMonitorIdleTime, true), cts.Token));

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
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName());

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

        await RunAndMonitorProcessUnderJob(job, jobSettings, "cmd.exe /c \"exit 1\"", NotificationCheck, cts.Token);

        // job is not signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_TIMEOUT));

        Assert.That(notificationNumber, Is.EqualTo(3));
    }


    [Test]
    public static async Task MonitorStartExitProcessWithoutMonitoring()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = new JobSettings(propagateOnChildProcesses: false);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName());

        Win32JobModule.SetLimits(job, jobSettings);

        await RunAndMonitorProcessUnderJob(job, jobSettings, "cmd.exe /c \"exit 1\"", null, cts.Token);

        // job is not signaled
        Assert.That(PInvoke.WaitForSingleObject(job.Handle, 0), Is.EqualTo(WAIT_EVENT.WAIT_TIMEOUT));
    }

    [Test]
    public static async Task MonitorTerminateJobEvents()
    {
        using var cts = new CancellationTokenSource(10000);

        var jobSettings = new JobSettings(clockTimeLimitInMilliseconds: 2000, propagateOnChildProcesses: false);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName());

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

        var task = RunAndMonitorProcessUnderJob(job, jobSettings, "winver.exe", NotificationCheck, cts.Token);

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

        var testLimitPath = await DownloadSysinternalsTestLimit(cts.Token);

        var jobSettings = new JobSettings(activeProcessLimit: 3, propagateOnChildProcesses: true);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName());

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
            await RunAndMonitorProcessUnderJob(job, jobSettings, $"\"{testLimitPath}\" -p 5", NotificationCheck, cts.Token);
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

        var testLimitPath = await DownloadSysinternalsTestLimit(cts.Token);

        // memory limit per process is 2MB
        var jobSettings = new JobSettings(maxProcessMemory: 2 * 1024 * 1024, propagateOnChildProcesses: true);
        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName());

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
            await RunAndMonitorProcessUnderJob(job, jobSettings, $"\"{testLimitPath}\" -m 1", NotificationCheck, cts.Token);
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

    static async Task<NamedPipeClientStream> StartMonitor(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        // is monitor already running?
        try { await pipe.ConnectAsync(10, ct); }
        catch
        {
            _ = Task.Run(() => Program.Execute(new RunAsMonitor(Program.DefaultMaxMonitorIdleTime, true), ct));

            while (!pipe.IsConnected && !ct.IsCancellationRequested)
            {
                try { await pipe.ConnectAsync(ct); } catch { }
            }
        }

        return pipe;
    }

    static async Task RunAndMonitorProcessUnderJob(Win32Job job, JobSettings jobSettings, string processArgs,
        Action<IMonitorResponse>? processNotification, CancellationToken ct)
    {
        using var pipe = await StartMonitor(ct);

        var (pid, processHandle, threadHandle) = CreateSuspendedProcess(processArgs);

        var buffer = new ArrayBufferWriter<byte>(1024);

        // job name check
        MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new GetJobNameReq(pid), cancellationToken: ct);
        await pipe.WriteAsync(buffer.WrittenMemory, ct);
        buffer.ResetWrittenCount();

        int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
        Assert.That(readBytes > 0);
        buffer.Advance(readBytes);

        var resp = MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory, out var deserializedBytes, ct);
        Assert.That(resp is GetJobNameResp { JobName: "" });
        Assert.That(readBytes, Is.EqualTo(deserializedBytes));
        buffer.ResetWrittenCount();

        // start monitoring
        MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new MonitorJobReq(job.Name, processNotification is not null,
            jobSettings), cancellationToken: ct);
        await pipe.WriteAsync(buffer.WrittenMemory, ct);
        buffer.ResetWrittenCount();

        readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
        buffer.Advance(readBytes);

        resp = MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory, out deserializedBytes, ct);
        Assert.That(resp is MonitorJobResp { JobName: var jobName } && jobName == job.Name);
        Assert.That(readBytes, Is.EqualTo(deserializedBytes));
        buffer.ResetWrittenCount();

        var notificationListenerTask = processNotification is not null ? NotificationListener() : Task.CompletedTask;

        Win32JobModule.AssignProcess(job, processHandle);

        // give it some time to process the request
        await Task.Delay(1000, ct);

        // job settings check
        Assert.That(await SharedApi.TryGetJobSettingsFromMonitor(pid, ct), Is.EqualTo(jobSettings));

        NtApi.CheckWin32Result(PInvoke.ResumeThread(threadHandle));

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
