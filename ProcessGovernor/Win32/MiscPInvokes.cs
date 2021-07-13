using System;
using System.Runtime.InteropServices;
using Windows.Win32.System.Kernel;
using Windows.Win32.System.SystemServices;

namespace LowLevelDesign.Win32
{
    public class JobMsgInfoMessages
    {
        public const uint JOB_OBJECT_MSG_END_OF_JOB_TIME = 1;
        public const uint JOB_OBJECT_MSG_END_OF_PROCESS_TIME = 2;
        public const uint JOB_OBJECT_MSG_ACTIVE_PROCESS_LIMIT = 3;
        public const uint JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO = 4;
        public const uint JOB_OBJECT_MSG_NEW_PROCESS = 6;
        public const uint JOB_OBJECT_MSG_EXIT_PROCESS = 7;
        public const uint JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS = 8;
        public const uint JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT = 9;
        public const uint JOB_OBJECT_MSG_JOB_MEMORY_LIMIT = 10;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT
    {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }

    [Flags]
    public enum JobInformationLimitFlags
    {
        JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008,
        JOB_OBJECT_LIMIT_AFFINITY = 0x00000010,
        JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800,
        JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400,
        JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200,
        JOB_OBJECT_LIMIT_JOB_TIME = 0x00000004,
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000,
        JOB_OBJECT_LIMIT_PRESERVE_JOB_TIME = 0x00000040,
        JOB_OBJECT_LIMIT_PRIORITY_CLASS = 0x00000020,
        JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100,
        JOB_OBJECT_LIMIT_PROCESS_TIME = 0x00000002,
        JOB_OBJECT_LIMIT_SCHEDULING_CLASS = 0x00000080,
        JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000,
        JOB_OBJECT_LIMIT_WORKINGSET = 0x00000001
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public Int64 PerProcessUserTimeLimit;
        public Int64 PerJobUserTimeLimit;
        public JobInformationLimitFlags LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public UInt32 ActiveProcessLimit;
        public UIntPtr Affinity;
        public UInt32 PriorityClass;
        public UInt32 SchedulingClass;
    }


    public struct IO_COUNTERS
    {
        public UInt64 ReadOperationCount;
        public UInt64 WriteOperationCount;
        public UInt64 OtherOperationCount;
        public UInt64 ReadTransferCount;
        public UInt64 WriteTransferCount;
        public UInt64 OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [Flags]
    public enum JOBOBJECT_CPU_RATE_CONTROL_FLAGS : uint
    {
        JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1,
        JOB_OBJECT_CPU_RATE_CONTROL_WEIGHT_BASED = 0x2,
        JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4,
        JOB_OBJECT_CPU_RATE_CONTROL_NOTIFY = 0x8,
        JOB_OBJECT_CPU_RATE_CONTROL_MIN_MAX_RATE = 0x10,
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        [FieldOffset(0)]
        public JOBOBJECT_CPU_RATE_CONTROL_FLAGS ControlFlags;
        [FieldOffset(4)]
        public uint CpuRate;
        [FieldOffset(4)]
        public uint Weight;
        [FieldOffset(4)]
        public ushort MinRate;
        [FieldOffset(6)]
        public ushort MaxRate;
    }

    [Flags]
    public enum JOB_OBJECT_NET_RATE_CONTROL_FLAGS
    {
        JOB_OBJECT_NET_RATE_CONTROL_ENABLE = 0x1,
        JOB_OBJECT_NET_RATE_CONTROL_MAX_BANDWIDTH = 0x2,
        JOB_OBJECT_NET_RATE_CONTROL_DSCP_TAG = 0x4,
        JOB_OBJECT_NET_RATE_CONTROL_VALID_FLAGS = 0x7
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_NET_RATE_CONTROL_INFORMATION
    {
        public UInt64 MaxBandwidth;
        public JOB_OBJECT_NET_RATE_CONTROL_FLAGS ControlFlags;
        public byte DscpTag;
    }


    internal static class Jobs
    {
        public const byte JOB_OBJECT_NET_RATE_CONTROL_MAX_DSCP_TAG = 64;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS JobObjectInfoClass,
                ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS JobObjectInfoClass,
                ref JOBOBJECT_ASSOCIATE_COMPLETION_PORT lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS JobObjectInfoClass,
                ref GROUP_AFFINITY lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS JobObjectInfoClass,
                ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS JobObjectInfoClass,
                ref JOBOBJECT_NET_RATE_CONTROL_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);
    }
}
