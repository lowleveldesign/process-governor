using MessagePack;
using MessagePack.Resolvers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("procgov-tests")]

namespace ProcessGovernor;

static partial class Program
{
    public static readonly TraceSource Logger = new("[procgov]", SourceLevels.Warning);

    public static readonly TimeSpan DefaultMaxMonitorIdleTime = TimeSpan.FromSeconds(5);

    static Program()
    {
        // required to make MessagePack work with NativeAOT
        StaticCompositeResolver.Instance.Register(GeneratedMessagePackResolver.Instance, StandardResolver.Instance);
        MessagePackSerializer.DefaultOptions = MessagePackSerializerOptions.Standard.WithResolver(StaticCompositeResolver.Instance); ;
    }

    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cts.Cancel(); };

        // we don't want any default trace listeners
        Logger.Listeners.Clear();
        // procgov ETW listener
        Logger.Listeners.Add(new EtwTraceListener());
#if DEBUG
        Logger.Listeners.Add(new DefaultTraceListener());
#endif

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

                -m|--maxmem=               Max committed memory usage in bytes (accepted suffixes: K, M, or G).
                   --maxjobmem=            Max committed memory usage for all the processes in the job (accepted suffixes: K, M, or G).
                   --maxws=                Max working set size in bytes (accepted suffixes: K, M, or G). Must be set with minws.
                   --minws=                Min working set size in bytes (accepted suffixes: K, M, or G). Must be set with maxws.
                   --env=                  A text file with environment variables (each line in form: VAR=VAL).
                -c|--cpu=                  If in hex (starts with 0x) it is treated as an affinity mask, otherwise it is a number of CPU cores assigned to your app. If you also provide the NUMA node, this setting will apply only to this node.",
                -e|--cpurate=              The maximum CPU rate in % for the process. If you also set the affinity, the rate will apply only to the selected CPU cores. (Windows 8.1+)
                -b|--bandwidth=            The maximum bandwidth (in bytes) for the process outgoing network traffic (accepted suffixes: K, M, or G). (Windows 10+)
                -r|--recursive             Apply limits to child processes too (will wait for all processes to finish).
                   --newconsole            Start the process in a new console window.
                   --nogui                 Hide Process Governor console window (set always when installed as debugger).
                -p|--pid=                  Apply limits on an already running process (or processes if used multiple times)
                -t|--timeout=              Kill the process (with -r, also all its children) if it does not finish within the specified time. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.
                   --process-utime=        Kill the process (with -r, also applies to its children) if it exceeds the given user-mode execution time. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.
                   --priority=             Sets the process priority class of monitored processes. Possible values: Idle, BelowNormal, Normal, AboveNormal, High, RealTime.
                   --job-utime=            Kill the process (with -r, also all its children) if the total user-mode execution time exceed the specified value. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.

                   --enable-privilege=     Enables the specified privileges in the remote process. You may specify multiple privileges by splitting them with commas, for example, 'SeDebugPrivilege,SeLockMemoryPrivilege'
                   --terminate-job-on-exit Terminates the job (and all its processes) when you stop procgov with Ctrl + C.

                   --install               Install procgov as a service which monitors a specific process.
                   --service-path          The path where the service will be installed (C:\Program Files\ProcessGovernor)
                   --service-username      The username for the service account (default: NT AUTHORITY\\SYSTEM).
                   --service-password      The password for the service account (required for non-system accounts).
                   --uninstall             Uninstall procgov for a specific process.
                   --uninstall-all         Uninstall procgov completely (removing all saved process settings)

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
                    ii => (ii.Item.ProcessorGroup.AffinityMask & processorGroup.AffinityMask) != 0).Select(ii => ii.Index)]).Select(
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
        Console.WriteLine("Copyright (C) 2024 Sebastian Solnica");
        Console.WriteLine();
    }

    internal static IExecutionMode ParseArgs(SystemInfo systemInfo, string[] rawArgs)
    {
        try
        {
            var parsedArgs = ParseRawArgs(["newconsole", "r", "recursive", "newconsole", "nogui", "install", "uninstall",
                                "terminate-job-on-exit", "background", "service", "q", "quiet", "nowait", "v", "verbose",
                                "nomonitor", "monitor", "uninstall-all" , "h", "?", "help"], rawArgs);

            if (parsedArgs.Remove("v") || parsedArgs.Remove("verbose"))
            {
                Logger.Switch.Level = SourceLevels.Verbose;
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
            var cpuAffinity = parsedArgs.Remove("c", out v) || parsedArgs.Remove("cpu", out v) ? ParseCpuAffinity(systemInfo, v) : null;
            var cpuMaxRate = parsedArgs.Remove("e", out v) || parsedArgs.Remove("cpurate", out v) ? ParseCpuRate(v[^1]) : 0;
            var maxBandwidth = parsedArgs.Remove("b", out v) || parsedArgs.Remove("bandwidth", out v) ? ParseByteLength(v[^1]) : 0;
            var processUserTimeLimitInMilliseconds = parsedArgs.Remove("process-utime", out v) ? ParseTimeStringToMilliseconds(v[^1]) : 0;
            var jobUserTimeLimitInMilliseconds = parsedArgs.Remove("job-utime", out v) ? ParseTimeStringToMilliseconds(v[^1]) : 0;
            var clockTimeLimitInMilliseconds = parsedArgs.Remove("t", out v) || parsedArgs.Remove("timeout", out v) ? ParseTimeStringToMilliseconds(v[^1]) : 0;
            var propagateOnChildProcesses = parsedArgs.Remove("r") || parsedArgs.Remove("recursive");
            var activeProcessLimit = 0u; // not yet available in command line
            var priorityClass = parsedArgs.Remove("priority", out v) ? Enum.Parse<PriorityClass>(v[^1], ignoreCase: true) : PriorityClass.Undefined;

            // from: https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information
            // Set CpuRate to a percentage times 100. For example, to let the job use 20% of the CPU, set CpuRate to 2,000.
            cpuMaxRate *= 100;

            Debug.Assert(cpuAffinity == null || cpuAffinity.Length > 0);
            if (cpuAffinity != null && cpuMaxRate != 0)
            {

                // CPU rate is set for the whole system so includes all the logical CPUs. When 
                // we have the CPU affinity set, we will divide the rate accordingly.
                var numberOfSelectedCores = cpuAffinity.Sum(aff => BitOperations.PopCount(aff.Affinity));
                Debug.Assert(numberOfSelectedCores < systemInfo.CpuCores.Length);
                cpuMaxRate /= (uint)(systemInfo.CpuCores.Length / numberOfSelectedCores);
            }

            JobSettings jobSettings = new(maxProcessMemory, maxJobMemory, maxWorkingSetSize, minWorkingSetSize,
                cpuAffinity, cpuMaxRate, maxBandwidth, processUserTimeLimitInMilliseconds, jobUserTimeLimitInMilliseconds,
                clockTimeLimitInMilliseconds, propagateOnChildProcesses, activeProcessLimit, priorityClass);

            if (jobSettings.MaxWorkingSetSize != jobSettings.MinWorkingSetSize &&
                Math.Min(jobSettings.MaxWorkingSetSize, jobSettings.MinWorkingSetSize) == 0)
            {
                throw new ArgumentException("minws and maxws must be set together and be greater than 0");
            }

            LaunchConfig launchConfig = LaunchConfig.Default;

            launchConfig |= parsedArgs.Remove("nogui") ? LaunchConfig.NoGui : 0;
            launchConfig |= parsedArgs.Remove("q") || parsedArgs.Remove("quiet") ? LaunchConfig.Quiet : 0;
            launchConfig |= parsedArgs.Remove("nomonitor") ? LaunchConfig.NoMonitor : 0;

            var nowait = parsedArgs.Remove("nowait");
            var exitBehavior = parsedArgs.Remove("terminate-job-on-exit") switch
            {
                true when nowait =>
                    throw new ArgumentException("--terminate-job-on-exit and --nowait cannot be used together"),
                true => ExitBehavior.TerminateJobOnExit,
                false when nowait && jobSettings.ClockTimeLimitInMilliseconds > 0 =>
                    throw new ArgumentException("--nowait cannot be used with --timeout"),
                false when nowait => ExitBehavior.DontWaitForJobCompletion,
                _ => ExitBehavior.WaitForJobCompletion
            };

            var environment = parsedArgs.Remove("env", out v) ? GetCustomEnvironmentVariables(v[^1]) : [];

            var privileges = parsedArgs.Remove("enable-privilege", out v) ? v : [];
            if (parsedArgs.Remove("enable-privileges", out v)) // legacy name of this argument
            {
                privileges = [.. privileges, .. v];
            }
            // recommended way is to use the parameter multiple times, but for legacy reasons
            // we also support comma-separated list
            privileges = privileges.SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();

            var newConsole = parsedArgs.Remove("newconsole");

            var procargs = parsedArgs.Remove("", out v) ? v : [];

            var pidsToParse = parsedArgs.Remove("p", out v) ? v : [];
            if (parsedArgs.Remove("pid", out v))
            {
                pidsToParse = pidsToParse.Union(v).ToList();
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
                ([var executable], [], false, false, true, false, false) => new SetupProcessGovernance(
                    jobSettings, environment, privileges, executable, serviceInstallPath, serviceUserName, serviceUserPassword),
                ([var executable], [], false, false, false, true, false) => new RemoveProcessGovernance(executable, serviceInstallPath),
                ([], [], false, false, false, false, true) => new RemoveAllProcessGovernance(serviceInstallPath),
                (_, [], false, false, false, false, false) when procargs.Count > 0 =>
                    new RunAsCmdApp(jobSettings, new LaunchProcess(procargs, newConsole), environment, privileges, launchConfig, exitBehavior),
                ([], _, false, false, false, false, false) when pids.Length > 0 =>
                    new RunAsCmdApp(jobSettings, new AttachToProcess(pids), environment, privileges, launchConfig, exitBehavior),
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

        static GroupAffinity[] ParseCpuAffinity(SystemInfo systemInfo, List<string> cpuAffinityArgs)
        {
            if (cpuAffinityArgs is [var s] && int.TryParse(s, out var cpuCount))
            {
                return CalculateAffinityMaskFromCpuCount(cpuCount);
            }

            Debug.Assert(systemInfo.NumaNodes.Length > 0 &&
                systemInfo.NumaNodes[0].ProcessorGroups.Length > 0);
            var defaultProcessorGroup = systemInfo.NumaNodes[0].ProcessorGroups[0];

            Dictionary<ushort, nuint> affinities = [];

            foreach (var cpuAffinityArg in cpuAffinityArgs)
            {
                switch (cpuAffinityArg.Split(':'))
                {
                    case [string aff] when TryFindProcessorGroup(aff, out var processorGroup):
                        affinities[processorGroup.Number] = processorGroup.AffinityMask;
                        break;
                    case [string aff] when TryFindNumaNode(aff, out var numaNode):
                        foreach (var processorGroup in numaNode.ProcessorGroups)
                        {
                            UpdateGroupAffinity(processorGroup.Number, processorGroup.AffinityMask);
                        }
                        break;
                    case [string aff] when TryParseAffinity(aff, out var groupAffinity):
                        UpdateGroupAffinity(defaultProcessorGroup.Number, groupAffinity);
                        break;
                    case [string numa, string group] when TryFindNumaNode(numa, out var numaNode)
                        && TryFindProcessorGroup(group, out var processorGroup):
                        if (numaNode.ProcessorGroups.FirstOrDefault(g => g.Number == processorGroup.Number) is { } numaProcessorGroup)
                        {
                            UpdateGroupAffinity(processorGroup.Number, numaProcessorGroup.AffinityMask);
                        }
                        else
                        {
                            throw new ArgumentException(
                                $"processor group {processorGroup.Number} does not belong to NUMA node {numaNode.Number}");
                        }
                        break;
                    case [string group, string aff] when TryFindProcessorGroup(group, out var processorGroup)
                        && TryParseAffinity(aff, out var cpuAffinity):
                        UpdateGroupAffinity(processorGroup.Number, processorGroup.AffinityMask & cpuAffinity);
                        break;
                    case [string numa, string group, string aff] when TryFindNumaNode(numa, out var numaNode)
                        && TryFindProcessorGroup(group, out var processorGroup) && TryParseAffinity(aff, out var cpuAffinity):
                        if (!numaNode.ProcessorGroups.Any(g => g.Number == processorGroup.Number))
                        {
                            throw new ArgumentException(
                                $"processor group {processorGroup.Number} does not belong to NUMA node {numaNode.Number}");
                        }
                        UpdateGroupAffinity(processorGroup.Number, processorGroup.AffinityMask & cpuAffinity);
                        break;
                    default:
                        throw new ArgumentException($"invalid affinity string: '{cpuAffinityArg}'");
                };
            }

            return [.. affinities.Select(kv => new GroupAffinity(kv.Key, kv.Value))];


            void UpdateGroupAffinity(ushort groupNumber, nuint affinity)
            {
                if (affinities.TryGetValue(groupNumber, out var savedAffinity))
                {
                    affinities[groupNumber] = savedAffinity | affinity;
                }
                else
                {
                    affinities.Add(groupNumber, affinity);
                }
            }

            bool TryFindNumaNode(string numaArg, [MaybeNullWhen(false)] out NumaNode numa)
            {
                if (numaArg.Length > 0 && (numaArg[0] == 'n' || numaArg[0] == 'N'))
                {
                    var number = uint.Parse(numaArg.AsSpan(1));
                    if (systemInfo.NumaNodes.FirstOrDefault(n => n.Number == number) is { } n)
                    {
                        numa = n;
                        return true;
                    }
                }
                numa = null;
                return false;
            }

            bool TryFindProcessorGroup(string groupArg, [MaybeNullWhen(false)] out ProcessorGroup group)
            {
                if (groupArg.Length > 0 && (groupArg[0] == 'g' || groupArg[0] == 'G'))
                {
                    var number = ushort.Parse(groupArg.AsSpan(1));
                    if (systemInfo.ProcessorGroups.FirstOrDefault(g => g.Number == number) is { } g)
                    {
                        group = g;
                        return true;
                    }
                }
                group = null;
                return false;
            }

            static bool TryParseAffinity(string s, out nuint affinity)
            {
                return nuint.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ?
                    s[2..] : s, NumberStyles.AllowHexSpecifier, null, out affinity);
            }

            GroupAffinity[] CalculateAffinityMaskFromCpuCount(int cpuCount)
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
        }

        static Dictionary<string, string> GetCustomEnvironmentVariables(string file)
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

                return envVars;
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

    sealed class EtwTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            if (message is not null)
            {
                ProcgovEventSource.Instance.Log(message);
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

    [Event(1, Message = "{0}", Level = EventLevel.Informational, Keywords = EventKeywords.None)]
    public void Log(string message) => WriteEvent(1, message);
}
