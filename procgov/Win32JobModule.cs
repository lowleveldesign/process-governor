using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using System.ComponentModel;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.Storage.FileSystem;
using System.Diagnostics.CodeAnalysis;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

internal static class Win32JobModule
{
    private static readonly TraceSource logger = Program.Logger;

    public static unsafe Win32Job CreateJobObjectAndAssignProcess(SafeHandle processHandle, string jobName, bool propagateOnChildProcesses,
        long clockTimeLimitInMilliseconds)
    {
        var job = CreateJobObject(jobName, clockTimeLimitInMilliseconds);
        AssignProcess(job, processHandle, propagateOnChildProcesses);

        return job;
    }

    public static unsafe Win32Job CreateJobObject(string jobName, long clockTimeLimitInMilliseconds)
    {
        var securityAttributes = new SECURITY_ATTRIBUTES();
        securityAttributes.nLength = (uint)Marshal.SizeOf(securityAttributes);

        var hJob = CheckWin32Result(PInvoke.CreateJobObject(securityAttributes, $"Local\\{jobName}"));
        var job = new Win32Job(hJob, jobName, clockTimeLimitInMilliseconds);

        return job;
    }

    public static void AssignProcess(Win32Job job, SafeHandle processHandle, bool propagateOnChildProcesses)
    {
        using var currentProcessHandle = PInvoke.GetCurrentProcess_SafeHandle();
        CheckWin32Result(PInvoke.DuplicateHandle(currentProcessHandle, job.JobHandle, processHandle,
            out _, (uint)FILE_ACCESS_RIGHTS.STANDARD_RIGHTS_READ, propagateOnChildProcesses, 0));

        CheckWin32Result(PInvoke.AssignProcessToJobObject(job.JobHandle, processHandle));
    }

    public static unsafe bool TryOpen(string jobName, [NotNullWhen(true)] out SafeHandle jobHandle)
    {
        jobHandle = PInvoke.OpenJobObject(PInvoke.JOB_OBJECT_QUERY | PInvoke.JOB_OBJECT_SET_ATTRIBUTES |
            PInvoke.JOB_OBJECT_TERMINATE | PInvoke.JOB_OBJECT_ASSIGN_PROCESS | (uint)FILE_ACCESS_RIGHTS.SYNCHRONIZE,
            false, $"Local\\{jobName}");
        return !jobHandle.IsInvalid;
    }

    public static void SetLimits(Win32Job job, SessionSettings session, ulong systemOrProcessorGroupAffinityMask)
    {
        if (session.NumaNode != 0xffff)
        {
            CheckWin32Result(PInvoke.GetNumaHighestNodeNumber(out var highestNodeNumber));
            if (session.NumaNode > highestNodeNumber)
            {
                throw new ArgumentException($"Incorrect NUMA node number. The highest accepted number is {highestNodeNumber}.");
            }
        }

        SetBasicLimits(job, session);
        SetMaxCpuRate(job, session, systemOrProcessorGroupAffinityMask);
        SetNumaAffinity(job, session, systemOrProcessorGroupAffinityMask);
        SetMaxBandwith(job, session);
    }

    // Process affinity is updated in the SetNumaAffinity method - updating through basic limits
    // could fail if Numa node was previously set (issue #46)
    private static unsafe void SetBasicLimits(Win32Job job, SessionSettings session)
    {
        var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        var size = (uint)Marshal.SizeOf(limitInfo);
        var length = 0u;
        CheckWin32Result(PInvoke.QueryInformationJobObject(job.JobHandle,
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
            limitInfo.ProcessMemoryLimit = (UIntPtr)session.MaxProcessMemory;
        }

        if (session.MaxJobMemory > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_MEMORY;
            limitInfo.JobMemoryLimit = (UIntPtr)session.MaxJobMemory;
        }

        if (session.MaxWorkingSetSize > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_WORKINGSET;
            limitInfo.BasicLimitInformation.MaximumWorkingSetSize = (UIntPtr)session.MaxWorkingSetSize;
            limitInfo.BasicLimitInformation.MinimumWorkingSetSize = (UIntPtr)session.MinWorkingSetSize;
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

        if (flags != 0)
        {
            limitInfo.BasicLimitInformation.LimitFlags = flags;
            CheckWin32Result(PInvoke.SetInformationJobObject(job.JobHandle,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limitInfo, size));
        }
    }

    private static unsafe void SetMaxCpuRate(Win32Job job, SessionSettings session, ulong affinityMask)
    {
        if (session.CpuMaxRate > 0)
        {
            // Set CpuRate to a percentage times 100. For example, to let the job use 20% of the CPU, 
            // set CpuRate to 2,000.
            uint finalCpuRate = session.CpuMaxRate * 100;

            // CPU rate is set for the whole system so includes all the logical CPUs. When 
            // we have the CPU affinity set, we will divide the rate accordingly.
            if (session.CpuAffinityMask != 0)
            {
                ulong affinity = affinityMask & session.CpuAffinityMask;
                var numberOfSelectedCores = (uint)Enumerable.Range(0, 8 * sizeof(ulong)).Count(i => (affinity & (1UL << i)) != 0);
                Debug.Assert(numberOfSelectedCores < Environment.ProcessorCount);
                finalCpuRate /= ((uint)Environment.ProcessorCount / numberOfSelectedCores);
            }

            // configure CPU rate limit
            var limitInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                    JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,

                Anonymous = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION._Anonymous_e__Union { CpuRate = finalCpuRate }
            };
            var size = (uint)Marshal.SizeOf(limitInfo);
            CheckWin32Result(PInvoke.SetInformationJobObject(job.JobHandle, JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation,
                &limitInfo, size));
        }
    }

    static unsafe void SetNumaAffinity(Win32Job job, SessionSettings session, ulong systemOrProcessorGroupAffinityMask)
    {
        if (session.NumaNode != 0xffff || session.CpuAffinityMask != 0)
        {
            GROUP_AFFINITY calculateGroupAffinity()
            {
                if (session.NumaNode != 0xffff)
                {
                    CheckWin32Result(PInvoke.GetNumaNodeProcessorMaskEx(session.NumaNode, out var groupAffinity));
                    /*
                    In my test Hyper-V environment, I set the number of NUMA nodes to four with two cores per node.
                    The results of running the following loop are presented below:

                    GetNumaHighestNodeNumber(out var highestNode);

                    for (ulong i = 0; i <= highestNode; i++) {
                        Console.WriteLine($"Node: {i:X}");
                        GetNumaNodeProcessorMaskEx((ushort)i, out var affinity);
                        $"Mask: {affinity.Mask:X}".Dump();
                    }

                    Results:

                    Node: 0
                    Mask: 3  (0x000000011)
                    Node: 1
                    Mask: 12  (0x00001100)
                    Node: 2
                    Mask: 48  (0x00110000)
                    Node: 3
                    Mask: 192 (0x11000000)
                    */

                    var nodeAffinityMask = Convert.ToUInt64(groupAffinity.Mask);
                    if (session.CpuAffinityMask != 0)
                    {
                        // When CPU affinity is set, we can't simply use 
                        // NUMA affinity, but rather need to apply the CPU affinity
                        // settings to the select NUMA node.
                        var firstNonZeroBitPosition = 0;
                        while ((nodeAffinityMask & (1UL << firstNonZeroBitPosition)) == 0)
                        {
                            firstNonZeroBitPosition++;
                        }
                        session.CpuAffinityMask <<= firstNonZeroBitPosition & 0x3f;
                    }
                    else
                    {
                        session.CpuAffinityMask = nodeAffinityMask;
                    }
                    // NOTE: this could result in an overflow on 32-bit apps, but I can't
                    // think of a nice way of handling it here
                    groupAffinity.Mask = (nuint)(session.CpuAffinityMask & groupAffinity.Mask);

                    return groupAffinity;
                }
                else
                {
                    GROUP_AFFINITY groupAffinity = new();
                    var size = (uint)Marshal.SizeOf(groupAffinity);
                    var length = 0u;
                    CheckWin32Result(PInvoke.QueryInformationJobObject(job.JobHandle, JOBOBJECTINFOCLASS.JobObjectGroupInformationEx,
                            &groupAffinity, size, &length));

                    // currently we support only one processor group per process
                    Debug.Assert(length == size);

                    // groupAffinity.Mask retrieved from the QueryInformationJobObject is the affinity mask
                    // of the group. It is hard to tell if it's correct - I would expect the currently set affinity,
                    // but I will stick to the affinity retrieved from the GetProcessAffinityMask
                    groupAffinity.Mask = (nuint)(session.CpuAffinityMask & systemOrProcessorGroupAffinityMask);

                    return groupAffinity;
                }
            }

            var groupAffinity = calculateGroupAffinity();
            logger.TraceInformation($"Group affinity number: {groupAffinity.Group}, mask: 0x{groupAffinity.Mask:x}");

            var size = (uint)Marshal.SizeOf(groupAffinity);
            CheckWin32Result(PInvoke.SetInformationJobObject(job.JobHandle, JOBOBJECTINFOCLASS.JobObjectGroupInformationEx,
                &groupAffinity, size));

        }
    }

    static unsafe void SetMaxBandwith(Win32Job job, SessionSettings session)
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
            CheckWin32Result(PInvoke.SetInformationJobObject(job.JobHandle, JOBOBJECTINFOCLASS.JobObjectNetRateControlInformation,
                &limitInfo, size));
        }
    }

    public static unsafe int WaitForTheJobToComplete(Win32Job job, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            switch (PInvoke.WaitForSingleObject(job.JobHandle, 200 /* ms */))
            {
                case WAIT_EVENT.WAIT_OBJECT_0:
                    logger.TraceInformation("End of job time limit passed - terminating.");
                    return 2;
                case WAIT_EVENT.WAIT_FAILED:
                    throw new Win32Exception();
                default:
                    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION jobBasicAcctInfo;
                    uint length;
                    CheckWin32Result(PInvoke.QueryInformationJobObject(job.JobHandle, JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                        &jobBasicAcctInfo, (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), &length));
                    Debug.Assert((uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() == length);

                    if (jobBasicAcctInfo.ActiveProcesses == 0)
                    {
                        logger.TraceInformation("No active processes in the job - terminating.");
                        return 0;
                    }
                    else if (job.IsTimedOut)
                    {
                        logger.TraceInformation("Clock time limit passed - terminating.");
                        PInvoke.TerminateJobObject(job.JobHandle, 1);
                        return 1;
                    }
                    else
                    {
                        break;
                    }
            }
        }

        return 3;
    }
}
