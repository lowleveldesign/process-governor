using Microsoft.Win32;
using NDesk.Options;
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

public static class Program
{
#if DEBUG
    const bool debugOutput = true;
#else
    const bool debugOutput = false;
#endif
    public static readonly TraceSource Logger = new("[procgov]", SourceLevels.Warning);

    public static int Main(string[] args)
    {
        var procargs = new List<string>();
        bool showhelp = false, nogui = false, debug = false, quiet = false, nowait = false,
            terminateJobOnExit = false;
        int[] pids = Array.Empty<int>();
        var registryOperation = RegistryOperation.NONE;

        var session = new SessionSettings();

        var p = new OptionSet()
        {
                { "m|maxmem=", "Max committed memory usage in bytes (accepted suffixes: K, M, or G).",
                    v => session.MaxProcessMemory = ParseMemoryString(v) },
                { "maxjobmem=", "Max committed memory usage for all the processes in the job (accepted suffixes: K, M, or G).",
                    v => session.MaxJobMemory = ParseMemoryString(v) },
                { "maxws=", "Max working set size in bytes (accepted suffixes: K, M, or G). Must be set with minws.",
                    v => session.MaxWorkingSetSize = ParseMemoryString(v) },
                { "minws=", "Min working set size in bytes (accepted suffixes: K, M, or G). Must be set with maxws.",
                    v => session.MinWorkingSetSize = ParseMemoryString(v) },
                { "env=", "A text file with environment variables (each line in form: VAR=VAL).",
                    v => LoadCustomEnvironmentVariables(session, v) },
                { "n|node=", "The preferred NUMA node for the process.",
                    v => session.NumaNode = ushort.Parse(v) },
                { "c|cpu=", "If in hex (starts with 0x) it is treated as an affinity mask, otherwise it is a number of CPU cores assigned to your app. " +
                    "If you also provide the NUMA node, this setting will apply only to this node.",
                    v => session.CpuAffinityMask = v switch {
                        var s when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => ulong.Parse(s[2..], NumberStyles.HexNumber),
                        var s  => CalculateAffinityMaskFromCpuCount(int.Parse(s))
                    }
                },
                { "e|cpurate=", "The maximum CPU rate in % for the process. If you also set the affinity, " +
                    "the rate will apply only to the selected CPU cores. (Windows 8.1+)",
                    v => session.CpuMaxRate = ParseCpuRate(v) },
                { "b|bandwidth=", "The maximum bandwidth (in bytes) for the process outgoing network traffic" +
                    " (accepted suffixes: K, M, or G). (Windows 10+)",
                    v => session.MaxBandwidth = ParseByteLength(v) },
                { "r|recursive", "Apply limits to child processes too (will wait for all processes to finish).",
                    v => session.PropagateOnChildProcesses = v != null },
                { "newconsole", "Start the process in a new console window.", v => session.SpawnNewConsoleWindow = v != null },
                { "nogui", "Hide Process Governor console window (set always when installed as debugger).",
                    v => nogui = v != null },
                { "p|pid=", "Apply limits on an already running process (or processes)", v => pids = ParsePids(v) },
                { "install", "Install procgov as a debugger for a specific process using Image File Executions. " +
                             "DO NOT USE this option if the process you want to control starts child instances of itself (for example, Chrome).",
                    v => registryOperation = RegistryOperation.INSTALL },
                { "t|timeout=", "Kill the process (with -r, also all its children) if it does not finish within the specified time. " +
                                "Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.",
                    v => session.ClockTimeLimitInMilliseconds = ParseTimeStringToMilliseconds(v) },
                { "process-utime=", "Kill the process (with -r, also applies to its children) if it exceeds the given " +
                                "user-mode execution time. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.",
                    v => session.ProcessUserTimeLimitInMilliseconds = ParseTimeStringToMilliseconds(v) },
                { "job-utime=", "Kill the process (with -r, also all its children) if the total user-mode execution " +
                               "time exceed the specified value. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.",
                    v => session.JobUserTimeLimitInMilliseconds = ParseTimeStringToMilliseconds(v) },
                { "uninstall", "Uninstall procgov for a specific process.", v => registryOperation = RegistryOperation.UNINSTALL },
                { "enable-privileges=", "Enables the specified privileges in the remote process. You may specify multiple privileges " +
                               "by splitting them with commas, for example, 'SeDebugPrivilege,SeLockMemoryPrivilege'",
                    v => session.Privileges = v.Split(',', StringSplitOptions.RemoveEmptyEntries) },
                { "terminate-job-on-exit", "Terminates the job (and all its processes) when you stop procgov with Ctrl + C.",
                    v => terminateJobOnExit = true },
                { "debugger", "Internal - do not use.",
                    v => debug = v != null },
                { "q|quiet", "Do not show procgov messages.", v => quiet = v != null },
                { "nowait", "Does not wait for the target process(es) to exit.", v => nowait = v != null },
                { "v|verbose", "Show verbose messages in the console.", v => {
                    if (v != null)
                    {
                        Logger.Switch.Level = SourceLevels.Verbose;
                    }
                } },
                { "h|help", "Show this message and exit", v => showhelp = v != null },
                { "?", "Show this message and exit", v => showhelp = v != null }
            };

        try
        {
            procargs = p.Parse(args);
        }
        catch (OptionException ex)
        {
            Console.Error.Write("ERROR: invalid argument");
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            showhelp = true;
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("ERROR: invalid number in one of the constraints");
            Console.Error.WriteLine();
            showhelp = true;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("ERROR: {0}", ex.Message);
            Console.Error.WriteLine();
            showhelp = true;
        }

        if (terminateJobOnExit && nowait)
        {
            Console.Error.WriteLine("ERROR: --terminate-job-on-exit and --nowait cannot be used together.");
            return 0xff;
        }

        if (session.MaxWorkingSetSize != session.MinWorkingSetSize && Math.Min(session.MaxWorkingSetSize, session.MinWorkingSetSize) == 0)
        {
            Console.Error.WriteLine("ERROR: minws and maxws must be set together and be greater than 0.");
            return 0xff;
        }

        if (!showhelp && registryOperation != RegistryOperation.NONE)
        {
            if (procargs.Count == 0)
            {
                Console.Error.WriteLine("ERROR: please provide an image name for a process you would like to intercept.");
                return 0xff;
            }
            SetupRegistryForProcessGovernor(session, procargs[0], registryOperation, nowait);
            return 0;
        }

        if (!showhelp && (procargs.Count == 0 && pids.Length == 0) || (pids.Length > 0 && procargs.Count > 0))
        {
            Console.Error.WriteLine("ERROR: please provide either process name or PID(s) of the already running process(es)");
            Console.Error.WriteLine();
            showhelp = true;
        }

        if (showhelp)
        {
            ShowHeader();
            ShowHelp(p);
            return 0;
        }

        if (nogui)
        {
            PInvoke.ShowWindow(PInvoke.GetConsoleWindow(), SHOW_WINDOW_CMD.SW_HIDE);
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
                _ when debug => ProcessModule.StartProcessUnderDebuggerAndAssignToJobObject(procargs, session),
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

    public static uint ParseTimeStringToMilliseconds(string v)
    {
        if (v.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(v[0..^2]);
        }
        if (v.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(v[0..^1]) * 1000;
        }
        if (v.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(v[0..^1]) * 1000 * 60;
        }
        if (v.EndsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(v[0..^1]) * 1000 * 60 * 60;
        }
        return uint.Parse(v);
    }

    public static uint ParseCpuRate(string v)
    {
        var rate = uint.Parse(v);
        if (rate == 0 || rate > 100)
        {
            throw new ArgumentException("CPU rate must be between 1 and 100");
        }
        return rate;
    }
    public static ulong ParseByteLength(string v)
    {
        if (v == null)
        {
            return 0;
        }
        ulong result;
        if (v.EndsWith("K", StringComparison.OrdinalIgnoreCase))
        {
            result = ulong.Parse(v[0..^1]) << 10;
        }
        else if (v.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        {
            result = ulong.Parse(v[0..^1]) << 20;
        }
        else if (v.EndsWith("G", StringComparison.OrdinalIgnoreCase))
        {
            result = ulong.Parse(v[0..^1]) << 30;
        }
        else
        {
            result = ulong.Parse(v);
        }
        return result;
    }

    public static ulong ParseMemoryString(string v)
    {
        if (v == null)
        {
            return 0;
        }
        ulong result = ParseByteLength(v);
        if (IntPtr.Size == 4 && result > uint.MaxValue)
        {
            // 32 bit
            throw new ArgumentException("memory limit is too high for 32-bit architecture.");
        }
        return result;
    }

    public static int[] ParsePids(string v)
    {
        return v.Split(',').Select(x => int.Parse(x)).ToArray();
    }

    public static void LoadCustomEnvironmentVariables(SessionSettings session, string file)
    {
        if (file == null || !File.Exists(file))
        {
            throw new ArgumentException("the text file with environment variables does not exist");
        }
        try
        {
            using var reader = File.OpenText(file);
            int linenum = 1;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var ind = line.IndexOf("=");
                if (ind > 0)
                {
                    var key = line[..ind].Trim();
                    if (!string.IsNullOrEmpty(key))
                    {
                        var val = line.Substring(ind + 1, line.Length - ind - 1).Trim();
                        session.AdditionalEnvironmentVars.Add(key, val);
                        linenum++;
                        continue;
                    }
                }
                throw new ArgumentException(string.Format("the environment file contains invalid data (line: {0})", linenum));
            }
        }
        catch (IOException ex)
        {
            throw new ArgumentException("can't read the text file with environment variables, {0}", ex.Message);
        }
    }

    static void ShowHeader()
    {
        Console.WriteLine("Process Governor v{0} - sets limits on processes",
            Assembly.GetExecutingAssembly()!.GetName()!.Version!.ToString());
        Console.WriteLine("Copyright (C) 2023 Sebastian Solnica (lowleveldesign.org)");
        Console.WriteLine();
    }

    static void ShowHelp(OptionSet p)
    {
        Console.WriteLine("Usage: procgov [OPTIONS] args");
        Console.WriteLine();
        Console.WriteLine("Options:");
        p.WriteOptionDescriptions(Console.Out);
        Console.WriteLine();
        Console.WriteLine("EXAMPLES:");
        Console.WriteLine("Limit memory of a test.exe process to 200MB:");
        Console.WriteLine("> procgov64 --maxmem 200M -- test.exe");
        Console.WriteLine();
        Console.WriteLine("Limit CPU usage of a test.exe process to first three CPU cores:");
        Console.WriteLine("> procgov64 --cpu 3 -- test.exe -arg1 -arg2=val2");
        Console.WriteLine();
        Console.WriteLine("Always run a test.exe process only on the first three CPU cores:");
        Console.WriteLine("> procgov64 --install --cpu 3 test.exe");
        Console.WriteLine();
    }

    static void ShowLimits(SessionSettings session)
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

    public static ulong CalculateAffinityMaskFromCpuCount(int cpuCount)
    {
        ulong mask = 0;
        for (int i = 0; i < cpuCount; i++)
        {
            mask <<= 1;
            mask |= 0x1;
        }
        return mask;
    }

    private static bool IsUserAdmin()
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public enum RegistryOperation
    {
        INSTALL,
        UNINSTALL,
        NONE
    }

    public static void SetupRegistryForProcessGovernor(SessionSettings session, string appImageExe, RegistryOperation oper, bool nowait)
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

    public static string PrepareDebuggerCommandString(SessionSettings session, string appImageExe, bool nowait)
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
