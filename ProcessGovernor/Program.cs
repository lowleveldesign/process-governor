using Microsoft.Win32;
using NDesk.Options;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using WinWindows = LowLevelDesign.Win32.Windows;

namespace LowLevelDesign
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            using (var procgov = new ProcessGovernor()) {
                List<string> procargs = null;
                bool showhelp = false, nogui = false, debug = false;
                int pid = 0;
                RegistryOperation registryOperation = RegistryOperation.NONE;

                var p = new OptionSet()
                {
                    { "m|maxmem=", "Max committed memory usage in bytes (accepted suffixes: K, M or G).",
                        v => { procgov.MaxProcessMemory = ParseMemoryString(v); } },
                    { "env=", "A text file with environment variables (each line in form: VAR=VAL). Applies only to newly created processes.",
                        v => LoadCustomEnvironmentVariables(procgov, v) },
                    { "c|cpu=", "If in hex (starts with 0x) it is treated as an affinity mask, otherwise it is a number of CPU cores assigned to your app.",
                        v => {
                            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                                procgov.CpuAffinityMask = long.Parse(v.Substring(2), NumberStyles.HexNumber);
                            } else {
                                procgov.CpuAffinityMask = CalculateAffinityMaskFromCpuCount(int.Parse(v));
                            }
                        }},
                    { "r|recursive", "Apply limits to child processes too.", v => { procgov.PropagateOnChildProcesses = v != null;  } },
                    { "newconsole", "Start the process in a new console window.", v => { procgov.SpawnNewConsoleWindow = v != null; } },
                    { "nogui", "Hide Process Governor console window (set always when installed as debugger).",
                        v => { nogui = v != null; } },
                    { "p|pid=", "Attach to an already running process", (int v) => pid = v },
                    { "install", "Installs procgov as a debugger for a specific process using Image File Executions.",
                        v => { registryOperation = RegistryOperation.INSTALL; } },
                    { "uninstall", "Uninstalls procgov for a specific process.",
                        v => { registryOperation = RegistryOperation.UNINSTALL; } },
                    { "debugger", "Internal - do not use.",
                        v => { debug = v != null; } },
                    { "h|help", "Show this message and exit", v => showhelp = v != null },
                    { "?", "Show this message and exit", v => showhelp = v != null }
                };

                try {
                    procargs = p.Parse(args);
                } catch (OptionException ex) {
                    Console.Write("ERROR: invalid argument");
                    Console.WriteLine(ex.Message);
                    Console.WriteLine();
                    showhelp = true;
                } catch (FormatException) {
                    Console.WriteLine("ERROR: invalid number in one of the constraints");
                    Console.WriteLine();
                    showhelp = true;
                } catch (ArgumentException ex) {
                    Console.WriteLine("ERROR: {0}", ex.Message);
                    Console.WriteLine();
                    showhelp = true;
                }

                if (!showhelp && registryOperation != RegistryOperation.NONE) {
                    if (procargs.Count == 0) {
                        Console.WriteLine("ERROR: please provide an image name for a process you would like to intercept.");
                        return;
                    }
                    SetupRegistryForProcessGovernor(procgov, procargs[0], registryOperation);
                    return;
                }

                if (!showhelp && (procargs.Count == 0 && pid == 0) || (pid > 0 && procargs.Count > 0)) {
                    Console.WriteLine("ERROR: please provide either process name or PID of the already running process");
                    Console.WriteLine();
                    showhelp = true;
                }

                if (showhelp) {
                    ShowHelp(p);
                    return;
                }

                if (nogui) {
                    WinWindows.NativeMethods.ShowWindow(WinWindows.NativeMethods.GetConsoleWindow(), 
                        WinWindows.NativeMethods.SW_HIDE);
                }

                try {
                    if (debug) {
                        procgov.StartProcessUnderDebuggerAndDetach(procargs);
                    } else if (pid > 0) {
                        procgov.AttachToProcess(pid);
                    } else {
                        procgov.StartProcess(procargs);
                    }
                } catch (Win32Exception ex) {
                    Console.WriteLine("ERROR: {0} (0x{1:X})", ex.Message, ex.ErrorCode);
                } catch (Exception ex) {
                    Console.WriteLine("ERROR: {0}", ex.Message);
                }
            }
        }

        public static uint ParseMemoryString(string v)
        {
            if (v == null) {
                return 0;
            }
            if (v.EndsWith("K", StringComparison.OrdinalIgnoreCase)) {
                return uint.Parse(v.Substring(0, v.Length - 1)) << 10;
            }
            if (v.EndsWith("M", StringComparison.OrdinalIgnoreCase)) {
                return uint.Parse(v.Substring(0, v.Length - 1)) << 20;
            }
            if (v.EndsWith("G", StringComparison.OrdinalIgnoreCase)) {
                return uint.Parse(v.Substring(0, v.Length - 1)) << 30;
            }
            return uint.Parse(v);
        }

        public static void LoadCustomEnvironmentVariables(ProcessGovernor procgov, string file)
        {
            if (file == null || !File.Exists(file)) {
                throw new ArgumentException("the text file with environment variables does not exist");
            }
            try {
                using (var reader = File.OpenText(file)) {
                    int linenum = 1;
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        var ind = line.IndexOf("=");
                        if (ind > 0) {
                            var key = line.Substring(0, ind).Trim();
                            if (!string.IsNullOrEmpty(key)) {
                                var val = line.Substring(ind + 1, line.Length - ind - 1).Trim();
                                procgov.AdditionalEnvironmentVars.Add(key, val);
                                linenum++;
                                continue;
                            }
                        }
                        throw new ArgumentException(string.Format("the environment file contains invalid data (line: {0})", linenum));
                    }
                }
            } catch (IOException ex) {
                throw new ArgumentException("can't read the text file with environment variables, {0}", ex.Message);
            }
        }

        static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Process Governor v{0} - allows you to set limits on your processes", 
                Assembly.GetExecutingAssembly().GetName().Version.ToString());
            Console.WriteLine("Copyright (C) 2016 Sebastian Solnica (@lowleveldesign)");
            Console.WriteLine();
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

        public static long CalculateAffinityMaskFromCpuCount(int cpuCount)
        {
            long mask = 0;
            for (int i = 0; i < cpuCount; i++) {
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

        public static void SetupRegistryForProcessGovernor(ProcessGovernor procgov, string appImageExe, RegistryOperation oper)
        {
            if (!IsUserAdmin()) {
                Console.WriteLine("You must be admin to do that. Run the app from the administrative console.");
                return;
            }
            // extrace image.exe if path is provided
            appImageExe = Path.GetFileName(appImageExe);
            var regkey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", true);
            // add to image file execution path
            if (oper == RegistryOperation.INSTALL) {
                regkey = regkey.CreateSubKey(appImageExe);
                regkey.SetValue("Debugger", PrepareDebuggerCommandString(procgov, appImageExe));
            } else if (oper == RegistryOperation.UNINSTALL) {
                regkey.DeleteSubKey(appImageExe, false);

                var appEnvironmentFilePath = GetAppEnvironmentFilePath(appImageExe);
                if (File.Exists(appEnvironmentFilePath)) {
                    File.Delete(appEnvironmentFilePath);
                }
            }
        }

        public static string PrepareDebuggerCommandString(ProcessGovernor procgov, string appImageExe)
        {
            var buffer = new StringBuilder();
            var procgovPath = Assembly.GetAssembly(typeof(ProcessGovernor)).Location;
            buffer.Append('"').Append(procgovPath).Append('"').Append(" --nogui --debugger");

            if (procgov.AdditionalEnvironmentVars.Count > 0) {
                // we will create a file in the procgov folder with the environment variables 
                string appEnvironmentFilePath = GetAppEnvironmentFilePath(appImageExe);
                using (var writer = new StreamWriter(appEnvironmentFilePath, false)) {
                    foreach (var kv in procgov.AdditionalEnvironmentVars) {
                        writer.WriteLine("{0}={1}", kv.Key, kv.Value);
                    }
                }
                buffer.AppendFormat(" --env=\"{0}\"", appEnvironmentFilePath);
            }

            if (procgov.CpuAffinityMask != 0) {
                buffer.AppendFormat(" --cpu=0x{0:X}", procgov.CpuAffinityMask);
            }

            if (procgov.MaxProcessMemory > 0) {
                buffer.AppendFormat(" --maxmem={0}", procgov.MaxProcessMemory);
            }

            return buffer.ToString();
        }

        public static string GetAppEnvironmentFilePath(string appImageExe)
        {
            var procgovPath = Assembly.GetAssembly(typeof(ProcessGovernor)).Location;
            return Path.Combine(Path.GetDirectoryName(procgovPath), appImageExe + ".env");
        }
    }
}
