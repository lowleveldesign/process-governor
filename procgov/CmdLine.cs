using System.Diagnostics;
using System.Reflection;

namespace ProcessGovernor;

internal static class CmdLine
{
    public static void ParseArgs(string[] rawArgs)
    {
        bool nogui = false, quiet = false, nowait = false, terminateJobOnExit = false;

        var parsedArgs = ParseRawArgs(["newconsole", "r", "recursive", "newconsole", "nogui", "install", "uninstall",
        "terminate-job-on-exit", "background", "service", "q", "quiet", "nowait", "v", "verbose", "h", "?", "help"], rawArgs);

        try
        {
            if ((parsedArgs.Remove("m", out var v) || parsedArgs.Remove("maxmem", out v)))
            {
                session.MaxProcessMemory = ParseMemoryString(v[^1]);
            }
            if (parsedArgs.Remove("maxjobmem", out v))
            {
                session.MaxJobMemory = ParseMemoryString(v[^1]);
            }
            if (parsedArgs.Remove("maxws", out v))
            {
                session.MaxWorkingSetSize = ParseMemoryString(v[^1]);
            }
            if (parsedArgs.Remove("minws", out v))
            {
                session.MinWorkingSetSize = ParseMemoryString(v[^1]);
            }
            if (parsedArgs.Remove("env", out v))
            {
                LoadCustomEnvironmentVariables(session, v[^1]);
            }
            if (parsedArgs.Remove("n", out v) || parsedArgs.Remove("node", out v))
            {
                session.NumaNode = ushort.Parse(v[^1]);
            }
            if (parsedArgs.Remove("c", out v) || parsedArgs.Remove("cpu", out v))
            {
                session.CpuAffinityMask = v[^1] switch
                {
                    var s when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => ulong.Parse(s[2..], NumberStyles.HexNumber),
                    var s => CalculateAffinityMaskFromCpuCount(int.Parse(s))
                };
            }
            if (parsedArgs.Remove("e", out v) || parsedArgs.Remove("cpurate", out v))
            {
                session.CpuMaxRate = ParseCpuRate(v[^1]);
            }
            if (parsedArgs.Remove("b", out v) || parsedArgs.Remove("bandwidth", out v))
            {
                session.MaxBandwidth = ParseByteLength(v[^1]);
            }
            if (parsedArgs.Remove("r") || parsedArgs.Remove("recursive"))
            {
                session.PropagateOnChildProcesses = true;
            }
            if (parsedArgs.Remove("newconsole"))
            {
                session.SpawnNewConsoleWindow = true;
            }
            if (parsedArgs.Remove("nogui"))
            {
                nogui = true;
            }
            if (parsedArgs.Remove("p", out v) || parsedArgs.Remove("pid", out v))
            {
                pids = v.Select(x => int.Parse(x)).ToArray();
            }
            if (parsedArgs.Remove("install", out v))
            {
                registryOperation = RegistryOperation.INSTALL;
            }
            if (parsedArgs.Remove("t", out v) || parsedArgs.Remove("timeout", out v))
            {
                session.ClockTimeLimitInMilliseconds = ParseTimeStringToMilliseconds(v[^1]);
            }
            if (parsedArgs.Remove("process-utime", out v))
            {
                session.ProcessUserTimeLimitInMilliseconds = ParseTimeStringToMilliseconds(v[^1]);
            }
            if (parsedArgs.Remove("job-utime", out v))
            {
                session.JobUserTimeLimitInMilliseconds = ParseTimeStringToMilliseconds(v[^1]);
            }
            if (parsedArgs.Remove("uninstall"))
            {
                registryOperation = RegistryOperation.UNINSTALL;
            }
            if (parsedArgs.Remove("enable-privilege", out v))
            {
                session.Privileges = v.ToArray();
            }
            if (parsedArgs.Remove("terminate-job-on-exit"))
            {
                terminateJobOnExit = true;
            }
            if (parsedArgs.Remove("monitor"))
            {
                monitor = true;
            }
            // FIXME: implement service
            //if (parsedArgs.Remove("service"))
            //{
            //    runAsService = true;
            //}
            if (parsedArgs.Remove("q") || parsedArgs.Remove("quiet"))
            {
                quiet = true;
            }
            if (parsedArgs.Remove("nowait"))
            {
                nowait = true;
            }
            if (parsedArgs.Remove("v") || parsedArgs.Remove("verbose"))
            {
                Logger.Switch.Level = SourceLevels.Verbose;
            }
            if (parsedArgs.Remove("h") || parsedArgs.Remove("?") || parsedArgs.Remove("help"))
            {
                showhelp = true;
            }
            if (parsedArgs.Remove("", out var freeargs))
            {
                procargs = freeargs;
            }

            if (parsedArgs.Count > 1)
            {
                throw new ArgumentException("unrecognized arguments: " + string.Join(", ", parsedArgs.Keys));
            }

            if (terminateJobOnExit && nowait)
            {
                throw new ArgumentException("--terminate-job-on-exit and --nowait cannot be used together.");
            }

            if (session.MaxWorkingSetSize != session.MinWorkingSetSize && Math.Min(session.MaxWorkingSetSize, session.MinWorkingSetSize) == 0)
            {
                throw new ArgumentException("minws and maxws must be set together and be greater than 0.");
            }

            if (!showhelp && (procargs.Count == 0 && pids.Length == 0) || (pids.Length > 0 && procargs.Count > 0))
            {
                throw new ArgumentException("please provide either an executable path or PID(s)");
            }
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("ERROR: invalid number in one of the constraints");
            Console.Error.WriteLine();
            showhelp = true;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine();
            showhelp = true;
        }

        if (showhelp)
        {
            ShowHeader();
            ShowHelp();
            // FIXME: should return error code if something failed
            return 0;
        }
    }

    private static Dictionary<string, List<string>> ParseRawArgs(string[] flagNames, string[] rawArgs)
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
        if (result > nuint.MaxValue)
        {
            throw new ArgumentException("memory limit is too high for 32-bit architecture.");
        }
        return result;
    }

    public static void LoadCustomEnvironmentVariables(JobSettings session, string file)
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

    private static void ShowHeader()
    {
        Console.WriteLine("Process Governor v{0} - sets limits on processes",
            Assembly.GetExecutingAssembly()!.GetName()!.Version!.ToString());
        Console.WriteLine("Copyright (C) 2024 Sebastian Solnica (lowleveldesign.org)");
        Console.WriteLine();
    }

    private static void ShowHelp()
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
