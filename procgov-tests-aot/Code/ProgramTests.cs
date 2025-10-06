using ProcessGovernor.Library;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;
using static ProcessGovernor.Program;

namespace ProcessGovernor.Tests.Code;

public partial class ProgramTests
{

    [Before(Class)]
    public static async Task BeforeTest()
    {
        // required for the service tests to succeed without admin rights
        InstalledProcessSettingsModule.IsUserScoped = true;
    }

    static readonly SystemInfo TestSystemInfo2Numas4Groups = new(
        NumaNodes: [
            new(0, [new(0, 0xF), new(1, 0x7)]),
            new(1, [new(2, 0xF), new(3, 0x7)])
        ],
        ProcessorGroups: [
            new(0, 0xF), new(1, 0x7), new(2, 0xF), new(3, 0x7)
        ],
        CpuCores: [
            new(true, new(0, 0x1)), new(true, new(0, 0x2)), new(true, new(0, 0x4)), new(true, new(0, 0x8)),
            new(true, new(1, 0x1)), new(true, new(1, 0x2)), new(true, new(1, 0x4)),
            new(true, new(2, 0x1)), new(true, new(2, 0x2)), new(true, new(2, 0x4)), new(true, new(2, 0x8)),
            new(true, new(3, 0x1)), new(true, new(3, 0x2)), new(true, new(3, 0x4)),
    ]);

    static readonly SystemInfo TestSystemInfo2Numas1Group = new(
        NumaNodes: [
            new(0, [new(0, 0x007F)]),
            new(1, [new(0, 0x3F80)])
        ],
        ProcessorGroups: [
            new(0, 0x3FFF)
        ],
        CpuCores: [
            new(true, new(0, 0x0001)), new(true, new(0, 0x0002)), new(true, new(0, 0x0004)), new(true, new(0, 0x0008)),
            new(true, new(0, 0x0010)), new(true, new(0, 0x0020)), new(true, new(0, 0x0040)), new(true, new(0, 0x0080)),
            new(true, new(0, 0x0100)), new(true, new(0, 0x0200)), new(true, new(0, 0x0400)), new(true, new(0, 0x0800)),
            new(true, new(0, 0x1000)), new(true, new(3, 0x2000)),
        ]
    );

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

        public static async Task StartAndAssignProcessToNamedJob(SafeHandle jobHandle, RunningJobId jobId,
            string processArgs, Action<IMonitorNotification>? processNotification, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var maxMonitorIdleTime = TimeSpan.FromSeconds(2);

            var (pipe, monitorTask) = await SharedApi.StartMonitor(maxMonitorIdleTime, cts.Token);
            var monitorPipeClient = new ProcessGovernorMonitorPipeClient(pipe, cts.Token);

            var notificationListenerTask = Task.CompletedTask;

            try
            {
                using var processObject = Win32ProcessModule.StartProcess(processArgs,
                    ProcessCreationFlags.Suspended, new(), out var threadHandle);
                try
                {
                    var resp = await monitorPipeClient.SendRequest(new GetJobIdReq(processObject.Id));
                    // job name check
                    await Assert.That(resp).IsTypeOf<GetJobIdResp>().And.HasProperty(
                        c => c.JobId).IsEqualTo(new RunningJobId()); // invalid

                    // start monitoring
                    resp = await monitorPipeClient.SendRequest(new MonitorJobReq(jobId));
                    await Assert.That(resp is AckResp { IsSuccess: true }).IsTrue();

                    // subscribe to notifications if requested
                    if (processNotification is not null)
                    {
                        resp = await monitorPipeClient.SendRequest(new SubscribeToNotificationsReq(jobId));
                        await Assert.That(resp is AckResp { IsSuccess: true }).IsTrue();

                        notificationListenerTask = StartNotificationListener(cts.Token);
                    }

                    Win32JobModule.AssignProcess(jobHandle, processObject.Handle);

                    // give it some time to process the request
                    await Task.Delay(1000, ct);

                    resp = await monitorPipeClient.SendRequest(new GetJobIdReq(processObject.Id));
                    // job name check
                    await Assert.That(resp).IsTypeOf<GetJobIdResp>().And.HasProperty(c => c.JobId, jobId);

                    if (PInvoke.ResumeThread(threadHandle) == 0xffffffff)
                    {
                        throw new Win32Exception();
                    }

                    // give it some time to process the request
                    await Task.Delay(1000, ct);

                    Win32ProcessModule.WaitForTheProcessToExit(processObject.Handle, ct);
                }
                finally { threadHandle.Dispose(); }
            }
            finally { pipe.Dispose(); }

            // monitor should finish when all clients are gone and the idle time passes
            await Assert.That(await Task.WhenAny(monitorTask, Task.Delay(maxMonitorIdleTime * 2, ct))).IsEqualTo(monitorTask);

            cts.Cancel();

            await notificationListenerTask;

            async Task StartNotificationListener(CancellationToken ct)
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var notification = await monitorPipeClient.NotificationsReader.ReadAsync(ct);
                        processNotification(notification);
                    }
                }
                catch (Exception ex) when (ex.IsCancelledException()) { }
            }
        }
    }
}
