using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

[assembly: InternalsVisibleTo("procgov-tests")]

namespace ProcessGovernor;

record JobSettings(
    ulong MaxProcessMemory,
    ulong MaxJobMemory,
    ulong MaxWorkingSetSize,
    ulong MinWorkingSetSize,
    ulong CpuAffinityMask,
    uint CpuMaxRate,
    ulong MaxBandwidth,
    uint ProcessUserTimeLimitInMilliseconds,
    uint JobUserTimeLimitInMilliseconds,
    uint ClockTimeLimitInMilliseconds,
    bool PropagateOnChildProcesses,
    ushort NumaNode = 0xffff
);

internal static partial class Program
{
#if DEBUG
    const bool debugOutput = true;
#else
    const bool debugOutput = false;
#endif
    public static readonly TraceSource Logger = new("[procgov]", SourceLevels.Warning);

    public static int Main(string[] args)
    {
        CmdLine.ParseArgs(args);

        if (showhelp)
        {
            ShowHeader();
            ShowHelp();
            // FIXME: should return error code if something failed
            return 0;
        }

        if (nogui)
        {
            PInvoke.ShowWindow(PInvoke.GetConsoleWindow(), SHOW_WINDOW_CMD.SW_HIDE);
        }

        if (registryOperation != RegistryOperation.NONE)
        {
            SetupRegistryForProcessGovernor(session, procargs[0], registryOperation, nowait);
            // FIXME: should return error code if something failed
            return 0;
        }

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cts.Cancel(); };

        try
        {
            if (!quiet)
            {
                Logger.Listeners.Add(new ConsoleTraceListener());

                ShowHeader();
                ShowLimits(session);
            }

            using var job = session switch
            {
                //FIXME: to remove: _ when debug => ProcessModule.StartProcessUnderDebuggerAndAssignToJobObject(procargs, session),
                _ when pids.Length == 1 => ProcessModule.AssignProcessToJobObject(pids[0], session),
                _ when pids.Length > 1 => ProcessModule.AssignProcessesToJobObject(pids, session),
                _ => ProcessModule.StartProcessAndAssignToJobObject(procargs, session)
            };

            if (nowait)
            {
                return 0;
            }

            if (!quiet)
            {
                if (terminateJobOnExit)
                {
                    Console.WriteLine("Press Ctrl-C to end execution and terminate the job.");
                }
                else
                {
                    Console.WriteLine("Press Ctrl-C to end execution without terminating the process.");
                }
                Console.WriteLine();
            }

            Win32JobModule.WaitForTheJobToComplete(job, cts.Token);
            var exitCode = job.FirstProcessHandle is { } h && !h.IsInvalid ?
                ProcessModule.GetProcessExitCode(h) : 0;

            if (cts.Token.IsCancellationRequested && terminateJobOnExit)
            {
                Win32JobModule.TerminateJob(job, exitCode);
            }

            return (int)exitCode;
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine("ERROR: {0}", (debugOutput ? ex.ToString() : $"{ex.Message} (0x{ex.ErrorCode:X})"));
            return 0xff;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: {0}", (debugOutput ? ex.ToString() : ex.Message));
            return 0xff;
        }

    }

    static void ShowLimits(JobSettings session)
    {
        if (session.CpuAffinityMask != 0)
        {
            Console.WriteLine($"CPU affinity mask:                          0x{session.CpuAffinityMask:X}");
        }
        if (session.CpuMaxRate > 0)
        {
            Console.WriteLine($"Max CPU rate:                               {session.CpuMaxRate}%");
        }
        if (session.MaxBandwidth > 0)
        {
            Console.WriteLine($"Max bandwidth (B):                          {(session.MaxBandwidth):#,0}");
        }
        if (session.MaxProcessMemory > 0)
        {
            Console.WriteLine($"Maximum committed memory (MB):              {(session.MaxProcessMemory / 1048576):0,0}");
        }
        if (session.MaxJobMemory > 0)
        {
            Console.WriteLine($"Maximum job committed memory (MB):          {(session.MaxJobMemory / 1048576):0,0}");
        }
        if (session.MinWorkingSetSize > 0)
        {
            Debug.Assert(session.MaxWorkingSetSize > 0);
            Console.WriteLine($"Minimum WS memory (MB):                     {(session.MinWorkingSetSize / 1048576):0,0}");
        }
        if (session.MaxWorkingSetSize > 0)
        {
            Debug.Assert(session.MinWorkingSetSize > 0);
            Console.WriteLine($"Maximum WS memory (MB):                     {(session.MaxWorkingSetSize / 1048576):0,0}");
        }
        if (session.NumaNode != 0xffff)
        {
            Console.WriteLine($"Preferred NUMA node:                        {session.NumaNode}");
        }
        if (session.ProcessUserTimeLimitInMilliseconds > 0)
        {
            Console.WriteLine($"Process user-time execution limit (ms):     {session.ProcessUserTimeLimitInMilliseconds:0,0}");

        }
        if (session.JobUserTimeLimitInMilliseconds > 0)
        {
            Console.WriteLine($"Job user-time execution limit (ms):         {session.JobUserTimeLimitInMilliseconds:0,0}");
        }
        if (session.ClockTimeLimitInMilliseconds > 0)
        {
            Console.WriteLine($"Clock-time execution limit (ms):            {session.ClockTimeLimitInMilliseconds:0,0}");
        }
        if (session.PropagateOnChildProcesses)
        {
            Console.WriteLine();
            Console.WriteLine("All configured limits will also apply to the child processes.");
        }
        Console.WriteLine();
    }

    private static bool IsUserAdmin()
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void SetupRegistryForProcessGovernor(JobSettings session, string appImageExe, RegistryOperation oper, bool nowait)
    {
        if (!IsUserAdmin())
        {
            Console.Error.WriteLine("You must be admin to do that. Run the app from the administrative console.");
            return;
        }
        // extrace image.exe if path is provided
        appImageExe = Path.GetFileName(appImageExe);
        var regkey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", true)!;
        // add to image file execution path
        if (oper == RegistryOperation.INSTALL)
        {
            regkey = regkey.CreateSubKey(appImageExe);
            regkey.SetValue("Debugger", PrepareDebuggerCommandString(session, appImageExe, nowait));
        }
        else if (oper == RegistryOperation.UNINSTALL)
        {
            regkey.DeleteSubKey(appImageExe, false);

            var appEnvironmentFilePath = GetAppEnvironmentFilePath(appImageExe);
            if (File.Exists(appEnvironmentFilePath))
            {
                File.Delete(appEnvironmentFilePath);
            }
        }
    }

    public static string PrepareDebuggerCommandString(JobSettings session, string appImageExe, bool nowait)
    {
        var buffer = new StringBuilder();
        var procgovPath = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        buffer.Append('"').Append(procgovPath).Append('"').Append(" --nogui --debugger");

        if (session.AdditionalEnvironmentVars.Count > 0)
        {
            // we will create a file in the procgov folder with the environment variables 
            string appEnvironmentFilePath = GetAppEnvironmentFilePath(appImageExe);
            using (var writer = new StreamWriter(appEnvironmentFilePath, false))
            {
                foreach (var kv in session.AdditionalEnvironmentVars)
                {
                    writer.WriteLine("{0}={1}", kv.Key, kv.Value);
                }
            }
            buffer.AppendFormat(" --env=\"{0}\"", appEnvironmentFilePath);
        }
        if (session.CpuAffinityMask != 0)
        {
            buffer.AppendFormat(" --cpu=0x{0:X}", session.CpuAffinityMask);
        }
        if (session.MaxProcessMemory > 0)
        {
            buffer.AppendFormat(" --maxmem={0}", session.MaxProcessMemory);
        }
        if (session.MaxJobMemory > 0)
        {
            buffer.AppendFormat(" --maxjobmem={0}", session.MaxJobMemory);
        }
        if (session.MaxWorkingSetSize > 0)
        {
            buffer.AppendFormat(" --maxws={0}", session.MaxWorkingSetSize);
        }
        if (session.MinWorkingSetSize > 0)
        {
            buffer.AppendFormat(" --minws={0}", session.MinWorkingSetSize);
        }
        if (session.NumaNode != 0xffff)
        {
            buffer.AppendFormat(" --node={0}", session.NumaNode);
        }
        if (session.CpuMaxRate > 0)
        {
            buffer.AppendFormat(" --cpurate={0}", session.CpuMaxRate);
        }
        if (session.MaxBandwidth > 0)
        {
            buffer.AppendFormat(" --bandwidth={0}", session.MaxBandwidth);
        }
        if (session.PropagateOnChildProcesses)
        {
            buffer.AppendFormat(" --recursive");
        }
        if (session.ClockTimeLimitInMilliseconds > 0)
        {
            buffer.AppendFormat(" --timeout={0}", session.ClockTimeLimitInMilliseconds);
        }
        if (session.ProcessUserTimeLimitInMilliseconds > 0)
        {
            buffer.AppendFormat(" --process-utime={0}", session.ProcessUserTimeLimitInMilliseconds);
        }
        if (session.JobUserTimeLimitInMilliseconds > 0)
        {
            buffer.AppendFormat(" --job-utime={0}", session.JobUserTimeLimitInMilliseconds);
        }
        if (session.Privileges.Length > 0)
        {
            buffer.AppendFormat(" --enable-privileges={0}", string.Join(',', session.Privileges));
        }
        if (nowait)
        {
            buffer.AppendFormat(" --nowait");
        }

        return buffer.ToString();
    }

    public static string GetAppEnvironmentFilePath(string appImageExe)
    {
        var procgovPath = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        return Path.Combine(Path.GetDirectoryName(procgovPath)!, appImageExe + ".env");
    }
}
