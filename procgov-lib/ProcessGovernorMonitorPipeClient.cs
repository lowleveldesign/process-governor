using Nerdbank.MessagePack;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

namespace ProcessGovernor.Library;

internal sealed class ProcessGovernorMonitorPipeClient
{
    sealed record IncomingRequest(IMonitorRequest Request, TaskCompletionSource<IMonitorResponse> PendingResponse);

    private readonly Channel<IncomingRequest> incomingRequests = Channel.CreateUnbounded<IncomingRequest>();
    private readonly Channel<IMonitorNotification> notifications = Channel.CreateBounded<IMonitorNotification>(
        new BoundedChannelOptions(30)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly CancellationToken cancellationToken;
    private readonly Task pipeReaderWriterTask;

    public ProcessGovernorMonitorPipeClient(NamedPipeClientStream pipe, CancellationToken ct)
    {
        cancellationToken = ct;
        pipeReaderWriterTask = RunBackgroundTask(async (ct) =>
        {
            var requestsReader = incomingRequests.Reader;
            var notificationsSink = notifications.Writer;

            Task<IncomingRequest>? requestTask = null;

            var pipeTaskBuffer = new ArrayBufferWriter<byte>(1024);
            Task<int>? pipeTask = null;

            IncomingRequest? lastRequest = null;

            try
            {
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    pipeTask ??= pipe.ReadAsync(pipeTaskBuffer.GetMemory(), ct).AsTask();
                    requestTask ??= requestsReader.ReadAsync(ct).AsTask();

                    if (await Task.WhenAny(pipeTask, requestTask) == pipeTask)
                    {
                        if (pipeTask.Result == 0) { break; } // disconnected pipe
                        HandleMonitorResponse(pipeTask.Result);
                        pipeTask = null;
                    }
                    else
                    {
                        lastRequest = requestTask.Result;
                        await SendLastRequest();
                        requestTask = null;
                    }
                }

                while (requestsReader.TryRead(out var request))
                {
                    if (ct.IsCancellationRequested) { request.PendingResponse.SetCanceled(ct); }
                    else { request.PendingResponse.SetException(new IOException("Monitor pipe disconnected")); }
                }

                Logger.TraceVerbose("[ext_monitor_client] Client task finished gracefully.");
            }
            catch (Exception ex) when (ex.IsCancelledException())
            {
                Logger.TraceVerbose("[ext_monitor_client] Client task was cancelled.");
                while (requestsReader.TryRead(out var request))
                {
                    request.PendingResponse.SetCanceled(ct);
                }
            }
            catch (Exception ex)
            {
                Logger.TraceWarning("[ext_monitor_client] Client task terminated with exception: {0}", ex);
                while (requestsReader.TryRead(out var request))
                {
                    request.PendingResponse.SetException(ex);
                }
            }


            void HandleMonitorResponse(int bytesRead)
            {
                pipeTaskBuffer.Advance(bytesRead);

                var msgPackReader = new MessagePackReader(pipeTaskBuffer.WrittenMemory);
                while (!msgPackReader.End)
                {
                    switch (MsgPackSerializer.Deserialize<IMonitorResponse>(ref msgPackReader, ct))
                    {
                        case IMonitorNotification notification:
                            notificationsSink.TryWrite(notification);
                            break;
                        case { } response:
                            lastRequest?.PendingResponse.SetResult(response);
                            lastRequest = null;
                            break;
                        default:
                            Debug.Assert(false);
                            break;
                    }
                }

                pipeTaskBuffer.ResetWrittenCount();
            }

            async Task SendLastRequest()
            {
                try
                {
                    Debug.Assert(lastRequest is not null);
                    await MsgPackSerializer.SerializeAsync(pipe, lastRequest.Request, cancellationToken: ct);
                }
                catch (Exception ex) when (ex.IsCancelledException())
                {
                    lastRequest.PendingResponse.SetCanceled(ct);
                    lastRequest = null;
                }
                catch (Exception ex)
                {
                    lastRequest.PendingResponse.SetException(ex);
                    lastRequest = null;
                }
            }
        }, ct);
    }

    public ChannelReader<IMonitorNotification> NotificationsReader => notifications;

    public Task<IMonitorResponse> SendRequest(IMonitorRequest request)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IMonitorResponse>(cancellationToken);
        }

        var tcs = new TaskCompletionSource<IMonitorResponse>();
        incomingRequests.Writer.TryWrite(new IncomingRequest(request, tcs));
        return tcs.Task;
    }

    public async Task WaitForStop()
    {
        await pipeReaderWriterTask;
    }
}
