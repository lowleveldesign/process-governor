using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessGovernor;

static partial class Program
{

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
