using Microsoft.Win32;
using NDesk.Options;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LowLevelDesign
{
    public static class Program
    {
#if DEBUG
        const bool debugOutput = true;
#else
        const bool debugOutput = false;
#endif
        public static readonly TraceSource Logger = new TraceSource("[procgov]", SourceLevels.All);

        static Program()
        {
            // remove default listeners (-v to enable console traces)
            Logger.Listeners.Clear();
        }

        public static int Main(string[] args)
        {
            var procargs = new List<string>();
            bool showhelp = false, nogui = false, debug = false, quiet = false, nowait = false;
            var pid = 0u;
            var registryOperation = RegistryOperation.NONE;

            var session = new SessionSettings();

            var p = new OptionSet()
            {
                    { "m|maxmem=", "Max committed memory usage in bytes (accepted suffixes: K, M, or G).",
                        v => { session.MaxProcessMemory = ParseMemoryString(v); } },
                    { "maxjobmem=", "Max committed memory usage for all the processes in the job (accepted suffixes: K, M, or G).",
                        v => { session.MaxJobMemory = ParseMemoryString(v); } },
                    { "maxws=", "Max working set size in bytes (accepted suffixes: K, M, or G). Must be set with minws.",
                        v => { session.MaxWorkingSetSize = ParseMemoryString(v); } },
                    { "minws=", "Min working set size in bytes (accepted suffixes: K, M, or G). Must be set with maxws.",
                        v => { session.MinWorkingSetSize = ParseMemoryString(v); } },
                    { "env=", "A text file with environment variables (each line in form: VAR=VAL). Applies only to newly created processes.",
                        v => LoadCustomEnvironmentVariables(session, v) },
                    { "n|node=", "The preferred NUMA node for the process.",
                        v => session.NumaNode = ushort.Parse(v) },
                    { "c|cpu=", "If in hex (starts with 0x) it is treated as an affinity mask, otherwise it is a number of CPU cores assigned to your app. " +
                        "If you also provide the NUMA node, this setting will apply only to this node.",
                        v => {
                            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                                session.CpuAffinityMask = ulong.Parse(v.Substring(2), NumberStyles.HexNumber);
                            } else {
                                session.CpuAffinityMask = CalculateAffinityMaskFromCpuCount(int.Parse(v));
                            }
                        }},
                    { "e|cpurate=", "The maximum CPU rate in % for the process. If you also set the affinity, " +
                        "the rate will apply only to the selected CPU cores. (Windows 8.1+)",
                        v => { session.CpuMaxRate = ParseCpuRate(v); } },
                    { "b|bandwidth=", "The maximum bandwidth (in bytes) for the process outgoing network traffic" +
                        " (accepted suffixes: K, M, or G). (Windows 10+)",
                        v => { session.MaxBandwidth = ParseByteLength(v); } },
                    { "r|recursive", "Apply limits to child processes too (will wait for all processes to finish).",
                        v => { session.PropagateOnChildProcesses = v != null; } },
                    { "newconsole", "Start the process in a new console window.", v => { session.SpawnNewConsoleWindow = v != null; } },
                    { "nogui", "Hide Process Governor console window (set always when installed as debugger).",
                        v => { nogui = v != null; } },
                    { "p|pid=", "Attach to an already running process", (uint v) => pid = v },
                    { "install", "Install procgov as a debugger for a specific process using Image File Executions. " +
                                 "DO NOT USE this option if the process you want to control starts child instances of itself (for example, Chrome).",
                        v => { registryOperation = RegistryOperation.INSTALL; } },
                    { "t|timeout=", "Kill the process (with -r, also all its children) if it does not finish within the specified time. " +
                                    "Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.",
                        (string v) => session.ClockTimeLimitInMilliseconds = ParseTimeStringToMilliseconds(v) },
                    { "process-utime=", "Kill the process (with -r, also applies to its children) if it exceeds the given " +
                                    "user-mode execution time. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.",
                        (string v) => session.ProcessUserTimeLimitInMilliseconds = ParseTimeStringToMilliseconds(v) },
                    { "job-utime=", "Kill the process (with -r, also all its children) if the total user-mode execution " +
                                   "time exceed the specified value. Add suffix to define the time unit. Valid suffixes are: ms, s, m, h.",
                        (string v) => session.JobUserTimeLimitInMilliseconds = ParseTimeStringToMilliseconds(v) },
                    { "uninstall", "Uninstall procgov for a specific process.", v => { registryOperation = RegistryOperation.UNINSTALL; } },
                    { "debugger", "Internal - do not use.",
                        v => debug = v != null },
                    { "q|quiet", "Do not show procgov messages.", v => quiet = v != null },
                    { "nowait", "Does not wait for the target process(es) to exit.", v => nowait = v != null },
                    { "v|verbose", "Show verbose messages in the console.", v => {
                        if (v != null)
                        {
                            Logger.Listeners.Add(new ConsoleTraceListener());
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

            if ((session.MaxWorkingSetSize > 0 && session.MinWorkingSetSize == 0) || (session.MinWorkingSetSize > 0 && session.MaxWorkingSetSize == 0))
            {
                Console.Error.WriteLine("ERROR: minws and maxws must be set together.");
                return 1;
            }

            if (!showhelp && registryOperation != RegistryOperation.NONE)
            {
                if (procargs.Count == 0)
                {
                    Console.Error.WriteLine("ERROR: please provide an image name for a process you would like to intercept.");
                    return 1;
                }
                SetupRegistryForProcessGovernor(session, procargs[0], registryOperation);
                return 0;
            }

            if (!showhelp && (procargs.Count == 0 && pid == 0) || (pid > 0 && procargs.Count > 0))
            {
                Console.Error.WriteLine("ERROR: please provide either process name or PID of the already running process");
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
                using var job = session switch {
                    _ when debug => ProcessModule.StartProcessUnderDebuggerAndDetach(procargs, session),
                    _ when pid > 0 => ProcessModule.AttachToProcess(pid, session),
                    _ => ProcessModule.StartProcess(procargs, session)
                };

                if (!quiet)
                {
                    ShowHeader();
                    ShowLimits(session);

                    if (!nowait)
                    {
                        Console.WriteLine("Press Ctrl-C to end execution without terminating the process.");
                        Console.WriteLine();
                    }
                }

                return nowait ? 0 : Win32JobModule.WaitForTheJobToComplete(job, cts.Token);
            }
            catch (Win32Exception ex)
            {
                Console.Error.WriteLine("ERROR: {0}", (debugOutput ? ex.ToString() : $"{ex.Message} (0x{ex.ErrorCode:X})"));
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: {0}", (debugOutput ? ex.ToString() : ex.Message));
                return 1;
            }
        }

        public static uint ParseTimeStringToMilliseconds(string v)
        {
            if (v.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                return uint.Parse(v.Substring(0, v.Length - 2));
            }
            if (v.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                return uint.Parse(v.Substring(0, v.Length - 1)) * 1000;
            }
            if (v.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                return uint.Parse(v.Substring(0, v.Length - 1)) * 1000 * 60;
            }
            if (v.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            {
                return uint.Parse(v.Substring(0, v.Length - 1)) * 1000 * 60 * 60;
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
                result = ulong.Parse(v.Substring(0, v.Length - 1)) << 10;
            }
            else if (v.EndsWith("M", StringComparison.OrdinalIgnoreCase))
            {
                result = ulong.Parse(v.Substring(0, v.Length - 1)) << 20;
            }
            else if (v.EndsWith("G", StringComparison.OrdinalIgnoreCase))
            {
                result = ulong.Parse(v.Substring(0, v.Length - 1)) << 30;
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
                throw new ArgumentException("Memory limit is too high for 32-bit architecture.");
            }
            return result;
        }

        public static void LoadCustomEnvironmentVariables(SessionSettings session, string file)
        {
            if (file == null || !File.Exists(file))
            {
                throw new ArgumentException("the text file with environment variables does not exist");
            }
            try
            {
                using (var reader = File.OpenText(file))
                {
                    int linenum = 1;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var ind = line.IndexOf("=");
                        if (ind > 0)
                        {
                            var key = line.Substring(0, ind).Trim();
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
            }
            catch (IOException ex)
            {
                throw new ArgumentException("can't read the text file with environment variables, {0}", ex.Message);
            }
        }

        static void ShowHeader()
        {
            Console.WriteLine("Process Governor v{0} - sets limits on your processes",
                Assembly.GetExecutingAssembly().GetName().Version.ToString());
            Console.WriteLine("Copyright (C) 2019 Sebastian Solnica (lowleveldesign.org)");
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
            Console.WriteLine("> procgov --maxmem 200M test.exe");
            Console.WriteLine();
            Console.WriteLine("Limit CPU usage of a test.exe process to first three CPU cores:");
            Console.WriteLine("> procgov --cpu 3 test.exe");
            Console.WriteLine();
            Console.WriteLine("Always run a test.exe process only on the first three CPU cores:");
            Console.WriteLine("> procgov --install --cpu 3 test.exe");
            Console.WriteLine();
        }

        static void ShowLimits(SessionSettings session)
        {
            Console.WriteLine("CPU affinity mask:                          {0}", session.CpuAffinityMask != 0 ?
                $"0x{session.CpuAffinityMask:X}" : "(not set)");
            Console.WriteLine("Max CPU rate:                               {0}", session.CpuMaxRate > 0 ?
                $"{session.CpuMaxRate}%" : "(not set)");
            Console.WriteLine("Max bandwidth (B):                          {0}", session.MaxBandwidth > 0 ?
                $"{(session.MaxBandwidth):#,0}" : "(not set)");
            Console.WriteLine("Maximum committed memory (MB):              {0}", session.MaxProcessMemory > 0 ?
                $"{(session.MaxProcessMemory / 1048576):0,0}" : "(not set)");
            Console.WriteLine("Maximum job committed memory (MB):          {0}", session.MaxJobMemory > 0 ?
                $"{(session.MaxJobMemory / 1048576):0,0}" : "(not set)");
            Console.WriteLine("Minimum WS memory (MB):                     {0}", session.MinWorkingSetSize > 0 ?
                $"{(session.MinWorkingSetSize / 1048576):0,0}" : "(not set)");
            Console.WriteLine("Maximum WS memory (MB):                     {0}", session.MaxWorkingSetSize > 0 ?
                $"{(session.MaxWorkingSetSize / 1048576):0,0}" : "(not set)");
            Console.WriteLine("Preferred NUMA node:                        {0}", session.NumaNode != 0xffff ?
                $"{session.NumaNode}" : "(not set)");
            Console.WriteLine("Process user-time execution limit (ms):     {0}", session.ProcessUserTimeLimitInMilliseconds > 0 ?
                $"{session.ProcessUserTimeLimitInMilliseconds:0,0}" : "(not set)");
            Console.WriteLine("Job user-time execution limit (ms):         {0}", session.JobUserTimeLimitInMilliseconds > 0 ?
                $"{session.JobUserTimeLimitInMilliseconds:0,0}" : "(not set)");
            Console.WriteLine("Clock-time execution limit (ms):            {0}", session.ClockTimeLimitInMilliseconds > 0 ?
                $"{session.ClockTimeLimitInMilliseconds:0,0}" : "(not set)");

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
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public enum RegistryOperation
        {
            INSTALL,
            UNINSTALL,
            NONE
        }

        public static void SetupRegistryForProcessGovernor(SessionSettings session, string appImageExe, RegistryOperation oper)
        {
            if (!IsUserAdmin())
            {
                Console.Error.WriteLine("You must be admin to do that. Run the app from the administrative console.");
                return;
            }
            // extrace image.exe if path is provided
            appImageExe = Path.GetFileName(appImageExe);
            var regkey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", true);
            // add to image file execution path
            if (oper == RegistryOperation.INSTALL)
            {
                regkey = regkey.CreateSubKey(appImageExe);
                regkey.SetValue("Debugger", PrepareDebuggerCommandString(session, appImageExe));
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

        public static string PrepareDebuggerCommandString(SessionSettings session, string appImageExe)
        {
            var buffer = new StringBuilder();
            var procgovPath = Assembly.GetAssembly(typeof(ProcessModule)).Location;
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

            return buffer.ToString();
        }

        public static string GetAppEnvironmentFilePath(string appImageExe)
        {
            var procgovPath = Assembly.GetAssembly(typeof(ProcessModule)).Location;
            return Path.Combine(Path.GetDirectoryName(procgovPath), appImageExe + ".env");
        }
    }
}
