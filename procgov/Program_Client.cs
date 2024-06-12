using System.Diagnostics;
using System.Reflection;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ProcessGovernor;

static partial class Program
{
    public static int Execute(LaunchProcess launch, CancellationToken ct)
    {
        if (launch.NoGui)
        {
            PInvoke.ShowWindow(PInvoke.GetConsoleWindow(), SHOW_WINDOW_CMD.SW_HIDE);
        }

        if (!launch.Quiet)
        {
            Logger.Listeners.Add(new ConsoleTraceListener());

            ShowHeader();
            ShowLimits(launch.JobSettings);
        }

        using var targetProcess = ProcessModule.CreateSuspendedProcess(launch);

        // Launch monitor process
        using var monitorProcess = Process.Start(Environment.ProcessPath!, "--monitor");

        // FIXME: wait for the named pipe and use messages to configure the job settings
        // FIXME: wait for message asynchronously

        ProcessModule.ResumeProcess(targetProcess);

        if (launch.ExitBehavior == ExitBehavior.DontWaitForJobCompletion)
        {
            return 0;
        }

        if (!launch.Quiet)
        {
            if (launch.ExitBehavior == ExitBehavior.TerminateJobOnExit)
            {
                Console.WriteLine("Press Ctrl-C to end execution and terminate the job.");
            }
            else
            {
                Console.WriteLine("Press Ctrl-C to end execution without terminating the process.");
            }
            Console.WriteLine();
        }

        Win32JobModule.WaitForTheJobToComplete(job, ct);
        var exitCode = job.FirstProcessHandle is { } h && !h.IsInvalid ? ProcessModule.GetProcessExitCode(h) : 0;

        if (ct.IsCancellationRequested && launch.ExitBehavior == ExitBehavior.TerminateJobOnExit)
        {
            Win32JobModule.TerminateJob(job, exitCode);
        }

        return (int)exitCode;
    }

    static void ShowHeader()
    {
        Console.WriteLine("Process Governor v{0} - sets limits on processes",
            Assembly.GetExecutingAssembly()!.GetName()!.Version!.ToString());
        Console.WriteLine("Copyright (C) 2024 Sebastian Solnica");
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
}
