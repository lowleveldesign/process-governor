using System.Runtime.InteropServices;

namespace ProcessGovernor;

public record Win32Job(SafeHandle JobHandle, string JobName, SafeHandle? ProcessHandle = null, long ClockTimeLimitInMilliseconds = 0L) : IDisposable
{
    private readonly DateTime startTimeUtc = DateTime.UtcNow;

    public bool IsTimedOut => ClockTimeLimitInMilliseconds > 0 
        && DateTime.UtcNow.Subtract(startTimeUtc).TotalMilliseconds >= ClockTimeLimitInMilliseconds;

    // When we are monitoring only a specific process, we will wait for its termination. Otherwise,
    // we will wait for the job object to be signaled.
    public SafeHandle WaitHandle => ProcessHandle ?? JobHandle;

    public void Dispose()
    {
        JobHandle.Dispose();
        if (ProcessHandle is { } h && !h.IsInvalid)
        {
            h.Dispose();
        }
    }
}
