using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Kernel;
using Windows.Win32.System.Threading;
using Windows.Win32.System.SystemServices;
using Microsoft.Win32.SafeHandles;
using LowLevelDesign.Win32;

namespace LowLevelDesign
{
    public class ProcessGovernor : IDisposable
    {
        private static readonly SafeHandle invalidHandle = new SafeFileHandle(new IntPtr(-1), false);
        private static readonly SafeHandle zeroHandle = new SafeFileHandle(IntPtr.Zero, false);

        private readonly Dictionary<string, string> additionalEnvironmentVars =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly TraceSource logger = new TraceSource("[procgov]", SourceLevels.All);
        private readonly Stopwatch processRunningTime = new Stopwatch();

        private ushort numaNode = 0xffff;

        private SafeHandle hProcess;
        private SafeHandle hIOCP = invalidHandle;
        private SafeHandle hJob = invalidHandle;

        public ProcessGovernor()
        {
            // remove default listeners (-v to enable console traces)
            logger.Listeners.Clear();
        }

        public uint AttachToProcess(uint pid)
        {
            using (new DebugPrivilege(logger)) {
                hProcess = CheckResult(PInvoke.OpenProcess_SafeHandle(
                    PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION |
                    PROCESS_ACCESS_RIGHTS.PROCESS_SET_QUOTA |
                    PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE, false, pid));
            }

            AssignProcessToJobObject();

            return WaitForTheJobToComplete();
        }

        public unsafe uint StartProcess(IList<string> procargs)
        {
            var pi = new PROCESS_INFORMATION();
            var si = new STARTUPINFOW();
            var processCreationFlags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT |
                                       PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;
            if (SpawnNewConsoleWindow) {
                processCreationFlags |= PROCESS_CREATION_FLAGS.CREATE_NEW_CONSOLE;
            }

            var args = string.Join(" ", procargs);
            var env = GetEnvironmentString();
            fixed (char* pargs = args) {
                fixed(char* penv = env) {
                    CheckResult(PInvoke.CreateProcess(null, pargs, null, null, false,
                        processCreationFlags, penv, null, si, out pi));
                }
            }

            hProcess = new SafeFileHandle(pi.hProcess, true);

            AssignProcessToJobObject();

            // resume process main thread
            CheckResult(PInvoke.ResumeThread(pi.hThread));
            // and we can close the thread handle
            PInvoke.CloseHandle(pi.hThread);

            return WaitForTheJobToComplete();
        }

        public unsafe uint StartProcessUnderDebuggerAndDetach(IList<string> procargs)
        {
            var pi = new PROCESS_INFORMATION();
            var si = new STARTUPINFOW();
            var processCreationFlags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT |
                                       PROCESS_CREATION_FLAGS.DEBUG_ONLY_THIS_PROCESS;
            if (SpawnNewConsoleWindow) {
                processCreationFlags |= PROCESS_CREATION_FLAGS.CREATE_NEW_CONSOLE;
            }

            var args = string.Join(" ", procargs);
            var env = GetEnvironmentString();
            fixed (char* pargs = args)
            {
                fixed (char* penv = env)
                {
                    CheckResult(PInvoke.CreateProcess(null, pargs, null, null, false,
                        processCreationFlags, penv, null, si, out pi));
                }
            }

            hProcess = new SafeFileHandle(pi.hProcess, true);
            CheckResult(PInvoke.DebugSetProcessKillOnExit(false));

            AssignProcessToJobObject();

            // resume process main thread by detaching from the debuggee
            CheckResult(PInvoke.DebugActiveProcessStop(pi.dwProcessId));
            // and we can close the thread handle
            PInvoke.CloseHandle(pi.hThread);

            return WaitForTheJobToComplete();
        }

        private string GetEnvironmentString()
        {
            if (additionalEnvironmentVars.Count == 0) {
                return null;
            }

            StringBuilder envEntries = new StringBuilder();
            foreach (string env in Environment.GetEnvironmentVariables().Keys) {
                if (additionalEnvironmentVars.ContainsKey(env)) {
                    continue; // overwrite existing env
                }

                envEntries.Append(env).Append("=").Append(
                    Environment.GetEnvironmentVariable(env)).Append("\0");
            }

            foreach (var kv in additionalEnvironmentVars) {
                envEntries.Append(kv.Key).Append("=").Append(
                    kv.Value).Append("\0");
            }

            envEntries.Append("\0");

            return envEntries.ToString();
        }

        void AssignProcessToJobObject()
        {
            var securityAttributes = new SECURITY_ATTRIBUTES();
            securityAttributes.nLength = (uint)Marshal.SizeOf(securityAttributes);

            hJob = CheckResult(PInvoke.CreateJobObject(securityAttributes, "procgov-" + Guid.NewGuid()));

            // create completion port
            hIOCP = CheckResult(PInvoke.CreateIoCompletionPort(invalidHandle, zeroHandle, UIntPtr.Zero, 1));
            var assocInfo = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT {
                CompletionKey = IntPtr.Zero,
                CompletionPort = hIOCP.DangerousGetHandle()
            };
            uint size = (uint)Marshal.SizeOf(assocInfo);
            CheckResult(Jobs.SetInformationJobObject(hJob.DangerousGetHandle(),
                JOBOBJECTINFOCLASS.JobObjectAssociateCompletionPortInformation,
                ref assocInfo, size));

            JobInformationLimitFlags flags = 0;
            if (!PropagateOnChildProcesses) {
                flags |= JobInformationLimitFlags.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
            }

            if (MaxProcessMemory > 0) {
                flags |= JobInformationLimitFlags.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            }
            if (MaxWorkingSetSize > 0) {
                flags |= JobInformationLimitFlags.JOB_OBJECT_LIMIT_WORKINGSET;
            }

            if (ProcessUserTimeLimitInMilliseconds > 0) {
                flags |= JobInformationLimitFlags.JOB_OBJECT_LIMIT_PROCESS_TIME;
            }

            if (JobUserTimeLimitInMilliseconds > 0) {
                flags |= JobInformationLimitFlags.JOB_OBJECT_LIMIT_JOB_TIME;
            }

            // only if NUMA node is not provided we will set the CPU affinity,
            // otherwise we will set the affinity on a selected NUMA node
            if (CpuAffinityMask != 0 && numaNode == 0xffff) {
                flags |= JobInformationLimitFlags.JOB_OBJECT_LIMIT_AFFINITY;
            }

            CheckResult(PInvoke.GetProcessAffinityMask(hProcess, out _, out var systemAffinityMask));

            if (flags != 0) {
                // configure basic constraints
                var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION {
                        LimitFlags = flags,
                        // NOTE: this could result in an overflow on 32-bit apps, but I can't
                        // think of a nice way of handling it here
                        Affinity = new UIntPtr((ulong)systemAffinityMask & CpuAffinityMask),
                        PerProcessUserTimeLimit = 10_000 * ProcessUserTimeLimitInMilliseconds, // in 100ns
                        PerJobUserTimeLimit = 10_000 * JobUserTimeLimitInMilliseconds, // in 100ns
                        MaximumWorkingSetSize = (UIntPtr)MaxWorkingSetSize,
                        MinimumWorkingSetSize = (UIntPtr)MinWorkingSetSize
                    },
                    ProcessMemoryLimit = (UIntPtr)MaxProcessMemory
                };
                size = (uint)Marshal.SizeOf(limitInfo);
                CheckResult(Jobs.SetInformationJobObject(hJob.DangerousGetHandle(),
                    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    ref limitInfo, size));
            }

            if (CpuMaxRate > 0)
            {
                // Set CpuRate to a percentage times 100. For example, to let the job use 20% of the CPU, 
                // set CpuRate to 20 times 100, or 2,000.
                uint finalCpuRate = CpuMaxRate * 100;

                // CPU rate is set for the whole system so includes all the logical CPUs. When 
                // we have the CPU affinity set, we will divide the rate accordingly.
                if (CpuAffinityMask != 0)
                {
                    ulong affinity = (ulong)systemAffinityMask & CpuAffinityMask;
                    uint numberOfSelectedCores = 0;
                    for (int i = 0; i < sizeof(ulong) * 8; i++)
                    {
                        numberOfSelectedCores += (affinity & (1UL << i)) == 0 ? 0u : 1u;
                    }
                    Debug.Assert(numberOfSelectedCores < Environment.ProcessorCount);
                    finalCpuRate /= ((uint)Environment.ProcessorCount / numberOfSelectedCores);
                }

                // configure CPU rate limit
                var limitInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION {
                    ControlFlags = JOBOBJECT_CPU_RATE_CONTROL_FLAGS.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                        JOBOBJECT_CPU_RATE_CONTROL_FLAGS.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,

                    CpuRate = finalCpuRate
                };
                size = (uint)Marshal.SizeOf(limitInfo);
                CheckResult(Jobs.SetInformationJobObject(hJob.DangerousGetHandle(),
                    JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation,
                    ref limitInfo, size));
            }

            if (MaxBandwidth > 0)
            {
                var limitInfo = new JOBOBJECT_NET_RATE_CONTROL_INFORMATION {
                    ControlFlags = JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_ENABLE |
                                    JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_MAX_BANDWIDTH,
                    MaxBandwidth = MaxBandwidth
                };
                size = (uint)Marshal.SizeOf(limitInfo);
                CheckResult(Jobs.SetInformationJobObject(hJob.DangerousGetHandle(),
                    JOBOBJECTINFOCLASS.JobObjectNetRateControlInformation,
                    ref limitInfo, size));
            }

            if (numaNode != 0xffff) {
                var affinity = new GROUP_AFFINITY();
                CheckResult(PInvoke.GetNumaNodeProcessorMaskEx(numaNode, out affinity));

                var nodeAffinityMask = affinity.Mask.ToUInt64();
                if (CpuAffinityMask != 0) {
                    // When CPU affinity is set, we can't simply use 
                    // NUMA affinity, but rather need to apply the CPU affinity
                    // settings to the select NUMA node.
                    var firstNonZeroBitPosition = 0;
                    while ((nodeAffinityMask & (1UL << firstNonZeroBitPosition)) == 0) {
                        firstNonZeroBitPosition++;
                    }
                    CpuAffinityMask <<= firstNonZeroBitPosition & 0x3f;
                } else {
                    CpuAffinityMask = nodeAffinityMask;
                }
                // NOTE: this could result in an overflow on 32-bit apps, but I can't
                // think of a nice way of handling it here
                affinity.Mask = new UIntPtr(CpuAffinityMask & (ulong)systemAffinityMask);

                size = (uint)Marshal.SizeOf(affinity);
                CheckResult(Jobs.SetInformationJobObject(hJob.DangerousGetHandle(),
                    JOBOBJECTINFOCLASS.JobObjectGroupInformationEx,
                    ref affinity, size));
            }

            // assign a process to a job to apply constraints
            CheckResult(PInvoke.AssignProcessToJobObject(hJob, hProcess));

            // we start counting the process time running under the job 
            // only if timeout is specified
            if (ClockTimeLimitInMilliseconds > 0) {
                processRunningTime.Start();
            }
        }

        unsafe uint WaitForTheJobToComplete()
        {
            var shouldTerminate = false;

            while (!shouldTerminate) {
                if (!PInvoke.GetQueuedCompletionStatus(hIOCP, out var msgIdentifier,
                    out _, out var lpOverlapped, 100 /* ms */)) {
                    if (Marshal.GetLastWin32Error() == 735 /* ERROR_ABANDONED_WAIT_0 */) {
                        throw new Win32Exception();
                    }

                    // otherwise timeout
                    if (ClockTimeLimitInMilliseconds > 0 &&
                        processRunningTime.ElapsedMilliseconds > ClockTimeLimitInMilliseconds) {
                        logger.TraceEvent(TraceEventType.Information, 0, "Clock time limit passed - terminating.");
                        PInvoke.TerminateJobObject(hJob, 1);
                        shouldTerminate = true;
                    }
                } else {
                    shouldTerminate = LogCompletionPacketAndCheckIfTerminating(msgIdentifier, (IntPtr)lpOverlapped);
                }
            }

            // Get the exit code and return it
            if (PInvoke.GetExitCodeProcess(hProcess, out var masterProcessExitCode)) {
                // could be STILL_ACTIVE if the process is still running
                return masterProcessExitCode;
            }

            Debug.Fail("Getting the exit code from a process was unsuccessful");
            return 1;
        }

        bool LogCompletionPacketAndCheckIfTerminating(uint msgIdentifier, IntPtr lpOverlapped)
        {
            switch (msgIdentifier) {
                case JobMsgInfoMessages.JOB_OBJECT_MSG_NEW_PROCESS:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier,
                        "Process {0} has started", (int)lpOverlapped);
                    return false;
                case JobMsgInfoMessages.JOB_OBJECT_MSG_EXIT_PROCESS:
                case JobMsgInfoMessages.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier,
                        "Process {0} exited", (int)lpOverlapped);
                    return false;
                case JobMsgInfoMessages.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier,
                        "no active processes in the job.");
                    return true; // ALWAYS EXIT - no more processes running in the job
                case JobMsgInfoMessages.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier,
                        "Process {0} exceeded its memory limit", (int)lpOverlapped);
                    return false;
                case JobMsgInfoMessages.JOB_OBJECT_MSG_END_OF_PROCESS_TIME:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier,
                        "Process {0} exceeded its user-mode execution limit", (int)lpOverlapped);
                    return !PropagateOnChildProcesses; // EXIT when single process - we hit the process user-time limit
                case JobMsgInfoMessages.JOB_OBJECT_MSG_END_OF_JOB_TIME:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier,
                        "Job exceeded its user-mode execution limit");
                    return true; // ALWAYS EXIT - we hit the job user-time limit
                default:
                    Trace.TraceInformation("Unknown message: {0}", msgIdentifier);
                    return false;
            }
        }

        public ulong MaxProcessMemory { get; set; }
        
        public ulong MaxWorkingSetSize { get; set; }

        public ulong MinWorkingSetSize { get; set; }

        public ushort NumaNode {
            get => numaNode;
            set {
                CheckResult(PInvoke.GetNumaHighestNodeNumber(out var highestNodeNumber));
                if (value > highestNodeNumber) {
                    throw new ArgumentException($"Incorrect NUMA node number. The highest accepted number is {highestNodeNumber}.");
                }
                numaNode = value;
            }
        }

        public ulong CpuAffinityMask { get; set; }

        public uint CpuMaxRate { get; set; }

        public ulong MaxBandwidth { get; set; }

        public bool SpawnNewConsoleWindow { get; set; }

        public bool PropagateOnChildProcesses { get; set; }

        public uint ProcessUserTimeLimitInMilliseconds { get; set; }

        public uint JobUserTimeLimitInMilliseconds { get; set; }

        public uint ClockTimeLimitInMilliseconds { get; set; }

        public bool ShowTraceMessages {
            set {
                if (value) {
                    logger.Listeners.Add(new ConsoleTraceListener());
                }
            }
        }

        public Dictionary<string, string> AdditionalEnvironmentVars {
            get { return additionalEnvironmentVars; }
        }

        public void Dispose(bool disposing)
        {
            if (disposing) {
                if (hProcess != null && !hProcess.IsClosed) {
                    hProcess.Close();
                }
            }

            hIOCP.Dispose();
            hJob.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ProcessGovernor()
        {
            Dispose(false);
        }

        /* Win32 API helper methods */

        private static void CheckResult(bool result)
        {
            if (!result) {
                throw new Win32Exception();
            }
        }

        private static void CheckResult(HANDLE result)
        {
            if (result.IsNull) {
                throw new Win32Exception();
            }
        }

        private static T CheckResult<T>(T handle) where T : SafeHandle
        {
            if (handle.IsInvalid) {
                throw new Win32Exception();
            }

            return handle;
        }

        private static void CheckResult(int result)
        {
            if (result == -1) {
                throw new Win32Exception();
            }
        }

        private static void CheckResult(uint result)
        {
            if (result == 0xffffffff) {
                throw new Win32Exception();
            }
        }
    }
}