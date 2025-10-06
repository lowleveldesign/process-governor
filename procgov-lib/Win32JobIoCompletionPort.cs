using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

using static ProcessGovernor.Library.Win32.Helpers;

namespace ProcessGovernor.Library;

public sealed class Win32JobIoCompletionPort : IDisposable
{
    private static readonly SafeHandle NullHandle = new SafeFileHandle();

    private readonly ConcurrentDictionary<nuint, RunningJobId> jobHandleJobIdMap = [];
    private readonly SafeHandle iocpHandle;

    public Win32JobIoCompletionPort()
    {
        iocpHandle = new SafeFileHandle(CheckWin32Result(
            PInvoke.CreateIoCompletionPort(HANDLE.INVALID_HANDLE_VALUE, (HANDLE)nuint.Zero, nuint.Zero, 0)), true);
    }

    public int JobsCount => jobHandleJobIdMap.Count;

    public void AssignJob(SafeHandle jobHandle, RunningJobId jobId)
    {
        jobHandleJobIdMap.AddOrUpdate((nuint)jobHandle.DangerousGetHandle(), _ =>
        {
            Win32JobModule.AssignIOCompletionPort(jobHandle, iocpHandle);
            return jobId;
        }, (_, id) =>
        {
            Debug.Assert(false); // I rather should not be calling this
            return id;
        });
    }

    public void UnassignJob(SafeHandle jobHandle) =>
        jobHandleJobIdMap.Remove((nuint)jobHandle.DangerousGetHandle(), out _);

    public bool TryRead([MaybeNullWhen(false)] out IMonitorNotification ev)
    {
        unsafe
        {
            if (!PInvoke.GetQueuedCompletionStatus(iocpHandle, out uint msgIdentifier,
                out nuint jobHandle, out var msgData, 200 /* ms */))
            {
                var winerr = Marshal.GetLastWin32Error();
                if (winerr == (int)WAIT_EVENT.WAIT_TIMEOUT)
                {
                    // regular timeout
                    ev = null;
                    return false;
                }
                throw new Win32Exception(winerr);
            }

            if (!jobHandleJobIdMap.TryGetValue(jobHandle, out var jobId))
            {
                // this might happen if we already processed NoProcessesInJobEvent (it shouldn't though)
                Debug.Assert(false);
                ev = null;
                return false;
            }

            ev = msgIdentifier switch
            {
                PInvoke.JOB_OBJECT_MSG_NEW_PROCESS => new NewProcessEvent(jobId, (uint)msgData),
                PInvoke.JOB_OBJECT_MSG_EXIT_PROCESS => new ExitProcessEvent(jobId, (uint)msgData, false),
                PInvoke.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS => new ExitProcessEvent(jobId, (uint)msgData, true),
                PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO => new NoProcessesInJobEvent(jobId),
                PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_LIMIT => new JobLimitExceededEvent(jobId, LimitType.ActiveProcessNumber),
                PInvoke.JOB_OBJECT_MSG_JOB_MEMORY_LIMIT => new JobLimitExceededEvent(jobId, LimitType.Memory),
                PInvoke.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT => new ProcessLimitExceededEvent(jobId, (uint)msgData, LimitType.Memory),
                PInvoke.JOB_OBJECT_MSG_END_OF_PROCESS_TIME => new ProcessLimitExceededEvent(jobId, (uint)msgData, LimitType.CpuTime),
                PInvoke.JOB_OBJECT_MSG_END_OF_JOB_TIME => new JobLimitExceededEvent(jobId, LimitType.CpuTime),
                _ => throw new NotImplementedException()
            };
            return true;
        }
    }

    public void Dispose()
    {
        iocpHandle.Dispose();
    }
}
