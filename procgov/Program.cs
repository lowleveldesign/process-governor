using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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
    const bool DebugOutput = true;
#else
    const bool DebugOutput = false;
#endif
    public static readonly TraceSource Logger = new("[procgov]", SourceLevels.Warning);

    public static int Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cts.Cancel(); };

        return ParseArgs(args) switch
        {
            ShowHelpAndExit em => Execute(em),

            _ => throw new NotImplementedException(),
        };

        static int Execute(ShowHelpAndExit em)
        {
            ShowHeader();
            if (em.ErrorMessage != "")
            {
                Console.WriteLine($"ERROR: {em.ErrorMessage}");
                Console.WriteLine();
            }
            ShowHelp();
            return em.ErrorMessage != "" ? 0xff : 0;
        }

        static int Execute(LaunchProcess em)
        {
            if (em.NoGui)
            {
                PInvoke.ShowWindow(PInvoke.GetConsoleWindow(), SHOW_WINDOW_CMD.SW_HIDE);
            }

            if (!em.Quiet)
            {
                Logger.Listeners.Add(new ConsoleTraceListener());

                ShowHeader();
                ShowLimits(em.JobSettings);
            }

            ProcessModule.StartProcessAndAssignToJobObject(procargs, session)
        }

        //if (nogui)
        //{
        //    PInvoke.ShowWindow(PInvoke.GetConsoleWindow(), SHOW_WINDOW_CMD.SW_HIDE);
        //}

        //try
        //{
        //    if (!quiet)
        //    {
        //        Logger.Listeners.Add(new ConsoleTraceListener());

        //        ShowHeader();
        //        ShowLimits(session);
        //    }

        //    using var job = session switch
        //    {
        //        //FIXME: to remove: _ when debug => ProcessModule.StartProcessUnderDebuggerAndAssignToJobObject(procargs, session),
        //        _ when pids.Length == 1 => ProcessModule.AssignProcessToJobObject(pids[0], session),
        //        _ when pids.Length > 1 => ProcessModule.AssignProcessesToJobObject(pids, session),
        //        _ => ProcessModule.StartProcessAndAssignToJobObject(procargs, session)
        //    };

        //    if (nowait)
        //    {
        //        return 0;
        //    }

        //    if (!quiet)
        //    {
        //        if (terminateJobOnExit)
        //        {
        //            Console.WriteLine("Press Ctrl-C to end execution and terminate the job.");
        //        }
        //        else
        //        {
        //            Console.WriteLine("Press Ctrl-C to end execution without terminating the process.");
        //        }
        //        Console.WriteLine();
        //    }

        //    Win32JobModule.WaitForTheJobToComplete(job, cts.Token);
        //    var exitCode = job.FirstProcessHandle is { } h && !h.IsInvalid ?
        //        ProcessModule.GetProcessExitCode(h) : 0;

        //    if (cts.Token.IsCancellationRequested && terminateJobOnExit)
        //    {
        //        Win32JobModule.TerminateJob(job, exitCode);
        //    }

        //    return (int)exitCode;
        //}
        //catch (Win32Exception ex)
        //{
        //    Console.Error.WriteLine("ERROR: {0}", (debugOutput ? ex.ToString() : $"{ex.Message} (0x{ex.ErrorCode:X})"));
        //    return 0xff;
        //}
        //catch (Exception ex)
        //{
        //    Console.Error.WriteLine("ERROR: {0}", (debugOutput ? ex.ToString() : ex.Message));
        //    return 0xff;
        //}
    }

    static void ShowHeader()
    {
        Console.WriteLine("Process Governor v{0} - sets limits on processes",
            Assembly.GetExecutingAssembly()!.GetName()!.Version!.ToString());
        Console.WriteLine("Copyright (C) 2024 Sebastian Solnica (lowleveldesign.org)");
        Console.WriteLine();
    }

    static void ShowHelp()
    {
        Console.WriteLine("""
            Usage: procgov [OPTIONS] args

            OPTIONS:

                -m|--maxmem=               Max committed memory usage in bytes (accepted suffixes: K, M, or G).
                   --maxjobmem=            Max committed memory usage for all the processes in the job (accepted suffixes: K, M, or G).
                   --maxws=                Max working set size in bytes (accepted suffixes: K, M, or G). Must be set with minws.
                   --minws=                Min working set size in bytes (accepted suffixes: K, M, or G). Must be set with maxws.
                   --env=                  A text file with environment variables (each line in form: VAR=VAL).
                -n|--node=                 The preferred NUMA node for the process.
                -c|--cpu=                  If in hex (starts with 0x) it is treated as an affinity mask, otherwise it is a number of CPU cores assigned to your app. If you also provide the NUMA node, this setting will apply only to this node.",
                -e|--cpurate=              The maximum CPU rate in % for the process. If you also set the affinity, he rate will apply only to the selected CPU cores. (Windows 8.1+)
                -b|--bandwidth=            The maximum bandwidth (in bytes) for the process outgoing network traffic (accepted suffixes: K, M, or G). (Windows 10+)
                -r|--recursive             Apply limits to child processes too (will wait for all processes to finish).
                   --newconsole            Start the process in a new console window.
                   --nogui                 Hide Process Governor console window (set always when installed as debugger).
                -p|--pid=                  Apply limits on an already running process (or processes if used multiple times)
                   --install               Install procgov as a service which monitors a specific process.
                -t|--timeout=              Kill the process (with -r, also all its children) if it does not finish within the specified time. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.
                   --process-utime=        Kill the process (with -r, also applies to its children) if it exceeds the given user-mode execution time. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.
                   --job-utime=            Kill the process (with -r, also all its children) if the total user-mode execution time exceed the specified value. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.
                   --uninstall             Uninstall procgov for a specific process.
                   --enable-privilege=     Enables the specified privileges in the remote process. You may specify multiple privileges by splitting them with commas, for example, 'SeDebugPrivilege,SeLockMemoryPrivilege'
                   --terminate-job-on-exit Terminates the job (and all its processes) when you stop procgov with Ctrl + C.
                -q|--quiet                 Do not show procgov messages.
                   --nowait                Does not wait for the target process(es) to exit.
                -v|--verbose               Show verbose messages in the console.
                -h|--help                  Show this message and exit.
                -?                         Show this message and exit.



            EXAMPLES:

            Limit memory of a test.exe process to 200MB:
            > procgov64 --maxmem 200M -- test.exe

            Limit CPU usage of a test.exe process to first three CPU cores:
            > procgov64 --cpu 3 -- test.exe -arg1 -arg2=val2
            
            Always run a test.exe process only on the first three CPU cores:
            > procgov64 --install --cpu 3 test.exe
            """);
    }

    public static void ShowLimits(JobSettings session)
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

    //public static string PrepareDebuggerCommandString(JobSettings session, string appImageExe, bool nowait)
    //{
    //    var buffer = new StringBuilder();
    //    var procgovPath = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
    //    buffer.Append('"').Append(procgovPath).Append('"').Append(" --nogui --debugger");

    //    if (session.AdditionalEnvironmentVars.Count > 0)
    //    {
    //        // we will create a file in the procgov folder with the environment variables 
    //        string appEnvironmentFilePath = GetAppEnvironmentFilePath(appImageExe);
    //        using (var writer = new StreamWriter(appEnvironmentFilePath, false))
    //        {
    //            foreach (var kv in session.AdditionalEnvironmentVars)
    //            {
    //                writer.WriteLine("{0}={1}", kv.Key, kv.Value);
    //            }
    //        }
    //        buffer.AppendFormat(" --env=\"{0}\"", appEnvironmentFilePath);
    //    }
    //    if (session.CpuAffinityMask != 0)
    //    {
    //        buffer.AppendFormat(" --cpu=0x{0:X}", session.CpuAffinityMask);
    //    }
    //    if (session.MaxProcessMemory > 0)
    //    {
    //        buffer.AppendFormat(" --maxmem={0}", session.MaxProcessMemory);
    //    }
    //    if (session.MaxJobMemory > 0)
    //    {
    //        buffer.AppendFormat(" --maxjobmem={0}", session.MaxJobMemory);
    //    }
    //    if (session.MaxWorkingSetSize > 0)
    //    {
    //        buffer.AppendFormat(" --maxws={0}", session.MaxWorkingSetSize);
    //    }
    //    if (session.MinWorkingSetSize > 0)
    //    {
    //        buffer.AppendFormat(" --minws={0}", session.MinWorkingSetSize);
    //    }
    //    if (session.NumaNode != 0xffff)
    //    {
    //        buffer.AppendFormat(" --node={0}", session.NumaNode);
    //    }
    //    if (session.CpuMaxRate > 0)
    //    {
    //        buffer.AppendFormat(" --cpurate={0}", session.CpuMaxRate);
    //    }
    //    if (session.MaxBandwidth > 0)
    //    {
    //        buffer.AppendFormat(" --bandwidth={0}", session.MaxBandwidth);
    //    }
    //    if (session.PropagateOnChildProcesses)
    //    {
    //        buffer.AppendFormat(" --recursive");
    //    }
    //    if (session.ClockTimeLimitInMilliseconds > 0)
    //    {
    //        buffer.AppendFormat(" --timeout={0}", session.ClockTimeLimitInMilliseconds);
    //    }
    //    if (session.ProcessUserTimeLimitInMilliseconds > 0)
    //    {
    //        buffer.AppendFormat(" --process-utime={0}", session.ProcessUserTimeLimitInMilliseconds);
    //    }
    //    if (session.JobUserTimeLimitInMilliseconds > 0)
    //    {
    //        buffer.AppendFormat(" --job-utime={0}", session.JobUserTimeLimitInMilliseconds);
    //    }
    //    if (session.Privileges.Length > 0)
    //    {
    //        buffer.AppendFormat(" --enable-privileges={0}", string.Join(',', session.Privileges));
    //    }
    //    if (nowait)
    //    {
    //        buffer.AppendFormat(" --nowait");
    //    }

    //    return buffer.ToString();
    //}
}
