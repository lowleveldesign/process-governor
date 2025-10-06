using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.System.ProcessStatus;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Collections.Immutable;

namespace ProcessGovernor.Library;

public record SystemInfo(NumaNode[] NumaNodes, ProcessorGroup[] ProcessorGroups, ProcessorCore[] CpuCores);

public record ProcessorGroup(ushort Number, nuint AffinityMask);

public record NumaNode(uint Number, ProcessorGroup[] ProcessorGroups);

public record ProcessorCore(bool MultiThreaded, ProcessorGroup ProcessorGroup);

public record PerformanceInformation(nuint PhysicalTotalKB, nuint PhysicalAvailableKB, nuint CommitTotalKB, nuint CommitLimitKB);


public static class SystemInfoModule
{
    public static SystemInfo GetSystemInfo()
    {
        var processorGroups = GetProcessorGroups();
        NumaNode[] numaNodes = [];
        try
        {
            numaNodes = GetNumaNodes();
        }
        catch (Win32Exception ex)
        {
            Logger.TraceInformation("[sysinfo] NUMA information not available (is it Windows Home?). Error: {0}", ex.Message);
            numaNodes = [new(0, processorGroups)];
        }

        return new(numaNodes, processorGroups, GetProcessorCores());

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
                var info = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)infoBytes;

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

    public static ImmutableArray<GroupAffinity> ParseCpuAffinity(SystemInfo systemInfo, IEnumerable<string> cpuAffinityArgs)
    {
        Debug.Assert(systemInfo.NumaNodes.Length > 0 &&
            systemInfo.NumaNodes[0].ProcessorGroups.Length > 0);
        var defaultProcessorGroup = systemInfo.NumaNodes[0].ProcessorGroups[0];

        Dictionary<ushort, nuint> affinities = [];

        foreach (var cpuAffinityArg in cpuAffinityArgs)
        {
            switch (cpuAffinityArg.Split(':'))
            {
                case [string aff] when TryFindProcessorGroup(aff, out var processorGroup):
                    affinities[processorGroup.Number] = processorGroup.AffinityMask;
                    break;
                case [string aff] when TryFindNumaNode(aff, out var numaNode):
                    foreach (var processorGroup in numaNode.ProcessorGroups)
                    {
                        UpdateGroupAffinity(processorGroup.Number, processorGroup.AffinityMask);
                    }
                    break;
                case [string aff] when TryParseAffinity(aff, out var groupAffinity):
                    UpdateGroupAffinity(defaultProcessorGroup.Number, groupAffinity);
                    break;
                case [string numa, string group] when TryFindNumaNode(numa, out var numaNode)
                    && TryFindProcessorGroup(group, out var processorGroup):
                    if (numaNode.ProcessorGroups.FirstOrDefault(g => g.Number == processorGroup.Number) is { } numaProcessorGroup)
                    {
                        UpdateGroupAffinity(processorGroup.Number, numaProcessorGroup.AffinityMask);
                    }
                    else
                    {
                        throw new ArgumentException(
                            $"processor group {processorGroup.Number} does not belong to NUMA node {numaNode.Number}");
                    }
                    break;
                case [string group, string aff] when TryFindProcessorGroup(group, out var processorGroup)
                    && TryParseAffinity(aff, out var cpuAffinity):
                    UpdateGroupAffinity(processorGroup.Number, processorGroup.AffinityMask & cpuAffinity);
                    break;
                case [string numa, string group, string aff] when TryFindNumaNode(numa, out var numaNode)
                    && TryFindProcessorGroup(group, out var processorGroup) && TryParseAffinity(aff, out var cpuAffinity):
                    if (!numaNode.ProcessorGroups.Any(g => g.Number == processorGroup.Number))
                    {
                        throw new ArgumentException(
                            $"processor group {processorGroup.Number} does not belong to NUMA node {numaNode.Number}");
                    }
                    UpdateGroupAffinity(processorGroup.Number, processorGroup.AffinityMask & cpuAffinity);
                    break;
                default:
                    throw new ArgumentException($"invalid affinity string: '{cpuAffinityArg}'");
            }
        }

        return [.. affinities.Select(kv => new GroupAffinity(kv.Key, kv.Value))];


        void UpdateGroupAffinity(ushort groupNumber, nuint affinity)
        {
            if (affinities.TryGetValue(groupNumber, out var savedAffinity))
            {
                affinities[groupNumber] = savedAffinity | affinity;
            }
            else
            {
                affinities.Add(groupNumber, affinity);
            }
        }

        bool TryFindNumaNode(string numaArg, [MaybeNullWhen(false)] out NumaNode numa)
        {
            if (numaArg.Length > 0 && (numaArg[0] == 'n' || numaArg[0] == 'N'))
            {
                var number = uint.Parse(numaArg.AsSpan(1));
                if (systemInfo.NumaNodes.FirstOrDefault(n => n.Number == number) is { } n)
                {
                    numa = n;
                    return true;
                }
            }
            numa = null;
            return false;
        }

        bool TryFindProcessorGroup(string groupArg, [MaybeNullWhen(false)] out ProcessorGroup group)
        {
            if (groupArg.Length > 0 && (groupArg[0] == 'g' || groupArg[0] == 'G'))
            {
                var number = ushort.Parse(groupArg.AsSpan(1));
                if (systemInfo.ProcessorGroups.FirstOrDefault(g => g.Number == number) is { } g)
                {
                    group = g;
                    return true;
                }
            }
            group = null;
            return false;
        }

        static bool TryParseAffinity(string s, out nuint affinity)
        {
            return nuint.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ?
                s[2..] : s, NumberStyles.AllowHexSpecifier, null, out affinity);
        }
    }
}
