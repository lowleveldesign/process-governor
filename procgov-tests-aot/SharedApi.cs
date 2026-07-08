using Nerdbank.MessagePack;
using ProcessGovernor.Library;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

namespace ProcessGovernor.Tests;

public sealed class AdminOnlyAttribute() : SkipAttribute("This test is only supported when run as admin.")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
    {
        return Task.FromResult(!Environment.IsPrivilegedProcess);
    }
}

static class SharedApi
{
    public static async Task<(NamedPipeClientStream, Task)> StartMonitor(TimeSpan monitorIdleTime, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", ProcessGovernorLibraryApi.DefaultPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        // is monitor already running?
        try
        {
            await pipe.ConnectAsync(10, ct);

            throw new InvalidOperationException("Monitor already running");
        }
        catch
        {
        }

        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(monitorIdleTime, false), ct));

        while (!pipe.IsConnected && !ct.IsCancellationRequested)
        {
            try { await pipe.ConnectAsync(ct); } catch { }
        }

        return (pipe, monitorTask);
    }

    public static async Task<bool> IsMonitorListening(CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", ProcessGovernorLibraryApi.DefaultPipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(500, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public static async ValueTask<RunningJobId> GetJobIdFromMonitor(uint processId, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", ProcessGovernorLibraryApi.DefaultPipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(500, ct);

        return await GetJobIdFromMonitor(pipe, processId, ct);
    }

    public static async ValueTask<RunningJobId> GetJobIdFromMonitor(NamedPipeClientStream pipe, uint processId, CancellationToken ct)
    {
        await MsgPackSerializer.SerializeAsync<IMonitorRequest>(pipe, new GetJobIdReq(processId), cancellationToken: ct);

        var buffer = new ArrayBufferWriter<byte>(1024);
        int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
        await Assert.That(readBytes).IsGreaterThan(0);
        buffer.Advance(readBytes);

        var msgPackReader = new MessagePackReader(buffer.WrittenMemory);
        if (MsgPackSerializer.Deserialize<IMonitorResponse>(ref msgPackReader, cancellationToken: ct) is GetJobIdResp { JobId: var jobId })
        {
            await Assert.That(readBytes).IsEqualTo((int)msgPackReader.Consumed);

            return jobId;
        }
        else { throw new InvalidOperationException(); }
    }

    public static readonly SystemInfo RealSystemInfo = SystemInfoModule.GetSystemInfo();

    public static ProcessorGroup GetDefaultProcessorGroup()
    {
        var systemInfo = RealSystemInfo;

        Debug.Assert(systemInfo.NumaNodes.Length > 0);
        Debug.Assert(systemInfo.NumaNodes[0].ProcessorGroups.Length > 0);

        return systemInfo.NumaNodes[0].ProcessorGroups[0];
    }
}

