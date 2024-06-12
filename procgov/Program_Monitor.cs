using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ProcessGovernor;

static partial class Program
{
    const string PipeName = "procgov";
    static readonly Buffer

    public static async Task<int> Execute(RunAsMonitor monitor, CancellationToken ct)
    {
        try
        {
            await StartNamedPipeServer(ct);
            return 0;
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : -1;
        }
    }

    static async Task StartNamedPipeServer(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // FIXME: set pipe security = current user + admins
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 5, PipeTransmissionMode.Byte,
                PipeOptions.WriteThrough | PipeOptions.Asynchronous);

            try
            {
                // Wait for a client to connect
                await pipe.WaitForConnectionAsync(ct);

                // Read the request from the client. Once the client has
                // written to the pipe its security token will be available.
                StreamString ss = new(pipe);

                if (PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid))
                {
                    Console.WriteLine($"Client PID: {pid}");
                }
                _ = ClientThread(pipe);

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

        async Task ClientThread(NamedPipeServerStream pipe, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
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
                }
            }
            finally
            {
                pipe.Close();
            }
        }
    }
}
