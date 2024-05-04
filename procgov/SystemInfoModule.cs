using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.System.ProcessStatus;

namespace ProcessGovernor;

record SystemInfo(NumaNode[] NumaNodes, ProcessorGroup[] ProcessorGroups, ProcessorCore[] CpuCores);

record ProcessorGroup(ushort Number, nuint AffinityMask);

record NumaNode(uint Number, ProcessorGroup[] ProcessorGroups);

record ProcessorCore(bool MultiThreaded, ProcessorGroup ProcessorGroup);

public record PerformanceInformation(nuint PhysicalTotalKB, nuint PhysicalAvailableKB, nuint CommitTotalKB, nuint CommitLimitKB);


static class SystemInfoModule
{
    public static SystemInfo GetSystemInfo()
    {
        return new(GetNumaNodes(), GetProcessorGroups(), GetProcessorCores());

        static NumaNode[] GetNumaNodes()
        {
            unsafe
            {
                uint length = 0;
                PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationNumaNodeEx, null, &length);
                if (Marshal.GetLastWin32Error() is var lastError && lastError != (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
                {
                    throw new Win32Exception(lastError);
                }

                byte* infoBytes = stackalloc byte[(int)length];
                if (!PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationNumaNodeEx,
                    (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)infoBytes, &length))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var numaNodes = new List<NumaNode>();
                var infoBytesEndAddress = infoBytes + length;

                while (infoBytes < infoBytesEndAddress)
                {
                    var info = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)infoBytes;

                    ref var numaNode = ref info->Anonymous.NumaNode;

                    var numaNodeProcessorGroups = new ProcessorGroup[Math.Max(numaNode.GroupCount, 1u)];
                    var numaNodeProcessorGroupsSpan = numaNode.Anonymous.GroupMasks.AsSpan(numaNodeProcessorGroups.Length);
                    for (int i = 0; i < numaNodeProcessorGroups.Length; i++)
                    {
                        var group = numaNodeProcessorGroupsSpan[i];
                        numaNodeProcessorGroups[i] = new ProcessorGroup(group.Group, group.Mask);
                    }

                    numaNodes.Add(new NumaNode(numaNode.NodeNumber, numaNodeProcessorGroups));

                    infoBytes += info->Size;
                }

                return [.. numaNodes];
            }
        }

    static ProcessorGroup[] GetProcessorGroups()
    {
        unsafe
        {
            uint length = 0;
            PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationGroup, null, &length);
            if (Marshal.GetLastWin32Error() is var lastError && lastError != (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new Win32Exception(lastError);
            }

            byte* infoBytes = stackalloc byte[(int)length];
            if (!PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationGroup,
                (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)infoBytes, &length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var info = ((SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)infoBytes);

            ref var relationGroup = ref info->Anonymous.Group;

            var processorGroups = new ProcessorGroup[relationGroup.ActiveGroupCount];

            var groupInfos = relationGroup.GroupInfo.AsSpan(relationGroup.ActiveGroupCount);

            for (ushort groupNumber = 0; groupNumber < relationGroup.ActiveGroupCount; groupNumber++)
            {
                ref var groupInfo = ref groupInfos[groupNumber];
                processorGroups[groupNumber] = new ProcessorGroup(groupNumber, groupInfo.ActiveProcessorMask);
            }

            return processorGroups;
        }
    }

    static ProcessorCore[] GetProcessorCores()
    {
        unsafe
        {
            uint length = 0;
            PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, null, &length);
            if (Marshal.GetLastWin32Error() is var lastError && lastError != (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new Win32Exception(lastError);
            }

            byte* infoBytes = stackalloc byte[(int)length];
            if (!PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore,
                (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)infoBytes, &length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var cores = new List<ProcessorCore>();
            var infoBytesEndAddress = infoBytes + length;

            while (infoBytes < infoBytesEndAddress)
            {
                var info = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)infoBytes;

                ref var processor = ref info->Anonymous.Processor;

                var group = processor.GroupMask[0];
                cores.Add(new(processor.Flags == PInvoke.LTP_PC_SMT, new(group.Group, group.Mask)));

                infoBytes += info->Size;
            }
            Debug.Assert(infoBytes == infoBytesEndAddress, $"{(nint)infoBytes} == {(nint)infoBytesEndAddress}");

            return [.. cores];
        }
    }
    }

    public static PerformanceInformation GetSystemPerformanceInformation()
    {
        unsafe
        {
            PERFORMANCE_INFORMATION pi = new() { cb = (uint)sizeof(PERFORMANCE_INFORMATION) };
            if (!PInvoke.GetPerformanceInfo(&pi, pi.cb))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            nuint pageSizeKb = pi.PageSize >> 10; // bytes to Kb
            return new PerformanceInformation(
                PhysicalTotalKB: pi.PhysicalTotal * pageSizeKb,
                PhysicalAvailableKB: pi.PhysicalAvailable * pageSizeKb,
                CommitTotalKB: pi.CommitTotal * pageSizeKb,
                CommitLimitKB: pi.CommitLimit * pageSizeKb
            );
        }
    }
}
