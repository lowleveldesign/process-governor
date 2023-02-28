using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProcessGovernor;

public sealed class Win32Job : IDisposable
{
    private readonly SafeHandle hJob;
    private readonly string jobName;
    private readonly Stopwatch? stopWatch;
    private readonly long clockTimeLimitInMilliseconds;

    public Win32Job(SafeHandle hJob, string jobName, long clockTimeLimitInMilliseconds = 0L)
    {
        this.hJob = hJob;
        this.jobName = jobName;

        this.clockTimeLimitInMilliseconds = clockTimeLimitInMilliseconds;
        stopWatch = clockTimeLimitInMilliseconds > 0 ? Stopwatch.StartNew() : null;
    }

    public SafeHandle JobHandle => hJob;

    public string JobName => jobName;

    public bool IsTimedOut => stopWatch != null && stopWatch.ElapsedMilliseconds > clockTimeLimitInMilliseconds;

    public void Dispose()
    {
        hJob.Dispose();
    }
}
