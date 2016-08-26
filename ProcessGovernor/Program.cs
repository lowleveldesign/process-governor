using NDesk.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LowLevelDesign
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            List<string> procargs = null;
            bool showhelp = false;
            int pid = 0;

            var procgov = new ProcessGovernor();

            var p = new OptionSet()
            {
                { "m|maxmem=", "Max committed memory usage in bytes (accepted suffixes: K, M or G).",
                    v => { procgov.MaxProcessMemory = ParseMemoryString(v); } },
                { "w|ws=", "Max working set (accepted suffixes: K, M or G).",
                    v => { procgov.MaxWorkingSet = ParseMemoryString(v); } },
                { "env=", "A text file with environment variables (each line in form: VAR=VAL). Applies only to newly created processes.",
                    v => LoadCustomEnvironmentVariables(procgov, v) },
                { "c|cpu=", "If in hex (starts with 0x) it is treated as an affinity mask, otherwise it is a number of CPU cores assigned to your app.",
                    v => {
                        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                            procgov.CpuAffinityMask = long.Parse(v, NumberStyles.AllowHexSpecifier | NumberStyles.HexNumber);
                        } else {
                            procgov.CpuAffinityMask = CalculateAffinityMaskFromCpuCount(int.Parse(v));
                        }
                    }},
                { "p|pid=", "Attach to an already running process", (int v) => pid = v },
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

            if (!showhelp && (procargs.Count == 0 && pid == 0) || (pid > 0 && procargs.Count > 0)) {
                Console.WriteLine("ERROR: please provide either process name to start or PID of the already running process");
                Console.WriteLine();
                showhelp = true;
            }

            if (showhelp) {
                ShowHelp(p);
                return;
            }

            if (pid > 0) {
                procgov.AttachToProcess(pid);
            } else {
                procgov.StartProcess(procargs);
            }
        }

        static uint ParseMemoryString(string v)
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

        static void LoadCustomEnvironmentVariables(ProcessGovernor procgov, string file)
        {
            if (file == null || !File.Exists(file)) {
                throw new ArgumentException("the text file with environment variables does not exist");
            }
            try {
                using (var reader = File.OpenText(file)) {
                    int linenum = 1;
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        var kv = line.TrimStart().Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                        if (kv.Length != 2) {
                            throw new ArgumentException(string.Format("the environment file contains invalid data (line: {0})", linenum));
                        }
                        procgov.AddEnvironmentVariable(kv[0], kv[1]);
                        linenum++;
                    }
                }
            } catch (IOException ex) {
                throw new ArgumentException("can't read the text file with environment variables, {0}", ex.Message);
            }
        }

        static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: procgov [OPTIONS] args");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
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

    }
}
