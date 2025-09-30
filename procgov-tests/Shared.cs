using MessagePack;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGovernor.Tests;

static class ProcessGovernorTestContext
{
    static ProcessGovernorTestContext()
    {
        Program.Logger.Switch.Level = SourceLevels.Verbose;
        Program.Logger.Listeners.Clear();
        Program.Logger.Listeners.Add(new DefaultTraceListener() { TraceOutputOptions = TraceOptions.DateTime });
        Program.Logger.Listeners.Add(new TestTraceListener() { TraceOutputOptions = TraceOptions.DateTime });
    }

    public static void Initialize() { }

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

static class SharedApi
{
    public static async Task<bool> IsMonitorListening(CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

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


    public static async ValueTask<string> TryGetJobNameFromMonitor(uint processId, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(500, ct);

        return await TryGetJobNameFromMonitor(pipe, processId, ct);
    }

    public static async ValueTask<string> TryGetJobNameFromMonitor(NamedPipeClientStream pipe, uint processId, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);

        MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new GetJobNameReq(processId), cancellationToken: ct);
        await pipe.WriteAsync(buffer.WrittenMemory, ct);
        buffer.ResetWrittenCount();

        int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
        Assert.That(readBytes > 0);
        buffer.Advance(readBytes);
        if (MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory,
            bytesRead: out var deseralizedBytes, cancellationToken: ct) is GetJobNameResp
            {
                JobName: var jobName
            })
        {
            Assert.That(readBytes, Is.EqualTo(deseralizedBytes));

            return jobName;
        }
        else { throw new InvalidOperationException(); }
    }

    public static async ValueTask<JobSettings?> TryGetJobSettingsFromMonitor(string jobName, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(500, ct);

        return await TryGetJobSettingsFromMonitor(pipe, jobName, ct);
    }

    public static async ValueTask<JobSettings?> TryGetJobSettingsFromMonitor(NamedPipeClientStream pipe, string jobName, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);

        Assert.That(jobName, Is.Not.Null.And.Not.Empty);
        buffer.ResetWrittenCount();

        MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new GetJobSettingsReq(jobName), cancellationToken: ct);
        await pipe.WriteAsync(buffer.WrittenMemory, ct);
        buffer.ResetWrittenCount();

        var readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
        Assert.That(readBytes > 0);
        buffer.Advance(readBytes);

        if (MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory,
            bytesRead: out var deseralizedBytes, cancellationToken: ct) is GetJobSettingsResp
            {
                JobName: var receivedJobName,
                JobSettings: var receivedJobSettings
            })
        {
            Assert.That(readBytes, Is.EqualTo(deseralizedBytes));
            Assert.That(receivedJobName, Is.EqualTo("").Or.EqualTo(jobName));

            return receivedJobName != "" ? receivedJobSettings : null;
        }
        else { throw new InvalidOperationException(); }
    }

    public static Task<(string JobName, JobSettings Settings)?> TryGetJobDataFromMonitor(uint processId, CancellationToken ct) =>
        TryGetJobDataFromMonitor(Program.PipeName, processId, ct);

    public static async Task<(string JobName, JobSettings Settings)?> TryGetJobDataFromMonitor(string pipeName, uint processId, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await pipe.ConnectAsync(500, ct);

        return await TryGetJobDataFromMonitor(pipe, processId, ct);
    }

    public static async Task<(string JobName, JobSettings Settings)?> TryGetJobDataFromMonitor(NamedPipeClientStream pipe, uint processId, CancellationToken ct)
    {
        var jobName = await TryGetJobNameFromMonitor(pipe, processId, ct);

        Assert.That(jobName, Is.Not.Null);
        if (jobName != "")
        {
            return (await TryGetJobSettingsFromMonitor(pipe, jobName, ct) is { } jobSettings) ? (jobName, jobSettings) : null;
        }
        else
        {
            return null;
        }
    }

    public static readonly SystemInfo RealSystemInfo = SystemInfoModule.GetSystemInfo();

    public static ProcessorGroup GetDefaultProcessorGroup()
    {
        var systemInfo = RealSystemInfo;

        Assert.That(systemInfo.NumaNodes, Has.Length.GreaterThan(0));
        Assert.That(systemInfo.NumaNodes[0].ProcessorGroups, Has.Length.GreaterThan(0));

        return systemInfo.NumaNodes[0].ProcessorGroups[0];
    }
}

