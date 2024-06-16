using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Windows.Win32;

namespace ProcessGovernor;

static partial class Program
{
    // main pipe used to receive commands from the client and respond to them
    const string PipeName = "procgov";

    public static async Task<int> Execute(RunAsMonitor monitor, CancellationToken ct)
    {
        try
        {
            await StartProcgovPipe(ct);
            return 0;
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : -1;
        }
    }

    static async Task StartProcgovPipe(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(512);

        while (!ct.IsCancellationRequested)
        {
            // FIXME: set pipe security = current user + admins
            using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.WriteThrough | PipeOptions.Asynchronous);

            try
            {
                // Wait for a client to connect
                await pipe.WaitForConnectionAsync(ct);

                if (!PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid))
                {
                    var err = Marshal.GetLastWin32Error();
                    Logger.TraceEvent(TraceEventType.Warning, 0, $"[{PipeName}] failed to read client PID: {err:x}");
                }

                // assign to broadcast thread
                // start 

                while ()

                switch ((IpcMessage)pipe.ReadByte())
                {
                    case IpcMessage.AssignJobToObject:
                        var ss = new StreamString(pipe);
                        var message = await ss.ReadStringAsync();
                        Console.WriteLine($"Message received: {message}");
                        break;
                    default:
                        break;
                }

                var ss = new StreamString(pipe);
                receivedMessages.Add(await ss.ReadStringAsync());

                // When running with only 1 server instance, we could do the processing here, for example:
                // string message = ss.ReadString();
                // Console.WriteLine($"Message received: {message}");
            }
            // IOException that is raised if the pipe is broken or disconnected.
            catch (IOException ex)
            {
                Debug.WriteLine("[procgov monitor] broken named pipe: " + ex);
            }
            catch (Exception ex) when (ex is OperationCanceledException || (
                    ex is AggregateException && ex.InnerException is TaskCanceledException))
            {
                Debug.WriteLine("[procgov monitor] cancellation: " + ex);
            }
        }
    }
}
