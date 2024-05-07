using System.Diagnostics;
using System.Globalization;
using System.Net.Quic;
using System.Reflection;

namespace ProcessGovernor;

static partial class Program
{
    public static IExecutionMode ParseArgs(string[] rawArgs, out bool quiet)
    {
        try
        {
            var parsedArgs = ParseRawArgs(["newconsole", "r", "recursive", "newconsole", "nogui", "install", "uninstall",
                                "terminate-job-on-exit", "background", "service", "q", "quiet", "nowait", "v", "verbose",
                                "h", "?", "help"], rawArgs);

            var jobSettings = new JobSettings(
                parsedArgs.Remove("maxmem", out var v) || parsedArgs.Remove("m", out v) ? ParseMemoryString(v[^1]) : 0,
                parsedArgs.Remove("maxjobmem", out v) ? ParseMemoryString(v[^1]) : 0,
                parsedArgs.Remove("maxws", out v) ? ParseMemoryString(v[^1]) : 0,
                parsedArgs.Remove("minws", out v) ? ParseMemoryString(v[^1]) : 0,
                parsedArgs.Remove("c", out v) || parsedArgs.Remove("cpu", out v) ? ParseCpuAffinity(v[^1]) : 0,
                parsedArgs.Remove("e", out v) || parsedArgs.Remove("cpurate", out v) ? ParseCpuRate(v[^1]) : 0,
                parsedArgs.Remove("b", out v) || parsedArgs.Remove("bandwidth", out v) ? ParseByteLength(v[^1]) : 0,
                parsedArgs.Remove("process-utime", out v) ? ParseTimeStringToMilliseconds(v[^1]) : 0,
                parsedArgs.Remove("job-utime", out v) ? ParseTimeStringToMilliseconds(v[^1]) : 0,
                parsedArgs.Remove("t", out v) || parsedArgs.Remove("timeout", out v) ? ParseTimeStringToMilliseconds(v[^1]) : 0,
                parsedArgs.Remove("r") || parsedArgs.Remove("recursive"),
                parsedArgs.Remove("n", out v) || parsedArgs.Remove("node", out v) ? ushort.Parse(v[^1]) : (ushort)0
            );

            if (jobSettings.MaxWorkingSetSize != jobSettings.MinWorkingSetSize && 
                Math.Min(jobSettings.MaxWorkingSetSize, jobSettings.MinWorkingSetSize) == 0)
            {
                throw new ArgumentException("minws and maxws must be set together and be greater than 0.");
            }

            var nogui = parsedArgs.Remove("nogui");

            var nowait = parsedArgs.Remove("nowait");
            var exitBehavior = parsedArgs.Remove("terminate-job-on-exit") switch
            {
                true when nowait => throw new ArgumentException("--terminate-job-on-exit and --nowait cannot be used together."),
                true => ExitBehavior.TerminateJobOnExit,
                false when nowait => ExitBehavior.DontWaitForJobCompletion,
                _ => ExitBehavior.WaitForJobCompletion
            };

            quiet = parsedArgs.Remove("q") || parsedArgs.Remove("quiet");
            if (parsedArgs.Remove("v") || parsedArgs.Remove("verbose"))
            {
                Logger.Switch.Level = SourceLevels.Verbose;
            }
            var showHelp = parsedArgs.Remove("h") || parsedArgs.Remove("?") || parsedArgs.Remove("help");

            var environment = parsedArgs.Remove("env", out v) ? GetCustomEnvironmentVariables(v[^1]) : [];
            var privileges = parsedArgs.Remove("enable-privilege", out v) ? v : [];
            var newConsole = parsedArgs.Remove("newconsole");

            var procargs = parsedArgs.Remove("", out v) ? v : [];
            var pids = parsedArgs.Remove("p", out v) || parsedArgs.Remove("pid", out v) ? v.Select(int.Parse).ToArray() : [];

            var runAsMonitor = parsedArgs.Remove("monitor");
            var runAsService = parsedArgs.Remove("service");
            var install = parsedArgs.Remove("install");
            var uninstall = parsedArgs.Remove("uninstall");

            if (parsedArgs.Count > 1)
            {
                throw new ArgumentException("unrecognized arguments: " + string.Join(", ", parsedArgs.Keys));
            }

            return (procargs, pids, runAsMonitor, runAsService, install, uninstall) switch
            {
                ([], [], true, false, false, false) => new RunAsMonitor(),
                ([], [], false, true, false, false) => new RunAsService(),
                ([var executable], [], false, false, true, false) => new InstallService(jobSettings, executable),
                ([var executable], [], false, false, false, true) => new UninstallService(executable),
                (_, [], false, false, false, false) => new LaunchProcess(jobSettings, procargs, newConsole, environment, privileges, nogui, exitBehavior),
                ([], _, false, false, false, false) => new AttachToProcesses(jobSettings, pids, environment, privileges, nogui, exitBehavior),
                _ => throw new ArgumentException("please provide either an executable path or PID(s)");
            };
        }
        catch (FormatException)
        {
            quiet = default;
            return new ExitImmediately("invalid number in one of the constraints");
        }
        catch (ArgumentException ex)
        {
            quiet = default;
            return new ExitImmediately(ex.Message);
        }

        static ulong ParseCpuAffinity(string cpuAffinityString)
        {
            static ulong CalculateAffinityMaskFromCpuCount(int cpuCount)
            {
                ulong mask = 0;
                for (int i = 0; i < cpuCount; i++)
                {
                    mask <<= 1;
                    mask |= 0x1;
                }
                return mask;
            }

            return cpuAffinityString switch
            {
                var s when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => ulong.Parse(s[2..], NumberStyles.HexNumber),
                var s => CalculateAffinityMaskFromCpuCount(int.Parse(s))
            };
        }

        static Dictionary<string, string> GetCustomEnvironmentVariables(string file)
        {
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
                    var ind = line.IndexOf("=");
                    if (ind > 0)
                    {
                        var key = line[..ind].Trim();
                        if (!string.IsNullOrEmpty(key))
                        {
                            var val = line.Substring(ind + 1, line.Length - ind - 1).Trim();
                            envVars.Add(key, val);
                            linenum++;
                            continue;
                        }
                    }
                    throw new ArgumentException(string.Format("the environment file contains invalid data (line: {0})", linenum));
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
                throw new ArgumentException("memory limit is too high for 32-bit architecture.");
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
            var args = rawArgs.SelectMany(arg => arg.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries)).ToArray();
            bool IsFlag(string v) => Array.IndexOf(flagNames, v) >= 0;

            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var lastOption = "";
            var firstFreeArgPassed = false;

            foreach (var arg in args)
            {
                if (!firstFreeArgPassed && arg.StartsWith("-", StringComparison.Ordinal))
                {
                    if (arg == "--")
                    {
                        lastOption = "";
                        firstFreeArgPassed = true;
                    }
                    else if (arg.TrimStart('-') is var option && IsFlag(option))
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
                        values.Add(arg);
                    }
                    else
                    {
                        result[lastOption] = new List<string> { arg };
                    }
                    firstFreeArgPassed = lastOption == "";
                    lastOption = "";
                }
            }
            return result;
        }
    }

    public static void ShowHeader()
    {
        Console.WriteLine("Process Governor v{0} - sets limits on processes",
            Assembly.GetExecutingAssembly()!.GetName()!.Version!.ToString());
        Console.WriteLine("Copyright (C) 2024 Sebastian Solnica (lowleveldesign.org)");
        Console.WriteLine();
    }

    public static void ShowHelp()
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
}
