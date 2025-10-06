using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using System.ComponentModel;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.Storage.FileSystem;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;
using static ProcessGovernor.Library.Win32.Helpers;
using System.Collections.Immutable;

namespace ProcessGovernor.Library;

public record struct Win32JobSettings(
    ulong MaxProcessMemory, ulong MaxJobMemory, ulong MaxWorkingSetSize,
    ulong MinWorkingSetSize, ImmutableArray<GroupAffinity> CpuAffinity, uint CpuMaxRate,
    ulong MaxBandwidth, uint ProcessUserTimeLimitInMilliseconds, uint JobUserTimeLimitInMilliseconds,
    bool PropagateOnChildProcesses, uint ActiveProcessLimit, PriorityClass PriorityClass
) : IEquatable<Win32JobSettings>
{
    public Win32JobSettings() : this(
        MaxProcessMemory: 0,
        MaxJobMemory: 0,
        MaxWorkingSetSize: 0,
        MinWorkingSetSize: 0,
        CpuAffinity: [],
        CpuMaxRate: 0,
        MaxBandwidth: 0,
        ProcessUserTimeLimitInMilliseconds: 0,
        JobUserTimeLimitInMilliseconds: 0,
        PropagateOnChildProcesses: false,
        ActiveProcessLimit: 0,
        PriorityClass: PriorityClass.Undefined)
    { }

    public readonly bool Equals(Win32JobSettings other) =>
        MaxProcessMemory == other.MaxProcessMemory &&
        MaxJobMemory == other.MaxJobMemory &&
        MaxWorkingSetSize == other.MaxWorkingSetSize &&
        MinWorkingSetSize == other.MinWorkingSetSize &&
        (CpuAffinity == other.CpuAffinity || CpuAffinity.SequenceEqual(other.CpuAffinity)) &&
        CpuMaxRate == other.CpuMaxRate &&
        MaxBandwidth == other.MaxBandwidth &&
        ProcessUserTimeLimitInMilliseconds == other.ProcessUserTimeLimitInMilliseconds &&
        JobUserTimeLimitInMilliseconds == other.JobUserTimeLimitInMilliseconds &&
        PropagateOnChildProcesses == other.PropagateOnChildProcesses &&
        ActiveProcessLimit == other.ActiveProcessLimit &&
        PriorityClass == other.PriorityClass;

    public override readonly int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(MaxProcessMemory);
        hash.Add(MaxJobMemory);
        hash.Add(MaxWorkingSetSize);
        hash.Add(MinWorkingSetSize);
        // We need to iterate through all the elements because we use value (not reference) equality
        foreach (var aff in CpuAffinity) { hash.Add(aff.GetHashCode()); }
        hash.Add(CpuMaxRate);
        hash.Add(MaxBandwidth);
        hash.Add(ProcessUserTimeLimitInMilliseconds);
        hash.Add(JobUserTimeLimitInMilliseconds);
        hash.Add(PropagateOnChildProcesses);
        hash.Add(ActiveProcessLimit);
        hash.Add(PriorityClass);
        return hash.ToHashCode();
    }
}

public static class Win32JobModule
{
    private const uint DefaultJobAccessRights = PInvoke.JOB_OBJECT_QUERY | PInvoke.JOB_OBJECT_SET_ATTRIBUTES |
        PInvoke.JOB_OBJECT_TERMINATE | PInvoke.JOB_OBJECT_ASSIGN_PROCESS | (uint)FILE_ACCESS_RIGHTS.SYNCHRONIZE;

    private static readonly GroupAffinity[] MaxSystemCpuAffinity =
        [..SystemInfoModule.GetSystemInfo().ProcessorGroups.Select(
            pg => new GroupAffinity(pg.Number, pg.AffinityMask))];

    public static unsafe SafeHandle CreateJob(string? jobName = null) =>
        CheckWin32Result(PInvoke.CreateJobObject(
            new SECURITY_ATTRIBUTES() { nLength = (uint)sizeof(SECURITY_ATTRIBUTES) },
            jobName is null ? null : $"Local\\{jobName}"));

    public static SafeHandle OpenJob(string jobName, uint desiredAccess = DefaultJobAccessRights) =>
        OpenJobHandle(jobName, desiredAccess) is var jobHandle && !jobHandle.IsInvalid ?
            jobHandle : throw new Win32Exception(Marshal.GetLastWin32Error());

    // it does not throw exception but returns invalid handle on failure
    public static SafeHandle OpenJobHandle(string jobName, uint desiredAccess = DefaultJobAccessRights) =>
         PInvoke.OpenJobObject(desiredAccess, false, $"Local\\{jobName}");

    public static void AssignProcess(SafeHandle jobHandle, SafeHandle processHandle) =>
        CheckWin32Result(PInvoke.AssignProcessToJobObject(jobHandle, processHandle));

    public static unsafe void AssignIOCompletionPort(SafeHandle jobHandle, SafeHandle iocp)
    {
        var assocInfo = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT
        {
            CompletionKey = (void*)jobHandle.DangerousGetHandle(),
            CompletionPort = new HANDLE(iocp.DangerousGetHandle())
        };
        uint size = (uint)Marshal.SizeOf(assocInfo);
        CheckWin32Result(PInvoke.SetInformationJobObject(jobHandle.ToHANDLE(),
            JOBOBJECTINFOCLASS.JobObjectAssociateCompletionPortInformation, &assocInfo, size));
    }

    public static bool IsProcessInJob(SafeHandle processHandle, SafeHandle jobHandle) =>
        PInvoke.IsProcessInJob(processHandle, jobHandle, out var result) ?
            result : throw new Win32Exception(Marshal.GetLastWin32Error());

    public static unsafe void SetJobFreezeStatus(SafeHandle jobHandle, bool freeze)
    {
        var freezeInfo = new Win32.JOBOBJECT_FREEZE_INFORMATION
        {
            Flags = Win32.FreezeInformationFlags.FreezeOperation,
            Freeze = Convert.ToByte(freeze)
        };
        uint size = (uint)Marshal.SizeOf(freezeInfo);
        if (!PInvoke.SetInformationJobObject(jobHandle.ToHANDLE(),
                JOBOBJECTINFOCLASS.JobObjectReserved1Information /* 18 */, &freezeInfo, size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static void SetLimits(SafeHandle jobHandle, Win32JobSettings settings)
    {
        bool jobHandleAddRef = false;
        try
        {
            jobHandle.DangerousAddRef(ref jobHandleAddRef);

            HANDLE jobWin32Handle = (HANDLE)jobHandle.DangerousGetHandle();
            SetBasicLimits(jobWin32Handle, settings);
            SetMaxCpuRate(jobWin32Handle, settings);
            SetCpuAffinity(jobWin32Handle, settings);
            SetMaxBandwith(jobWin32Handle, settings);
        }
        finally
        {
            if (jobHandleAddRef)
            {
                jobHandle.DangerousRelease();
            }
        }

        static unsafe void SetBasicLimits(HANDLE jobHandle, Win32JobSettings settings)
        {
            // Process affinity is updated in the SetCpuAffinity method - updating through basic limits
            // (JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_AFFINITY) could fail if Numa node was previously set (issue #46)

            var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            var size = (uint)Marshal.SizeOf(limitInfo);

            JOB_OBJECT_LIMIT flags = 0;

            // make sure we have the required privileges
            if (settings.MaxWorkingSetSize > 0 || 
                settings.PriorityClass is PriorityClass.AboveNormal or PriorityClass.High or PriorityClass.Realtime)
            {
                if (!AccountPrivilegeModule.TryEnablingProcessPrivileges(PInvoke.GetCurrentProcess_SafeHandle(),
                    ["SeIncreaseBasePriorityPrivilege"], out var errors) && errors is [(var privilegeName, var errorCode)])
                {
                    throw new Win32Exception(errorCode, $"The current process is missing the privilege {privilegeName}.");
                }
            }

            if (!settings.PropagateOnChildProcesses)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
            }

            if (settings.MaxProcessMemory > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
                limitInfo.ProcessMemoryLimit = checked((nuint)settings.MaxProcessMemory);
            }

            if (settings.MaxJobMemory > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_MEMORY;
                limitInfo.JobMemoryLimit = checked((nuint)settings.MaxJobMemory);
            }

            if (settings.MaxWorkingSetSize > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_WORKINGSET;
                limitInfo.BasicLimitInformation.MaximumWorkingSetSize = checked((nuint)settings.MaxWorkingSetSize);
                limitInfo.BasicLimitInformation.MinimumWorkingSetSize = checked((nuint)settings.MinWorkingSetSize);
            }

            if (settings.ProcessUserTimeLimitInMilliseconds > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_TIME;
                limitInfo.BasicLimitInformation.PerProcessUserTimeLimit = 10_000 * settings.ProcessUserTimeLimitInMilliseconds; // in 100ns
            }

            if (settings.JobUserTimeLimitInMilliseconds > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_TIME;
                limitInfo.BasicLimitInformation.PerJobUserTimeLimit = 10_000 * settings.JobUserTimeLimitInMilliseconds; // in 100ns
            }

            if (settings.ActiveProcessLimit > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
                limitInfo.BasicLimitInformation.ActiveProcessLimit = settings.ActiveProcessLimit;
            }

            if (settings.PriorityClass != PriorityClass.Undefined)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PRIORITY_CLASS;
                Debug.Assert(Enum.IsDefined(settings.PriorityClass));
                limitInfo.BasicLimitInformation.PriorityClass = (uint)settings.PriorityClass;
            }

            // we always updated the job (even when flags are empty), thus, allowing resetting
            limitInfo.BasicLimitInformation.LimitFlags = flags;
            CheckWin32Result(PInvoke.SetInformationJobObject(jobHandle,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limitInfo, size));
        }

        static unsafe void SetMaxCpuRate(HANDLE jobHandle, Win32JobSettings settings)
        {
            var limitInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION();
            var infoClass = JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation;
            if (settings.CpuMaxRate > 0 || PInvoke.QueryInformationJobObject(jobHandle, infoClass, &limitInfo,
                (uint)sizeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION)) &&
                    limitInfo.ControlFlags.HasFlag(JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE))
            {

                limitInfo.ControlFlags = settings.CpuMaxRate > 0 ?
                        (JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                         JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP) : 0;
                limitInfo.Anonymous = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION._Anonymous_e__Union { CpuRate = settings.CpuMaxRate };
                CheckWin32Result(PInvoke.SetInformationJobObject(jobHandle, infoClass, &limitInfo,
                    (uint)sizeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION)));
            }
        }

        static unsafe void SetCpuAffinity(HANDLE jobHandle, Win32JobSettings session)
        {
            // settings empty group affinity resets it to default (so maximum affinity)
            GROUP_AFFINITY[] groupAffinity = session.CpuAffinity.Length > 0 ?
                [.. session.CpuAffinity.Select(aff => new GROUP_AFFINITY { Group = aff.GroupNumber, Mask = (nuint)aff.Affinity })] : [];
            var size = (uint)(sizeof(GROUP_AFFINITY) * groupAffinity.Length);
            fixed (GROUP_AFFINITY* groupAffinityPtr = groupAffinity)
            {
                CheckWin32Result(PInvoke.SetInformationJobObject(jobHandle,
                    JOBOBJECTINFOCLASS.JobObjectGroupInformationEx, groupAffinityPtr, size));
            }
        }

        static unsafe void SetMaxBandwith(HANDLE jobHandle, Win32JobSettings settings)
        {
            var limitInfo = new JOBOBJECT_NET_RATE_CONTROL_INFORMATION();
            var infoClass = JOBOBJECTINFOCLASS.JobObjectNetRateControlInformation;
            if (settings.MaxBandwidth > 0 || PInvoke.QueryInformationJobObject(jobHandle, infoClass, &limitInfo,
                (uint)sizeof(JOBOBJECT_NET_RATE_CONTROL_INFORMATION)) &&
                    limitInfo.ControlFlags.HasFlag(JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_ENABLE))
            {
                limitInfo.ControlFlags = settings.MaxBandwidth > 0 ?
                    (JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_ENABLE |
                     JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_MAX_BANDWIDTH) : 0;
                limitInfo.MaxBandwidth = settings.MaxBandwidth;
                limitInfo.DscpTag = 0;

                CheckWin32Result(PInvoke.SetInformationJobObject(jobHandle, infoClass, &limitInfo,
                    (uint)sizeof(JOBOBJECT_NET_RATE_CONTROL_INFORMATION)));
            }
        }
    }

    public static unsafe void WaitForTheJobToComplete(SafeHandle jobHandle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            switch (PInvoke.WaitForSingleObject(jobHandle.ToHANDLE(), 200 /* ms */))
            {
                case WAIT_EVENT.WAIT_OBJECT_0:
                    Logger.TraceVerbose("[win32_job] Job or process got signaled.");
                    return;
                case WAIT_EVENT.WAIT_FAILED:
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                default:
                    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION jobBasicAcctInfo;
                    uint length;
                    CheckWin32Result(PInvoke.QueryInformationJobObject(jobHandle.ToHANDLE(), JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                        &jobBasicAcctInfo, (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), &length));
                    Debug.Assert((uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() == length);

                    if (jobBasicAcctInfo.ActiveProcesses == 0)
                    {
                        Logger.TraceInformation("[win32_job] No active processes in a job.");
                        return;
                    }
                    break;
            }
        }
    }

    public static void TerminateJob(SafeHandle jobHandle, uint exitCode)
    {
        PInvoke.TerminateJobObject(jobHandle.ToHANDLE(), exitCode);
    }

    public static unsafe Win32JobSettings QueryJobSettings(SafeHandle jobHandle)
    {
        var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        uint length = (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);

        CheckWin32Result(PInvoke.QueryInformationJobObject(jobHandle.ToHANDLE(),
            JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limitInfo, length, null));

        var basicLimits = limitInfo.BasicLimitInformation;
        var flags = basicLimits.LimitFlags;

        // Extract Basic Limits
        ulong maxProcessMemory = (flags & JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY) != 0
            ? limitInfo.ProcessMemoryLimit : 0;
        ulong maxJobMemory = (flags & JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_MEMORY) != 0
            ? limitInfo.JobMemoryLimit : 0;
        (ulong minWorkingSetSize, ulong maxWorkingSetSize) = (flags & JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_WORKINGSET) != 0 ?
            (basicLimits.MinimumWorkingSetSize, basicLimits.MaximumWorkingSetSize) : (0, 0);
        uint activeProcessLimit = (flags & JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_ACTIVE_PROCESS) != 0
            ? basicLimits.ActiveProcessLimit : 0;

        // Times are in 100ns units, convert to ms
        uint processUserTimeLimit = (flags & JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_TIME) != 0
            ? (uint)(basicLimits.PerProcessUserTimeLimit / 10_000) : 0;
        uint jobUserTimeLimit = (flags & JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_TIME) != 0
            ? (uint)(basicLimits.PerJobUserTimeLimit / 10_000) : 0;

        PriorityClass priorityClass = PriorityClass.Undefined;
        if ((flags & JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PRIORITY_CLASS) != 0)
        {
            priorityClass = (PriorityClass)basicLimits.PriorityClass;
        }

        bool propagateOnChildProcesses = (flags & JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK) == 0;

        // Construct and return the settings object
        return new Win32JobSettings(
            MaxProcessMemory: maxProcessMemory,
            MaxJobMemory: maxJobMemory,
            MaxWorkingSetSize: maxWorkingSetSize,
            MinWorkingSetSize: minWorkingSetSize,
            CpuAffinity: QueryCpuAffinity(jobHandle),
            CpuMaxRate: QueryCpuMaxRate(jobHandle),
            MaxBandwidth: QueryMaxBandwidth(jobHandle),
            ProcessUserTimeLimitInMilliseconds: processUserTimeLimit,
            JobUserTimeLimitInMilliseconds: jobUserTimeLimit,
            PropagateOnChildProcesses: propagateOnChildProcesses,
            ActiveProcessLimit: activeProcessLimit,
            PriorityClass: priorityClass
        );

        // QueryJobSettings: helper functions

        static uint QueryCpuMaxRate(SafeHandle jobHandle)
        {
            var cpuRateInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION();
            var length = (uint)sizeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION);

            CheckWin32Result(PInvoke.QueryInformationJobObject(jobHandle.ToHANDLE(),
                JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation, &cpuRateInfo, length, null));

            var flags = JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP;
            return (cpuRateInfo.ControlFlags & flags) == flags ? cpuRateInfo.Anonymous.CpuRate : 0;
        }

        static ulong QueryMaxBandwidth(SafeHandle jobHandle)
        {
            var netRateInfo = new JOBOBJECT_NET_RATE_CONTROL_INFORMATION();
            var length = (uint)sizeof(JOBOBJECT_NET_RATE_CONTROL_INFORMATION);

            CheckWin32Result(PInvoke.QueryInformationJobObject(jobHandle.ToHANDLE(),
                JOBOBJECTINFOCLASS.JobObjectNetRateControlInformation, &netRateInfo, length, null));

            var flags = JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_ENABLE |
                JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_MAX_BANDWIDTH;
            return (netRateInfo.ControlFlags & flags) == flags ? netRateInfo.MaxBandwidth : 0;
        }

        static unsafe ImmutableArray<GroupAffinity> QueryCpuAffinity(SafeHandle jobHandle)
        {
            GROUP_AFFINITY[] groupAffinity = [new()];
            uint requiredLength = 0;
            int err = 0;

            fixed (GROUP_AFFINITY* ptr = groupAffinity)
            {
                if (PInvoke.QueryInformationJobObject(jobHandle.ToHANDLE(), JOBOBJECTINFOCLASS.JobObjectGroupInformationEx, ptr,
                    (uint)(groupAffinity.Length * sizeof(GROUP_AFFINITY)), &requiredLength))
                {
                    return ConvertFromSystemAffinity(groupAffinity);
                }
                err = Marshal.GetLastWin32Error();
            }
            if (err == (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                groupAffinity = new GROUP_AFFINITY[requiredLength / sizeof(GROUP_AFFINITY)];
                Debug.Assert(groupAffinity.Length * sizeof(GROUP_AFFINITY) == requiredLength);
                fixed (GROUP_AFFINITY* ptr = groupAffinity)
                {
                    if (!PInvoke.QueryInformationJobObject(jobHandle.ToHANDLE(), JOBOBJECTINFOCLASS.JobObjectGroupInformationEx, ptr, requiredLength, &requiredLength))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
                return ConvertFromSystemAffinity(groupAffinity);
            }
            else
            {
                throw new Win32Exception(err);
            }

            static ImmutableArray<GroupAffinity> ConvertFromSystemAffinity(GROUP_AFFINITY[] systemAffinity) =>
                // we treat maximum affinity as empty affinity
                systemAffinity.Select(g => new GroupAffinity(g.Group, g.Mask)).ToImmutableArray() is var aff &&
                    aff.SequenceEqual(MaxSystemCpuAffinity) ? [] : aff;
        }
    }
}
