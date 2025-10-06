using Nerdbank.MessagePack;
using ProcessGovernor.Library;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

namespace ProcessGovernor.Tests;

static class SharedApi
{
    public static void InitializeTestContext()
    {
        var debugTraceListener = new DefaultTraceListener() { TraceOutputOptions = TraceOptions.DateTime };
        var testTraceListener = new TestTraceListener() { TraceOutputOptions = TraceOptions.DateTime };

        Program.Logger.Switch.Level = SourceLevels.Verbose;
        Program.Logger.Listeners.Clear();
        Program.Logger.Listeners.Add(debugTraceListener);
        Program.Logger.Listeners.Add(testTraceListener);

        ProcessGovernorLibraryApi.SetLibraryLoggerLevel(SourceLevels.Verbose);
        ProcessGovernorLibraryApi.SetLibraryLoggerListeners([debugTraceListener, testTraceListener]);
    }


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
        Assert.That(readBytes > 0);
        buffer.Advance(readBytes);

        var msgPackReader = new MessagePackReader(buffer.WrittenMemory);
        if (MsgPackSerializer.Deserialize<IMonitorResponse>(ref msgPackReader, cancellationToken: ct) is GetJobIdResp { JobId: var jobId })
        {
            Assert.That(readBytes, Is.EqualTo(msgPackReader.Consumed));

            return jobId;
        }
        else { throw new InvalidOperationException(); }
    }

    public static readonly SystemInfo RealSystemInfo = SystemInfoModule.GetSystemInfo();

    public static ProcessorGroup GetDefaultProcessorGroup()
    {
        var systemInfo = RealSystemInfo;

        Assert.That(systemInfo.NumaNodes, Has.Length.GreaterThan(0));
        Assert.That(systemInfo.NumaNodes[0].ProcessorGroups, Has.Length.GreaterThan(0));

        return systemInfo.NumaNodes[0].ProcessorGroups[0];
    }

    class TestTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            TestContext.Out.Write(message);
        }

        public override void WriteLine(string? message)
        {
            TestContext.Out.WriteLine(message);
        }
    }
}

