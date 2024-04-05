using System.Runtime.InteropServices;

namespace ProcessGovernor;

public record Win32Job(SafeHandle JobHandle, string JobName, SafeHandle? FirstProcessHandle = null, long ClockTimeLimitInMilliseconds = 0L) : IDisposable
{
    private readonly DateTime startTimeUtc = DateTime.UtcNow;

    public bool IsTimedOut => ClockTimeLimitInMilliseconds > 0 
        && DateTime.UtcNow.Subtract(startTimeUtc).TotalMilliseconds >= ClockTimeLimitInMilliseconds;

    public SafeHandle Handle => JobHandle;

    public void Dispose()
    {
        JobHandle.Dispose();
        if (FirstProcessHandle is { } h && !h.IsInvalid)
        {
            h.Dispose();
        }
    }
}
