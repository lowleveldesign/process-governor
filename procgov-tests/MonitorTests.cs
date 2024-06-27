using MessagePack;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Collections.Generic;
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

public sealed class MonitorTests
{
    // FIXME: test start without processing events

    [Test]
    public async Task StartExitProcessEvents()
    {
        var notifications = await RunProcessUnderMonitor("cmd.exe /c \"exit 1\"", new JobSettings(
            MaxProcessMemory: 256 * 1024 * 1024, PropagateOnChildProcesses: false));

        Assert.That(notifications, Has.Count.EqualTo(3));

        Assert.That(notifications[0], Is.InstanceOf<NewProcessEvent>());
        Assert.That(notifications[1], Is.InstanceOf<ExitProcessEvent>());
        Assert.That(notifications[2], Is.InstanceOf<NoProcessesInJobEvent>());
    }

    [Test]
    public async Task ActiveProcessNumberExceeded()
    {
        var testLimitPath = Path.Combine(AppContext.BaseDirectory, "testlimit.exe");
        if (!File.Exists(testLimitPath))
        {
            using var client = new HttpClient();
            using var fileStream = File.Create(testLimitPath);
            await (await client.GetStreamAsync("https://live.sysinternals.com/Testlimit.exe")).CopyToAsync(fileStream);
        }
    }

    async static Task<List<IMonitorResponse>> RunProcessUnderMonitor(string processArgs, JobSettings jobSettings)
    {
        // using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cts = new CancellationTokenSource();

        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(), cts.Token));

        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        while (!pipe.IsConnected && !cts.IsCancellationRequested)
        {
            try { await pipe.ConnectAsync(cts.Token); } catch { }
        }

        var (pid, processHandle, threadHandle) = CreateSuspendedProcess(processArgs);

        var buffer = new ArrayBufferWriter<byte>(1024);

        using var job = Win32JobModule.CreateJob(Win32JobModule.GetNewJobName(), jobSettings.ClockTimeLimitInMilliseconds);

        MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new IsProcessGoverned(pid), cancellationToken: cts.Token);
        await pipe.WriteAsync(buffer.WrittenMemory, cts.Token);
        buffer.ResetWrittenCount();

        int readBytes = await pipe.ReadAsync(buffer.GetMemory(), cts.Token);
        Assert.That(readBytes > 0);
        buffer.Advance(readBytes);

        Assert.That(MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory, bytesRead: out var deseralizedBytes,
            cancellationToken: cts.Token) is ProcessStatus { JobName: "" });
        Assert.That(readBytes, Is.EqualTo(deseralizedBytes));
        buffer.ResetWrittenCount();

        MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new MonitorJob(job.Name, true));
        await pipe.WriteAsync(buffer.WrittenMemory, cts.Token);
        buffer.ResetWrittenCount();

        var notificationListenerTask = NotificationListener(cts.Token);

        Win32JobModule.SetLimits(job, jobSettings, ProcessModule.GetSystemOrProcessorGroupAffinity(processHandle));

        Win32JobModule.AssignProcess(job, processHandle, jobSettings.PropagateOnChildProcesses);

        NtApi.CheckWin32Result(PInvoke.ResumeThread(threadHandle));

        // monitor should exit as there are no more jobs to monitor
        await monitorTask;

        // notification listener should finish as the pipe gets diconnected
        return await notificationListenerTask;

        async Task<List<IMonitorResponse>> NotificationListener(CancellationToken ct)
        {
            List<IMonitorResponse> notifications = [];

            while (pipe.IsConnected && await pipe.ReadAsync(buffer.GetMemory(), cts.Token) is var bytesRead && bytesRead > 0)
            {
                buffer.Advance(bytesRead);

                var processedBytes = 0;
                while (processedBytes < buffer.WrittenCount)
                {
                    var notification = MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory.Slice(processedBytes),
                                    bytesRead: out var deserializedBytes, cancellationToken: cts.Token);
                    notifications.Add(notification);
                    processedBytes += deserializedBytes;
                }

                buffer.ResetWrittenCount();
            }

            return notifications;
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
