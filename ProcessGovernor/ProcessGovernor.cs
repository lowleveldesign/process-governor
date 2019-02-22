using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using WinDebug = VsChromium.Core.Win32.Debugging;
using WinHandles = VsChromium.Core.Win32.Handles;
using WinInterop = VsChromium.Core.Win32.Interop;
using WinJobs = LowLevelDesign.Win32.Jobs;
using WinProcesses = VsChromium.Core.Win32.Processes;

namespace LowLevelDesign
{
    public class ProcessGovernor : IDisposable
    {
        private readonly Dictionary<string, string> additionalEnvironmentVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly TraceSource logger = new TraceSource("[procgov]", SourceLevels.All);

        private ulong maxProcessMemory;
        private long cpuAffinityMask;
        private bool spawnNewConsoleWindow;
        private bool propagateOnChildProcesses;
        private uint processUserTimeLimitInMilliseconds;
        private uint jobUserTimeLimitInMilliseconds;
        private uint clockTimeLimitInMilliseconds;

        private WinProcesses.SafeProcessHandle hProcess;
        private IntPtr hIOCP, hJob;
        private Stopwatch processRunningTime = new Stopwatch();

        public ProcessGovernor()
        {
            // remove default listeners (-v to enable console traces)
            logger.Listeners.Clear();
        }

        public int AttachToProcess(int pid)
        {
            using (new DebugPrivilege(logger))
            {
                hProcess = CheckResult(WinProcesses.NativeMethods.OpenProcess(
                    WinProcesses.ProcessAccessFlags.ProcessSetQuota | WinProcesses.ProcessAccessFlags.ProcessTerminate |
                    WinProcesses.ProcessAccessFlags.QueryInformation, false, pid));
            }

            AssignProcessToJobObject();

            return WaitForTheJobToComplete();
        }

        public int StartProcess(IList<string> procargs)
        {
            var pi = new WinProcesses.PROCESS_INFORMATION();
            var si = new WinProcesses.STARTUPINFO();
            var processCreationFlags = WinProcesses.ProcessCreationFlags.CREATE_UNICODE_ENVIRONMENT | WinProcesses.ProcessCreationFlags.CREATE_SUSPENDED;
            if (spawnNewConsoleWindow) {
                processCreationFlags |= WinProcesses.ProcessCreationFlags.CREATE_NEW_CONSOLE;
            }

            CheckResult(WinProcesses.NativeMethods.CreateProcess(null, new StringBuilder(string.Join(" ", procargs)), null, null, false,
                        processCreationFlags, GetEnvironmentString(), null, si, pi));

            hProcess = new WinProcesses.SafeProcessHandle(pi.hProcess);

            AssignProcessToJobObject();

            // resume process main thread
            CheckResult(WinProcesses.NativeMethods.ResumeThread(pi.hThread));
            // and we can close the thread handle
            CloseHandle(pi.hThread);

            return WaitForTheJobToComplete();
        }

        public int StartProcessUnderDebuggerAndDetach(IList<string> procargs)
        {
            var pi = new WinProcesses.PROCESS_INFORMATION();
            var si = new WinProcesses.STARTUPINFO();
            var processCreationFlags = WinProcesses.ProcessCreationFlags.CREATE_UNICODE_ENVIRONMENT | WinProcesses.ProcessCreationFlags.DEBUG_ONLY_THIS_PROCESS;
            if (spawnNewConsoleWindow) {
                processCreationFlags |= WinProcesses.ProcessCreationFlags.CREATE_NEW_CONSOLE;
            }

            CheckResult(WinProcesses.NativeMethods.CreateProcess(null, new StringBuilder(string.Join(" ", procargs)), null, null, false,
                        processCreationFlags, GetEnvironmentString(), null, si, pi));

            hProcess = new WinProcesses.SafeProcessHandle(pi.hProcess);
            CheckResult(WinDebug.NativeMethods.DebugSetProcessKillOnExit(false));

            AssignProcessToJobObject();

            // resume process main thread by detaching from the debuggee
            CheckResult(WinDebug.NativeMethods.DebugActiveProcessStop(pi.dwProcessId));
            // and we can close the thread handle
            CloseHandle(pi.hThread);

            return WaitForTheJobToComplete();
        }

        private StringBuilder GetEnvironmentString()
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

            return envEntries;
        }

        void AssignProcessToJobObject()
        {
            var securityAttributes = new WinInterop.SecurityAttributes();
            securityAttributes.nLength = Marshal.SizeOf(securityAttributes);

            hJob = CheckResult(WinJobs.NativeMethods.CreateJobObject(securityAttributes, "procgov-" + Guid.NewGuid()));

            // create completion port
            hIOCP = CheckResult(WinJobs.NativeMethods.CreateIoCompletionPort(WinHandles.NativeMethods.INVALID_HANDLE_VALUE, IntPtr.Zero, IntPtr.Zero, 1));
            var assocInfo = new WinJobs.JOBOBJECT_ASSOCIATE_COMPLETION_PORT {
                CompletionKey = IntPtr.Zero,
                CompletionPort = hIOCP
            };
            uint size = (uint)Marshal.SizeOf(assocInfo);
            CheckResult(WinJobs.NativeMethods.SetInformationJobObject(hJob, WinJobs.JOBOBJECTINFOCLASS.AssociateCompletionPortInformation,
                    ref assocInfo, size));

            WinJobs.JobInformationLimitFlags flags = 0;
            if (!propagateOnChildProcesses)
            {
                flags |= WinJobs.JobInformationLimitFlags.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
            }
            if (maxProcessMemory > 0) {
                flags |= WinJobs.JobInformationLimitFlags.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            }
            if (cpuAffinityMask != 0) {
                flags |= WinJobs.JobInformationLimitFlags.JOB_OBJECT_LIMIT_AFFINITY;
            }
            if (processUserTimeLimitInMilliseconds > 0) {
                flags |= WinJobs.JobInformationLimitFlags.JOB_OBJECT_LIMIT_PROCESS_TIME;
            }
            if (jobUserTimeLimitInMilliseconds > 0) {
                flags |= WinJobs.JobInformationLimitFlags.JOB_OBJECT_LIMIT_JOB_TIME;
            }

            long systemAffinity, processAffinity;
            CheckResult(WinProcesses.NativeMethods.GetProcessAffinityMask(hProcess, out processAffinity, out systemAffinity));

            // configure constraints
            var limitInfo = new WinJobs.JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
                BasicLimitInformation = new WinJobs.JOBOBJECT_BASIC_LIMIT_INFORMATION {
                    LimitFlags = flags,
                    Affinity = systemAffinity & cpuAffinityMask,
                    PerProcessUserTimeLimit = 10_000 * processUserTimeLimitInMilliseconds, // in 100ns
                    PerJobUserTimeLimit = 10_000 * jobUserTimeLimitInMilliseconds // in 100ns
                },
                ProcessMemoryLimit = (UIntPtr)maxProcessMemory
            };
            size = (uint)Marshal.SizeOf(limitInfo);
            CheckResult(WinJobs.NativeMethods.SetInformationJobObject(hJob, WinJobs.JOBOBJECTINFOCLASS.ExtendedLimitInformation,
                    ref limitInfo, size));

            // assign a process to a job to apply constraints
            CheckResult(WinJobs.NativeMethods.AssignProcessToJobObject(hJob, hProcess.DangerousGetHandle()));

            // we start counting the process time running under the job 
            // only if timeout is specified
            if (clockTimeLimitInMilliseconds > 0) {
                processRunningTime.Start();
            }
        }

        int WaitForTheJobToComplete()
        {
            uint msgIdentifier;
            IntPtr pCompletionKey, lpOverlapped;
            bool shouldTerminate = false;

            while (!shouldTerminate) {
                if (!WinJobs.NativeMethods.GetQueuedCompletionStatus(hIOCP, out msgIdentifier, 
                        out pCompletionKey, out lpOverlapped, 100 /* ms */)) {
                    if (Marshal.GetLastWin32Error() == 735 /* ERROR_ABANDONED_WAIT_0 */) {
                        throw new Win32Exception();
                    }
                    // otherwise timeout
                    if (clockTimeLimitInMilliseconds > 0 &&
                        processRunningTime.ElapsedMilliseconds > clockTimeLimitInMilliseconds) {
                        logger.TraceEvent(TraceEventType.Information, 0, "Clock time limit passed - terminating.");
                        WinJobs.NativeMethods.TerminateJobObject(hJob, 1);
                        shouldTerminate = true;
                    }
                } else {
                    shouldTerminate = LogCompletionPacketAndCheckIfTerminating(msgIdentifier, lpOverlapped);
                }
            }

            // Get the exit code and return it
            int masterProcessExitCode;
            if (WinProcesses.NativeMethods.GetExitCodeProcess(hProcess, out masterProcessExitCode))
            {
                // could be STILL_ACTIVE if the process is still running
                return masterProcessExitCode;
            }
            Debug.Fail("Getting the exit code from a process was unsuccesful");
            return 1;
        }

        bool LogCompletionPacketAndCheckIfTerminating(uint msgIdentifier, IntPtr lpOverlapped)
        {
            switch (msgIdentifier) {
                case WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_NEW_PROCESS:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, 
                        "Process {0} has started", (int)lpOverlapped);
                    return false;
                case WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_EXIT_PROCESS:
                case WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, 
                        "Process {0} exited", (int)lpOverlapped);
                    return false;
                case WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, "no active processes in the job.");
                    return true; // ALWAYS EXIT - no more processes running in the job
                case WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, 
                        "Process {0} exceeded its memory limit", (int)lpOverlapped);
                    return false;
                case WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_END_OF_PROCESS_TIME:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, 
                        "Process {0} exceeded its user-mode execution limit", (int)lpOverlapped);
                    return !propagateOnChildProcesses; // EXIT when single process - we hit the process user-time limit
                case WinJobs.JobMsgInfoMessages.JOB_OBJECT_MSG_END_OF_JOB_TIME:
                    logger.TraceEvent(TraceEventType.Information, (int)msgIdentifier, 
                        "Job exceeded its user-mode execution limit");
                    return true; // ALWAYS EXIT - we hit the job user-time limit
                default:
                    Trace.TraceInformation("Unknown message: {0}", msgIdentifier);
                    return false;
            }
        }

        public ulong MaxProcessMemory { get => maxProcessMemory; set => maxProcessMemory = value; }

        public long CpuAffinityMask { get => cpuAffinityMask; set => cpuAffinityMask = value; }

        public bool SpawnNewConsoleWindow { get => spawnNewConsoleWindow; set => spawnNewConsoleWindow = value; }

        public bool PropagateOnChildProcesses { get => propagateOnChildProcesses; set => propagateOnChildProcesses = value; }

        public uint ProcessUserTimeLimitInMilliseconds {
            get => processUserTimeLimitInMilliseconds; set => processUserTimeLimitInMilliseconds = value;
        }

        public uint JobUserTimeLimitInMilliseconds {
            get => jobUserTimeLimitInMilliseconds; set => jobUserTimeLimitInMilliseconds = value;
        }

        public uint ClockTimeLimitInMilliseconds {
            get => clockTimeLimitInMilliseconds;
            set => clockTimeLimitInMilliseconds = value;
        }

        public bool ShowTraceMessages {
            set {
                if (value) {
                    logger.Listeners.Add(new ConsoleTraceListener());
                }
            }
        }

        public Dictionary<string, string> AdditionalEnvironmentVars
        {
            get { return additionalEnvironmentVars; }
        }

        public void Dispose(bool disposing)
        {
            if (disposing) {
                if (hProcess != null && !hProcess.IsClosed) {
                    hProcess.Close();
                }
            }

            CloseHandle(hIOCP);
            CloseHandle(hJob);
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

        private static void CloseHandle(IntPtr handle)
        {
            if (handle != IntPtr.Zero) {
                WinHandles.NativeMethods.CloseHandle(handle);
            }
        }

        private static void CheckResult(bool result)
        {
            if (!result) {
                throw new Win32Exception();
            }
        }

        private static IntPtr CheckResult(IntPtr result)
        {
            if (result == IntPtr.Zero) {
                throw new Win32Exception();
            }
            return result;
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
    }
}
