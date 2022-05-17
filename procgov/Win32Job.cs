using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LowLevelDesign
{
    public sealed class Win32Job : IDisposable
    {
        private readonly SafeHandle hJob, hProcess;
        private readonly bool propagateOnChildProcesses;
        private readonly Stopwatch? stopWatch;
        private readonly long clockTimeLimitInMilliseconds;

        public Win32Job(SafeHandle hJob, SafeHandle hProcess, bool propagateOnChildProcesses,
            long clockTimeLimitInMilliseconds = 0L)
        {
            this.hJob = hJob;
            this.hProcess = hProcess;
            this.propagateOnChildProcesses = propagateOnChildProcesses;

            this.clockTimeLimitInMilliseconds = clockTimeLimitInMilliseconds;
            stopWatch = clockTimeLimitInMilliseconds > 0 ? Stopwatch.StartNew() : null;
        }

        public SafeHandle ProcessHandle => hProcess;

        public SafeHandle JobHandle => hJob;

        public bool PropagateOnChildProcesses => propagateOnChildProcesses;

        public bool IsTimedOut => stopWatch != null && stopWatch.ElapsedMilliseconds > clockTimeLimitInMilliseconds;

        public void Dispose()
        {
            hJob.Dispose();
            hProcess.Dispose();
        }
    }
}
