using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using System.ComponentModel;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.Storage.FileSystem;
using static ProcessGovernor.NtApi;
using System.Numerics;

namespace ProcessGovernor;

sealed class Win32Job(SafeHandle jobHandle, string jobName) : IDisposable
{
    public SafeHandle Handle => jobHandle;

    public nuint NativeHandle => (nuint)jobHandle.DangerousGetHandle();

    public string Name => jobName;

    public void Dispose()
    {
        jobHandle.Dispose();
    }
}

static class Win32JobModule
{
    private static readonly TraceSource logger = Program.Logger;

    public static string GetNewJobName()
    {
        return $"procgov-{Guid.NewGuid():D}";
    }

    public static unsafe Win32Job CreateJob(string jobName)
    {
        var securityAttributes = new SECURITY_ATTRIBUTES();
        securityAttributes.nLength = (uint)Marshal.SizeOf(securityAttributes);

        var hJob = CheckWin32Result(PInvoke.CreateJobObject(securityAttributes, $"Local\\{jobName}"));
        var job = new Win32Job(hJob, jobName);

        return job;
    }

    public static unsafe Win32Job OpenJob(string jobName)
    {
        var jobHandle = PInvoke.OpenJobObject(PInvoke.JOB_OBJECT_QUERY | PInvoke.JOB_OBJECT_SET_ATTRIBUTES |
            PInvoke.JOB_OBJECT_TERMINATE | PInvoke.JOB_OBJECT_ASSIGN_PROCESS | (uint)FILE_ACCESS_RIGHTS.SYNCHRONIZE,
            false, $"Local\\{jobName}");
        if (jobHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new Win32Job(jobHandle, jobName);
    }

    public static void AssignProcess(Win32Job job, SafeHandle processHandle)
    {
        CheckWin32Result(PInvoke.AssignProcessToJobObject(job.Handle, processHandle));
    }

    public static unsafe int AssignIOCompletionPort(Win32Job job, SafeHandle iocp)
    {
        var assocInfo = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT
        {
            CompletionKey = (void*)job.NativeHandle,
            CompletionPort = new HANDLE(iocp.DangerousGetHandle())
        };
        uint size = (uint)Marshal.SizeOf(assocInfo);
        if (!PInvoke.SetInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectAssociateCompletionPortInformation, &assocInfo, size))
        {
            return Marshal.GetLastWin32Error();
        }
        return 0;
    }

    public static void SetLimits(Win32Job job, JobSettings session)
    {
        SetBasicLimits(job, session);
        SetMaxCpuRate(job, session);
        SetCpuAffinity(job, session);
        SetMaxBandwith(job, session);
    }

    // Process affinity is updated in the SetCpuAffinity method - updating through basic limits
    // could fail if Numa node was previously set (issue #46)
    private static unsafe void SetBasicLimits(Win32Job job, JobSettings session)
    {
        var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        var size = (uint)Marshal.SizeOf(limitInfo);
        var length = 0u;
        CheckWin32Result(PInvoke.QueryInformationJobObject(job.Handle,
            JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limitInfo, size, &length));
        Debug.Assert(length == size);

        JOB_OBJECT_LIMIT flags = limitInfo.BasicLimitInformation.LimitFlags & ~JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_AFFINITY;

        if (flags.HasFlag(JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK) || !session.PropagateOnChildProcesses)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
        }

        if (session.MaxProcessMemory > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            limitInfo.ProcessMemoryLimit = checked((nuint)session.MaxProcessMemory);
        }

        if (session.MaxJobMemory > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_MEMORY;
            limitInfo.JobMemoryLimit = checked((nuint)session.MaxJobMemory);
        }

        if (session.MaxWorkingSetSize > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_WORKINGSET;
            limitInfo.BasicLimitInformation.MaximumWorkingSetSize = checked((nuint)session.MaxWorkingSetSize);
            limitInfo.BasicLimitInformation.MinimumWorkingSetSize = checked((nuint)session.MinWorkingSetSize);
        }

        if (session.ProcessUserTimeLimitInMilliseconds > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_TIME;
            limitInfo.BasicLimitInformation.PerProcessUserTimeLimit = 10_000 * session.ProcessUserTimeLimitInMilliseconds; // in 100ns
        }

        if (session.JobUserTimeLimitInMilliseconds > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_TIME;
            limitInfo.BasicLimitInformation.PerJobUserTimeLimit = 10_000 * session.JobUserTimeLimitInMilliseconds; // in 100ns
        }

        if (session.ActiveProcessLimit > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
            limitInfo.BasicLimitInformation.ActiveProcessLimit = session.ActiveProcessLimit;
        }

        if (session.PriorityClass != PriorityClass.Undefined)
        {
            if (session.PriorityClass == PriorityClass.AboveNormal || session.PriorityClass == PriorityClass.High ||
                session.PriorityClass == PriorityClass.Realtime)
            {
                // we need to acquire the SE_INC_BASE_PRIORITY_NAME privilege to set the priority class to AboveNormal, High or Realtime
                AccountPrivilegeModule.EnableProcessPrivileges(PInvoke.GetCurrentProcess_SafeHandle(), [("SeIncreaseBasePriorityPrivilege", true)]);
            }

            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PRIORITY_CLASS;
            Debug.Assert(Enum.IsDefined(session.PriorityClass));
            limitInfo.BasicLimitInformation.PriorityClass = (uint)session.PriorityClass;
        }

        if (flags != 0)
        {
            limitInfo.BasicLimitInformation.LimitFlags = flags;
            CheckWin32Result(PInvoke.SetInformationJobObject(job.Handle,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limitInfo, size));
        }
    }

    private static unsafe void SetMaxCpuRate(Win32Job job, JobSettings session)
    {
        if (session.CpuMaxRate > 0)
        {
            var limitInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                    JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,

                Anonymous = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION._Anonymous_e__Union { CpuRate = session.CpuMaxRate }
            };
            var size = (uint)Marshal.SizeOf(limitInfo);
            CheckWin32Result(PInvoke.SetInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation,
                &limitInfo, size));
        }
    }

    static unsafe void SetCpuAffinity(Win32Job job, JobSettings session)
    {
        if (session.CpuAffinity is { } cpuAffinity && cpuAffinity.Length > 0)
        {
            var groupAffinity = cpuAffinity.Select(aff => new GROUP_AFFINITY { Group = aff.GroupNumber, Mask = (nuint)aff.Affinity }).ToArray();
            var size = (uint)(sizeof(GROUP_AFFINITY) * cpuAffinity.Length);
            fixed (GROUP_AFFINITY* groupAffinityPtr = groupAffinity)
            {
                CheckWin32Result(PInvoke.SetInformationJobObject(job.Handle, 
                    JOBOBJECTINFOCLASS.JobObjectGroupInformationEx, groupAffinityPtr, size));
            }
        }
    }

    static unsafe void SetMaxBandwith(Win32Job job, JobSettings session)
    {
        if (session.MaxBandwidth > 0)
        {
            var limitInfo = new JOBOBJECT_NET_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_ENABLE |
                                JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_MAX_BANDWIDTH,
                MaxBandwidth = session.MaxBandwidth,
                DscpTag = 0
            };
            var size = (uint)Marshal.SizeOf(limitInfo);
            CheckWin32Result(PInvoke.SetInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectNetRateControlInformation,
                &limitInfo, size));
        }
    }

    public static unsafe void WaitForTheJobToComplete(Win32Job job, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            switch (PInvoke.WaitForSingleObject(job.Handle, 200 /* ms */))
            {
                case WAIT_EVENT.WAIT_OBJECT_0:
                    logger.TraceInformation("Job or process got signaled.");
                    return;
                case WAIT_EVENT.WAIT_FAILED:
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                default:
                    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION jobBasicAcctInfo;
                    uint length;
                    CheckWin32Result(PInvoke.QueryInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                        &jobBasicAcctInfo, (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), &length));
                    Debug.Assert((uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() == length);

                    if (jobBasicAcctInfo.ActiveProcesses == 0)
                    {
                        logger.TraceInformation("No active processes in the job - terminating.");
                        return;
                    }
                    break;
            }
        }
    }

    public static void TerminateJob(Win32Job job, uint exitCode)
    {
        PInvoke.TerminateJobObject(job.Handle, exitCode);
    }
}
