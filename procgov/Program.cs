using ProcessGovernor.Library;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace ProcessGovernor;

public static partial class Program
{
    public static readonly TraceSource Logger = new("[procgov]", SourceLevels.Warning);

    public static readonly TimeSpan DefaultMaxMonitorIdleTime = TimeSpan.FromSeconds(5);

    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cts.Cancel(); };

#if DEBUG
        TraceListener[] listeners = [new EtwTraceListener(), new DefaultTraceListener()];
#else
        TraceListener[] listeners = [new EtwTraceListener()];
#endif
        Logger.Listeners.Clear();
        Logger.Listeners.AddRange(listeners);
        ProcessGovernorLibraryApi.SetLibraryLoggerListeners(listeners);

        try
        {
            return ParseArgs(SystemInfoModule.GetSystemInfo(), args) switch
            {
                ShowSystemInfoAndExit em => Execute(em),
                ShowHelpAndExit em => Execute(em),
                RunAsCmdApp em => await Execute(em, cts.Token),
                RunAsMonitor em => await Execute(em, cts.Token),
                RunAsService em => Execute(em),
                SetupProcessGovernance em => Execute(em),
                RemoveProcessGovernance em => Execute(em),
                RemoveAllProcessGovernance em => Execute(em),
                _ => throw new NotImplementedException(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            return 0xff;
        }
    }

    public static int Execute(ShowHelpAndExit em)
    {
        ShowHeader();
        if (em.ErrorMessage != "")
        {
            Console.WriteLine($"ERROR: {em.ErrorMessage}.");
            Console.WriteLine();
        }
        ShowHelp();
        return em.ErrorMessage != "" ? 0xff : 0;


        static void ShowHelp()
        {
            Console.WriteLine("""
            Usage: procgov [OPTIONS] args

            OPTIONS:

                   --job-name=             The job name that will be used to identify the Process Governor job 
                                           in the system (auto-generated if not set).
                -m|--maxmem=               Max committed memory usage in bytes (accepted suffixes: K, M, or G).
                   --maxjobmem=            Max committed memory usage for all the processes in the job
                                           (accepted suffixes: K, M, or G).
                   --maxws=                Max working set size in bytes (accepted suffixes: K, M, or G).
                                           Must be set with minws.
                   --minws=                Min working set size in bytes (accepted suffixes: K, M, or G).
                                           Must be set with maxws.
                   --env=                  A text file with environment variables (each line in form: VAR=VAL).
                -c|--cpu=                  If in hex (starts with 0x) it is treated as an affinity mask, otherwise
                                           it is a number of CPU cores assigned to your app. If you also provide
                                           the NUMA node, this setting will apply only to this node.
                -e|--cpurate=              The maximum CPU rate in % for the process. If you also set the affinity,
                                           the rate will apply only to the selected CPU cores. (Windows 8.1+)
                -b|--bandwidth=            The maximum bandwidth (in bytes) for the process outgoing network traffic
                                           (accepted suffixes: K, M, or G). (Windows 10+)
                -r|--recursive             Apply limits to child processes too (will wait for all processes to finish).
                   --newconsole            Start the process in a new console window.
                   --nogui                 Hide Process Governor console window.
                -p|--pid=                  Apply limits on an already running process (or processes if used multiple times)
                -t|--timeout=              Kill the process (with -r, also all its children) if it does not finish within 
                                           the specified time. Add suffix to define the time unit. Valid suffixes are: 
                                           ms, s, m, h.
                   --process-utime=        Kill the process (with -r, also applies to its children) if it exceeds the given
                                           user-mode execution time. Add suffix to define the time unit. Valid suffixes are:
                                           ms, s, m, h.
                   --priority=             Sets the process priority class of monitored processes. Possible values: Idle, 
                                           BelowNormal, Normal, AboveNormal, High, RealTime.
                   --efficiency-mode=      Sets or unsets the efficiency mode. Possible values: auto, on, off.
                   --job-utime=            Kill the process (with -r, also all its children) if the total user-mode 
                                           execution time exceed the specified value. Add suffix to define the time unit.
                                           Valid suffixes are: ms, s, m, h.
                   --max-process-count     The maximum number of processes in the job.
                   --unset-limits          Unsets all the configured job limits.

                   --freeze                Freezes (suspends) the process or group of processes (EXPERIMENTAL).
                   --thaw                  Thaws (resumes) the process or group of processes (EXPERIMENTAL).

                   --enable-privilege=     Enables the specified privileges in the remote process. You may specify multiple 
                                           privileges by splitting them with commas.
                   --terminate-job-on-exit Terminates the job (and all its processes) when you stop procgov with Ctrl + C.

                   --install               Install procgov as a service which monitors a specific process.
                   --isolate               For installed processes, if the procgov service detects a process that should
                                           be monitored, it should create a seperate job, and not add a new process to the
                                           existing job created before for a process with the same executable (incompatible
                                           with --job-name).
                   --service-path          The path where the service will be installed (C:\Program Files\ProcessGovernor)
                   --service-username      The username for the service account (default: NT AUTHORITY\\SYSTEM).
                   --service-password      The password for the service account (required for non-system accounts).
                   --uninstall             Uninstall procgov for a specific process.
                   --uninstall-all         Uninstall procgov completely (removing all saved process settings).

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
    }
    public static int Execute(ShowSystemInfoAndExit em)
    {
        ShowHeader();

        Console.WriteLine("Use --help to print the available options.");
        Console.WriteLine();

        var (physicalTotalKb, physicalAvailableKb, commitTotalKb, commitLimitKb) = SystemInfoModule.GetSystemPerformanceInformation();

        Console.WriteLine("=== SYSTEM INFORMATION ===");
        Console.WriteLine();

        var (numaNodes, processorGroups, cpuCores) = SystemInfoModule.GetSystemInfo();

        foreach (var numaNode in numaNodes)
        {
            Console.WriteLine($"NUMA Node {numaNode.Number}:");
            foreach (var processorGroup in numaNode.ProcessorGroups)
            {
                var groupCpus = string.Join(',', CreateRanges([..cpuCores.Index().Where(
                    ii => ii.Item.ProcessorGroup.Number == processorGroup.Number
                        && (ii.Item.ProcessorGroup.AffinityMask & processorGroup.AffinityMask) != 0).Select(ii => ii.Index)]).Select(
                    r => r.Start.Equals(r.End) ? $"{r.Start}" : $"{r.Start}-{r.End}"));
                Console.WriteLine($"  Processor Group {processorGroup.Number}: {(processorGroup.AffinityMask):X16} (CPUs: {groupCpus})");
            }
            Console.WriteLine();
        }

        const int ONE_KB = 1024;
        Console.WriteLine($"Total Physical Memory (MB):          {physicalTotalKb / ONE_KB:0,0}");
        Console.WriteLine($"Available Physical Memory (MB):      {physicalAvailableKb / ONE_KB:0,0}");
        Console.WriteLine($"Total Committed Memory (MB):         {commitTotalKb / ONE_KB:0,0}");
        Console.WriteLine($"Current Committed Memory Limit (MB): {commitLimitKb / ONE_KB:0,0}");
        Console.WriteLine();

        return 0;

        static List<Range> CreateRanges(int[] nums)
        {
            Debug.Assert(nums.Length > 0);
            List<Range> result = [];
            int start = nums[0];
            for (int i = 1; i < nums.Length; i++)
            {
                if (nums[i] - nums[i - 1] > 1)
                {
                    result.Add(new(start, nums[i - 1]));
                    start = nums[i];
                }
            }
            result.Add(new(start, nums[^1]));
            return result;
        }
    }

    static void ShowHeader()
    {
        Console.WriteLine("Process Governor v{0} - sets limits on processes",
            Assembly.GetExecutingAssembly()!.GetName()!.Version!.ToString());
        Console.WriteLine("Copyright (C) Sebastian Solnica");
        Console.WriteLine();
    }

    public static IExecutionMode ParseArgs(SystemInfo systemInfo, string[] rawArgs)
    {
        try
        {
            var parsedArgs = ParseRawArgs(["newconsole", "r", "recursive", "newconsole", "nogui", "install", "uninstall",
                "terminate-job-on-exit", "background", "service", "q", "quiet", "nowait", "v", "verbose",
                "monitor", "uninstall-all" , "h", "?", "help", "freeze", "thaw", "unset-limits"], rawArgs);

            if (parsedArgs.Remove("v") || parsedArgs.Remove("verbose"))
            {
                Logger.Switch.Level = SourceLevels.Verbose;
                ProcessGovernorLibraryApi.SetLibraryLoggerLevel(SourceLevels.Verbose);
            }

            if (parsedArgs.Count == 0)
            {
                return new ShowSystemInfoAndExit();
            }

            if (parsedArgs.Remove("h") || parsedArgs.Remove("?") || parsedArgs.Remove("help"))
            {
                return new ShowHelpAndExit("");
            }

            var maxProcessMemory = parsedArgs.Remove("maxmem", out var v) || parsedArgs.Remove("m", out v) ? ParseMemoryString(v[^1]) : 0;
            var maxJobMemory = parsedArgs.Remove("maxjobmem", out v) ? ParseMemoryString(v[^1]) : 0;
            var maxWorkingSetSize = parsedArgs.Remove("maxws", out v) ? ParseMemoryString(v[^1]) : 0;
            var minWorkingSetSize = parsedArgs.Remove("minws", out v) ? ParseMemoryString(v[^1]) : 0;
            var cpuAffinity = parsedArgs.Remove("c", out v) || parsedArgs.Remove("cpu", out v) ?
                (v is [var s] && int.TryParse(s, out var cpuCount) ? CalculateAffinityMaskFromCpuCount(cpuCount) :
                    SystemInfoModule.ParseCpuAffinity(systemInfo, v)) : [];
            var cpuMaxRate = parsedArgs.Remove("e", out v) || parsedArgs.Remove("cpurate", out v) ? ParseCpuRate(v[^1]) : 0;
            var maxBandwidth = parsedArgs.Remove("b", out v) || parsedArgs.Remove("bandwidth", out v) ? ParseByteLength(v[^1]) : 0;
            var processUserTimeLimitInMilliseconds = parsedArgs.Remove("process-utime", out v) ? ParseTimeStringToMilliseconds(v[^1]) : 0;
            var jobUserTimeLimitInMilliseconds = parsedArgs.Remove("job-utime", out v) ? ParseTimeStringToMilliseconds(v[^1]) : 0;
            var clockTimeLimitInMilliseconds = parsedArgs.Remove("t", out v) || parsedArgs.Remove("timeout", out v) ? ParseTimeStringToMilliseconds(v[^1]) : 0;
            var propagateOnChildProcesses = parsedArgs.Remove("r") || parsedArgs.Remove("recursive");
            var activeProcessLimit = parsedArgs.Remove("max-process-count", out v) ? uint.Parse(v[^1]) : 0u;
            var requestedJobName = parsedArgs.Remove("job-name", out v) ? v[^1] : null;
            var priorityClass = parsedArgs.Remove("priority", out v) ? Enum.Parse<PriorityClass>(v[^1], ignoreCase: true) : PriorityClass.Undefined;
            var efficiencyMode = parsedArgs.Remove("efficiency-mode", out v) ? Enum.Parse<EfficiencyMode>(v[^1], ignoreCase: true) : EfficiencyMode.Undefined;
            var nowait = parsedArgs.Remove("nowait");

            // from: https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information
            // Set CpuRate to a percentage times 100. For example, to let the job use 20% of the CPU, set CpuRate to 2,000.
            cpuMaxRate *= 100;

            if (cpuAffinity is not [] && cpuMaxRate != 0)
            {

                // CPU rate is set for the whole system so includes all the logical CPUs. When 
                // we have the CPU affinity set, we will divide the rate accordingly.
                var numberOfSelectedCores = cpuAffinity.Sum(aff => BitOperations.PopCount(aff.Affinity));
                Debug.Assert(numberOfSelectedCores < systemInfo.CpuCores.Length);
                cpuMaxRate /= (uint)(systemInfo.CpuCores.Length / numberOfSelectedCores);
            }

            if (maxWorkingSetSize != minWorkingSetSize &&
                Math.Min(maxWorkingSetSize, minWorkingSetSize) == 0)
            {
                throw new ArgumentException("minws and maxws must be set together and be greater than 0");
            }

            if (nowait && clockTimeLimitInMilliseconds > 0)
            {
                throw new ArgumentException("--nowait cannot be used with --timeout");
            }

            bool runIsolated = parsedArgs.Remove("isolate");
            if (runIsolated && requestedJobName is not null)
            {
                throw new ArgumentException("--isolate is incompatible with --job-name - you need to use either one");
            }

            var environment = parsedArgs.Remove("env", out v) ? GetCustomEnvironmentVariables(v[^1]) : [];

            ImmutableArray<string> privileges = parsedArgs.Remove("enable-privilege", out v) ? [.. v] : [];
            if (parsedArgs.Remove("enable-privileges", out v)) // legacy name of this argument
            {
                privileges = [.. privileges, .. v];
            }
            // recommended way is to use the parameter multiple times, but for legacy reasons
            // we also support comma-separated list
            privileges = [.. privileges.SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries))];

            // jobSettings may be null when user does not want to change the existing limits
            JobSettings? jobSettings = new JobSettings(maxProcessMemory, maxJobMemory, maxWorkingSetSize, minWorkingSetSize,
                cpuAffinity, cpuMaxRate, maxBandwidth, processUserTimeLimitInMilliseconds, jobUserTimeLimitInMilliseconds,
                clockTimeLimitInMilliseconds, propagateOnChildProcesses, activeProcessLimit, priorityClass,
                GetRunMode(requestedJobName, runIsolated), privileges, environment, efficiencyMode) is var j
                && j.IsEmpty() && !parsedArgs.Remove("unset-limits") ? null : j;

            LaunchConfig launchConfig = LaunchConfig.Default;

            launchConfig |= parsedArgs.Remove("nogui") ? LaunchConfig.NoGui : 0;
            launchConfig |= parsedArgs.Remove("q") || parsedArgs.Remove("quiet") ? LaunchConfig.Quiet : 0;

            var startBehavior = (parsedArgs.Remove("thaw"), parsedArgs.Remove("freeze")) switch
            {
                (true, true) => throw new ArgumentException("--thaw and --freeze cannot be set at the same time"),
                (var thaw, var freeze) when (thaw || freeze) && requestedJobName is null =>
                    throw new ArgumentException("--job-name is required when using --thaw or --freeze"),
                (true, false) => StartBehavior.Thaw,
                (false, true) => StartBehavior.Freeze,
                _ => StartBehavior.None
            };

            var exitBehavior = parsedArgs.Remove("terminate-job-on-exit") switch
            {
                true when nowait =>
                    throw new ArgumentException("--terminate-job-on-exit and --nowait cannot be used together"),
                true => ExitBehavior.TerminateJobOnExit,
                false when nowait && jobSettings?.JobClockTimeLimitInMilliseconds > 0 =>
                    throw new ArgumentException("--nowait cannot be used with --timeout"),
                false when nowait => ExitBehavior.DontWaitForJobCompletion,
                _ => ExitBehavior.WaitForJobCompletion
            };

            var newConsole = parsedArgs.Remove("newconsole");

            var procargs = parsedArgs.Remove("", out v) ? v : [];

            var pidsToParse = parsedArgs.Remove("p", out v) ? v : [];
            if (parsedArgs.Remove("pid", out v))
            {
                pidsToParse = [.. pidsToParse.Union(v)];
            }
            // recommended way is to use the parameter multiple times, but for legacy reasons
            // we also support comma-separated list
            var pids = pidsToParse.SelectMany(p => p.Split(
                ',', StringSplitOptions.RemoveEmptyEntries)).Select(uint.Parse).Distinct().ToArray();

            var runAsMonitor = parsedArgs.Remove("monitor");
            var runAsService = parsedArgs.Remove("service");
            var install = parsedArgs.Remove("install");

            var serviceUserName = install switch
            {
                true => parsedArgs.Remove("service-username", out v) && v is [.., var username] ? username : "NT AUTHORITY\\SYSTEM",
                false => ""
            };
            var serviceUserPassword = install switch
            {
                true => parsedArgs.Remove("service-password", out v) && v is [.., var password] ? password : null,
                false => null
            };
            var serviceInstallPath = parsedArgs.Remove("service-path", out v) && v is [.., var servicePath] ? servicePath :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ServiceName);
            var uninstall = parsedArgs.Remove("uninstall");
            var uninstallAll = parsedArgs.Remove("uninstall-all");

            if (parsedArgs.Count > 0)
            {
                throw new ArgumentException("unrecognized arguments: " + string.Join(", ", parsedArgs.Keys));
            }

            return (procargs, pids, runAsMonitor, runAsService, install, uninstall, uninstallAll) switch
            {
                ([], [], true, false, false, false, false) => new RunAsMonitor(DefaultMaxMonitorIdleTime, launchConfig.HasFlag(LaunchConfig.NoGui)),
                ([], [], false, true, false, false, false) => new RunAsService(),
                ([var executable], [], false, false, true, false, false) when jobSettings is not null =>
                    new SetupProcessGovernance(jobSettings, executable, serviceInstallPath, serviceUserName, serviceUserPassword),
                ([var executable], [], false, false, false, true, false) => new RemoveProcessGovernance(executable, serviceInstallPath),
                ([], [], false, false, false, false, true) => new RemoveAllProcessGovernance(serviceInstallPath),
                (_, [], false, false, false, false, false) when procargs.Count > 0 =>
                    new RunAsCmdApp(jobSettings, new LaunchProcess(procargs, newConsole), launchConfig, startBehavior, exitBehavior),
                ([], _, false, false, false, false, false) when pids.Length > 0 =>
                    new RunAsCmdApp(jobSettings, new AttachToProcess(pids), launchConfig, startBehavior, exitBehavior),
                _ => throw new ArgumentException("invalid arguments provided")
            };
        }
        catch (FormatException)
        {
            return new ShowHelpAndExit("invalid number in one of the constraints");
        }
        catch (ArgumentException ex)
        {
            return new ShowHelpAndExit(ex.Message);
        }

        static IRunMode GetRunMode(string? jobName, bool runIsolated) => jobName is null ?
            (runIsolated ? RunModes.IsolatedJob : RunModes.SharedJob) : RunModes.NamedJob(jobName);

        ImmutableArray<GroupAffinity> CalculateAffinityMaskFromCpuCount(int cpuCount)
        {
            if (systemInfo.CpuCores.Length < cpuCount)
            {
                throw new ArgumentException($"you can't set affinity to more than {systemInfo.CpuCores.Length} CPU cores");
            }

            List<GroupAffinity> jobCpuAffinity = [];
            for (int i = 0; i < systemInfo.ProcessorGroups.Length && cpuCount > 0; i++)
            {
                var group = systemInfo.ProcessorGroups[i];
                int coresNumber = BitOperations.PopCount(group.AffinityMask);
                if (cpuCount > coresNumber)
                {
                    jobCpuAffinity.Add(new(group.Number, group.AffinityMask));
                    cpuCount -= coresNumber;
                }
                else
                {
                    jobCpuAffinity.Add(new(group.Number, group.AffinityMask >> (coresNumber - cpuCount)));
                    cpuCount = 0;
                }
            }
            return [.. jobCpuAffinity];
        }

        static ImmutableDictionary<string, string> GetCustomEnvironmentVariables(string file)
        {
            file = file.Trim('"');
            if (!File.Exists(file))
            {
                throw new ArgumentException("the text file with environment variables does not exist");
            }

            try
            {
                var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using var reader = File.OpenText(file);
                int linenum = 1;
                string? line;

                while ((line = reader.ReadLine()) != null)
                {
                    var ind = line.IndexOf('=');
                    if (ind > 0)
                    {
                        var key = line[..ind].Trim();
                        if (!string.IsNullOrEmpty(key))
                        {
                            var val = line[(ind + 1)..].Trim();
                            envVars.Add(key, val);
                            linenum++;
                            continue;
                        }
                    }
                    throw new ArgumentException($"the environment file contains invalid data (line: {linenum})");
                }

                return [.. envVars];
            }
            catch (IOException ex)
            {
                throw new ArgumentException("can't read the text file with environment variables, {0}", ex.Message);
            }
        }

        static uint ParseCpuRate(string cpuRateString)
        {
            return uint.Parse(cpuRateString) switch
            {
                0 or > 100 => throw new ArgumentException("CPU rate must be between 1 and 100"),
                var v => v
            };
        }

        static ulong ParseMemoryString(string v)
        {
            if (v == null)
            {
                return 0;
            }
            ulong result = ParseByteLength(v);
            if (result > nuint.MaxValue)
            {
                throw new ArgumentException("memory limit is too high for 32-bit architecture");
            }
            return result;
        }

        static uint ParseTimeStringToMilliseconds(string v)
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

        static ulong ParseByteLength(string v)
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

        static Dictionary<string, List<string>> ParseRawArgs(string[] flagNames, string[] rawArgs)
        {
            bool IsFlag(string v) => Array.IndexOf(flagNames, v) >= 0;

            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var lastOption = "";
            var firstFreeArgPassed = false;

            var argsToProcess = new Stack<string>(rawArgs.Reverse());

            while (argsToProcess.Count > 0)
            {
                var argToProcess = argsToProcess.Pop();

                if (!firstFreeArgPassed && argToProcess.StartsWith('-'))
                {
                    if (argToProcess.Split('=', 2) is var splitArgs && splitArgs.Length > 1)
                    {
                        argsToProcess.Push(splitArgs[1]);
                    }

                    if (splitArgs[0] == "--")
                    {
                        lastOption = "";
                        firstFreeArgPassed = true;
                    }
                    else if (splitArgs[0].TrimStart('-') is var option && IsFlag(option))
                    {
                        Debug.Assert(lastOption == "");
                        result[option] = [];
                    }
                    else
                    {
                        Debug.Assert(lastOption == "");
                        lastOption = option;
                    }
                }
                else
                {
                    // the logic is the same for options (lastOption) and free args
                    if (result.TryGetValue(lastOption, out var values))
                    {
                        values.Add(argToProcess);
                    }
                    else
                    {
                        result[lastOption] = [argToProcess];
                    }
                    firstFreeArgPassed = lastOption == "";
                    lastOption = "";
                }
            }
            return result;
        }
    }

    static void ShowLimits(JobSettings? session)
    {
        if (session is null)
        {
            Console.WriteLine("Configured job limits will not be modified.");
        }
        else if (session.IsEmpty())
        {
            Console.WriteLine("All configured job limits will be unset.");
        }
        else
        {
            const uint ONE_MB = 1_048_576;

            if (session.CpuAffinity.Length > 0)
            {
                Console.WriteLine($"CPU groups and affinity masks:");
                foreach (var (group, affinity) in session.CpuAffinity)
                {
                    Console.WriteLine($"  {group}:{affinity:X16}");
                }
            }
            if (session.CpuMaxRate > 0)
            {
                Console.WriteLine($"Max CPU rate:                               {session.CpuMaxRate / 100.0:0.##}%");
                if (session.CpuAffinity is not [])
                {
                    Console.WriteLine("(NOTE: the CPU rate was adjusted to the CPU affinity settings)");
                }
            }
            if (session.MaxBandwidth > 0)
            {
                Console.WriteLine($"Max bandwidth (B):                          {(session.MaxBandwidth):#,0}");
            }
            if (session.MaxProcessMemory > 0)
            {
                Console.WriteLine($"Maximum committed memory (MB):              {(session.MaxProcessMemory / ONE_MB):0,0}");
            }
            if (session.MaxJobMemory > 0)
            {
                Console.WriteLine($"Maximum job committed memory (MB):          {(session.MaxJobMemory / ONE_MB):0,0}");
            }
            if (session.MinWorkingSetSize > 0)
            {
                Debug.Assert(session.MaxWorkingSetSize > 0);
                Console.WriteLine($"Minimum WS memory (MB):                     {(session.MinWorkingSetSize / ONE_MB):0,0}");
            }
            if (session.MaxWorkingSetSize > 0)
            {
                Debug.Assert(session.MinWorkingSetSize > 0);
                Console.WriteLine($"Maximum WS memory (MB):                     {(session.MaxWorkingSetSize / ONE_MB):0,0}");
            }
            if (session.ProcessUserTimeLimitInMilliseconds > 0)
            {
                Console.WriteLine($"Process user-time execution limit (ms):     {session.ProcessUserTimeLimitInMilliseconds:0,0}");
            }
            if (session.JobUserTimeLimitInMilliseconds > 0)
            {
                Console.WriteLine($"Job user-time execution limit (ms):         {session.JobUserTimeLimitInMilliseconds:0,0}");
            }
            if (session.JobClockTimeLimitInMilliseconds > 0)
            {
                Console.WriteLine($"Clock-time execution limit (ms):            {session.JobClockTimeLimitInMilliseconds:0,0}");
            }
            if (session.PropagateOnChildProcesses)
            {
                Console.WriteLine();
                Console.WriteLine("All configured limits will also apply to the child processes.");
            }
        }
        Console.WriteLine();
    }

    sealed class EtwTraceListener : TraceListener
    {
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (message is not null)
            {
                switch (eventType)
                {
                    case TraceEventType.Critical:
                    case TraceEventType.Error:
                        ProcgovEventSource.Instance.LogError(message);
                        break;
                    case TraceEventType.Warning:
                        ProcgovEventSource.Instance.LogWarning(message);
                        break;
                    case TraceEventType.Verbose:
                        ProcgovEventSource.Instance.LogVerbose(message);
                        break;
                    default:
                        ProcgovEventSource.Instance.LogInfo(message);
                        break;
                }
            }
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, [StringSyntax("CompositeFormat")] string? format, params object?[]? args)
        {
            TraceEvent(eventCache, source, eventType, id, args is not null ? string.Format(CultureInfo.InvariantCulture, format!, args) : format);
        }

        public override void Write(string? message)
        {
            if (message is not null)
            {
                ProcgovEventSource.Instance.LogInfo(message);
            }
        }

        public override void WriteLine(string? message)
        {
            Write(message);
        }
    }
}

[EventSource(Name = "LowLevelDesign-ProcessGovernor")]
class ProcgovEventSource : EventSource
{
    public static readonly ProcgovEventSource Instance = new();

    private ProcgovEventSource() { }

    [Event(1, Message = "{0}", Level = EventLevel.Verbose, Keywords = EventKeywords.None)]
    public void LogVerbose(string message) => WriteEvent(1, message);

    [Event(2, Message = "{0}", Level = EventLevel.Informational, Keywords = EventKeywords.None)]
    public void LogInfo(string message) => WriteEvent(2, message);

    [Event(3, Message = "{0}", Level = EventLevel.Warning, Keywords = EventKeywords.None)]
    public void LogWarning(string message) => WriteEvent(3, message);

    [Event(4, Message = "{0}", Level = EventLevel.Error, Keywords = EventKeywords.None)]
    public void LogError(string message) => WriteEvent(4, message);
}
