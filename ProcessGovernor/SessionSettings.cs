using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LowLevelDesign
{
    public sealed class SessionSettings
    {
        private readonly Dictionary<string, string> additionalEnvironmentVars =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ulong MaxProcessMemory { get; set; }
        
        public ulong MaxJobMemory { get; set; }

        public ulong MaxWorkingSetSize { get; set; }

        public ulong MinWorkingSetSize { get; set; }

        public ushort NumaNode { get; set; } = 0xffff;

        public ulong CpuAffinityMask { get; set; }

        public uint CpuMaxRate { get; set; }

        public ulong MaxBandwidth { get; set; }

        public uint ProcessUserTimeLimitInMilliseconds { get; set; }

        public uint JobUserTimeLimitInMilliseconds { get; set; }

        public uint ClockTimeLimitInMilliseconds { get; set; }

        public bool SpawnNewConsoleWindow { get; set; }

        public bool PropagateOnChildProcesses { get; set; }

        public Dictionary<string, string> AdditionalEnvironmentVars => additionalEnvironmentVars;
    }
}
