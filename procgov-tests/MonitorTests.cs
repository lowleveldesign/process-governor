using MessagePack;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace ProcessGovernor.Tests;

public sealed class MonitorTests
{
    // FIXME: test start without processing events

    [Test]
    public async Task StartExitProcessEvents()
    {
        const string ProcessArgs = "cmd.exe /c \"exit 1\"\0";

        // using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cts = new CancellationTokenSource();

        var jobSettings = new JobSettings(MaxProcessMemory: 256 * 1024 * 1024, PropagateOnChildProcesses: false);

        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(), cts.Token));

        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        while (!pipe.IsConnected && !cts.IsCancellationRequested)
        {
            try { await pipe.ConnectAsync(cts.Token); } catch { }
        }

        var (pid, processHandle, threadHandle) = CreateSuspendedProcess();

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
        await notificationListenerTask;

        // FIXME: test the content of the notifications

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

        static (uint pid, SafeFileHandle processHandle, SafeFileHandle threadHandle) CreateSuspendedProcess()
        {
            unsafe
            {
                var pi = new PROCESS_INFORMATION();
                var si = new STARTUPINFOW();
                var processCreationFlags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;

                Span<char> processArgsSpan = stackalloc char[ProcessArgs.Length];
                ProcessArgs.CopyTo(processArgsSpan);

                NtApi.CheckWin32Result(PInvoke.CreateProcess(null, ref processArgsSpan, null, null, false,
                    processCreationFlags, null, null, si, out pi));

                return (pi.dwProcessId, new(pi.hProcess, true), new(pi.hThread, true));
            }
        }
    }
}
