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
using Windows.Win32.System.Threading;
using static ProcessGovernor.Win32.Helpers;

namespace ProcessGovernor.Tests.Code;

public static partial class ProgramTests
{
    static class H
    {
        public static async Task<string> DownloadSysinternalsTestLimit(CancellationToken ct)
        {
            var testLimitPath = Path.Combine(AppContext.BaseDirectory, "testlimit64.exe");
            if (!File.Exists(testLimitPath))
            {
                using var client = new HttpClient();
                using var fileStream = File.Create(testLimitPath);
                await (await client.GetStreamAsync("https://live.sysinternals.com/Testlimit64.exe", ct)).CopyToAsync(fileStream, ct);
            }
            return testLimitPath;
        }

        public static async Task<(NamedPipeClientStream, Task)> StartMonitor(CancellationToken ct)
        {
            var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            // is monitor already running?
            try
            {
                await pipe.ConnectAsync(10, ct);

                throw new InvalidOperationException("Monitor already running");
            }
            catch
            {
            }

            var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(Program.DefaultMaxMonitorIdleTime, false), ct));

            while (!pipe.IsConnected && !ct.IsCancellationRequested)
            {
                try { await pipe.ConnectAsync(ct); } catch { }
            }

            return (pipe, monitorTask);
        }

        public static async Task RunAndMonitorProcessUnderJob(Win32Job job, JobSettings jobSettings, string processArgs,
            Action<IMonitorResponse>? processNotification, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var (pipe, monitorTask) = await StartMonitor(cts.Token);
            var buffer = new ArrayBufferWriter<byte>(1024);

            try
            {
                var (pid, processHandle, threadHandle) = CreateSuspendedProcess(processArgs);

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
                var jobData = await SharedApi.TryGetJobDataFromMonitor(pid, ct);
                Assert.That(jobData, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    var (receivedJobName, receivedJobSettings) = jobData.Value;
                    Assert.That(receivedJobName, Is.EqualTo(job.Name));
                    Assert.That(receivedJobSettings, Is.EqualTo(jobSettings));
                });

                CheckWin32Result(PInvoke.ResumeThread(threadHandle));

                // notification listener should finish as the pipe gets diconnected
                await notificationListenerTask;

                cts.Cancel();

                await monitorTask;
            }
            finally
            {
                pipe.Dispose();
            }

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
                        CheckWin32Result(PInvoke.CreateProcess(null, (char*)processArgsPtr, null, null,
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
}
