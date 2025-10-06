using ProcessGovernor.Library;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace ProcessGovernor.Tests.Code;

public partial class LibraryTests
{
    [Test]
    public async Task StartAndTerminateProces()
    {
        using var cts = new CancellationTokenSource(3000);

        using var jobHandle = Win32JobModule.CreateJob();
        using var iocp = new Win32JobIoCompletionPort();

        var jobId = new RunningJobId(RunModes.Default, new("anything"));
        iocp.AssignJob(jobHandle, jobId);

        using var process = Win32ProcessModule.StartProcessInJob("cmd.exe /c timeout 1", ProcessCreationFlags.None, new(), jobHandle);
        await Assert.That(PInvoke.WaitForSingleObject(process.Handle, 2000)).IsEqualTo(WAIT_EVENT.WAIT_OBJECT_0);

        IMonitorNotification[] expectedEvents = [
            new NewProcessEvent(jobId, process.Id),
                new ExitProcessEvent(jobId, process.Id, false),
                new NoProcessesInJobEvent(jobId)
        ];

        int foundEvents = 0;
        foreach (var expectedEvent in expectedEvents)
        {
            while (iocp.TryRead(out var ev))
            {
                if (expectedEvent.Equals(ev))
                {
                    foundEvents++;
                    break;
                }
            }
        }
        await Assert.That(foundEvents).IsEqualTo(expectedEvents.Length);
    }
}
