using LowLevelDesign.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LowLevelDesign
{
    public class ProcessGovernor : IDisposable
    {
        private uint maxProcessMemory;
        private long cpuAffinityMask;
        private readonly Dictionary<string, string> additionalEnvironmentVars = new Dictionary<string, string>();
        private Thread listener;

        private IntPtr hProcess, hIOCP, hJob;

        public void AttachToProcess(int pid)
        {
            hProcess = CheckResult(ApiMethods.OpenProcess(ProcessAccessFlags.AllAccess, false, pid));

            AssignProcessToJobObject();

            WaitForTheChildProcess();
        }

        public void StartProcess(IList<string> procargs)
        {
            PROCESS_INFORMATION pi;
            STARTUPINFO si = new STARTUPINFO();

            CheckResult(ApiMethods.CreateProcess(null, String.Join(" ", procargs), IntPtr.Zero, IntPtr.Zero, false,
                        CreateProcessFlags.CREATE_SUSPENDED | CreateProcessFlags.CREATE_NEW_CONSOLE,
                        GetEnvironmentString(), null, ref si, out pi));

            hProcess = pi.hProcess;

            AssignProcessToJobObject();

            // resume process main thread
            CheckResult(ApiMethods.ResumeThread(pi.hThread));
            // and we can close the thread handle
            CloseHandle(pi.hThread);

            WaitForTheChildProcess();
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
                var securityAttributes = new SECURITY_ATTRIBUTES();
                securityAttributes.nLength = Marshal.SizeOf(securityAttributes);

                hJob = CheckResult(ApiMethods.CreateJobObject(ref securityAttributes, "procgov-" + Guid.NewGuid()));

                // create completion port
                hIOCP = CheckResult(ApiMethods.CreateIoCompletionPort(ApiMethods.INVALID_HANDLE_VALUE, IntPtr.Zero, IntPtr.Zero, 1));
                var assocInfo = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT {
                    CompletionKey = IntPtr.Zero,
                    CompletionPort = hIOCP
                };
                uint size = (uint)Marshal.SizeOf(assocInfo);
                CheckResult(ApiMethods.SetInformationJobObject(hJob, JOBOBJECTINFOCLASS.AssociateCompletionPortInformation,
                        ref assocInfo, size));

                // start listening thread
                listener = new Thread(CompletionPortListener);
                listener.Start(hIOCP);

                JobInformationLimitFlags flags = JobInformationLimitFlags.JOB_OBJECT_LIMIT_BREAKAWAY_OK
                                        | JobInformationLimitFlags.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
                if (maxProcessMemory > 0) {
                    flags |= JobInformationLimitFlags.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
                }
                if (cpuAffinityMask != 0) {
                    flags |= JobInformationLimitFlags.JOB_OBJECT_LIMIT_AFFINITY;
                }

                long systemAffinity, processAffinity;
                CheckResult(ApiMethods.GetProcessAffinityMask(hProcess, out processAffinity, out systemAffinity));

                // configure constraints
                var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION {
                        LimitFlags = flags,
                        Affinity = systemAffinity & cpuAffinityMask
                    },
                    ProcessMemoryLimit = maxProcessMemory
                };
                size = (uint)Marshal.SizeOf(limitInfo);
                CheckResult(ApiMethods.SetInformationJobObject(hJob, JOBOBJECTINFOCLASS.ExtendedLimitInformation,
                        ref limitInfo, size));

                // assign a process to a job to apply constraints
                CheckResult(ApiMethods.AssignProcessToJobObject(hJob, hProcess));
        }

        void WaitForTheChildProcess()
        {

            if (ApiMethods.WaitForSingleObject(hProcess, ApiMethods.INFINITE) == 0xFFFFFFFF) {
                throw new Win32Exception();
            }

            if (hIOCP != IntPtr.Zero) {
                CloseHandle(hIOCP);
            }
            if (listener.IsAlive) {
                if (!listener.Join(TimeSpan.FromMilliseconds(500))) {
                    listener.Abort();
                }
            }
        }

        void CompletionPortListener(object o)
        {
            uint msgIdentifier;
            IntPtr pCompletionKey, lpOverlapped;

            while (ApiMethods.GetQueuedCompletionStatus(hIOCP, out msgIdentifier, out pCompletionKey,
                        out lpOverlapped, ApiMethods.INFINITE)) {
                if (msgIdentifier == (uint)JobMsgInfoMessages.JOB_OBJECT_MSG_NEW_PROCESS) {
                    Trace.TraceInformation("{0}: process {1} has started", msgIdentifier, (int)lpOverlapped);
                } else if (msgIdentifier == (uint)JobMsgInfoMessages.JOB_OBJECT_MSG_EXIT_PROCESS) {
                    Trace.TraceInformation("{0}: process {1} exited", msgIdentifier, (int)lpOverlapped);
                } else if (msgIdentifier == (uint)JobMsgInfoMessages.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO) {
                    // nothing
                } else if (msgIdentifier == (uint)JobMsgInfoMessages.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT) {
                    Trace.TraceInformation("{0}: process {1} exceeded its memory limit", msgIdentifier, (int)lpOverlapped);
                } else {
                    Trace.TraceInformation("Unknown message: {0}", msgIdentifier);
                }
            }
        }

        public uint MaxProcessMemory
        {
            get { return maxProcessMemory; }
            set { maxProcessMemory = value; }
        }

        public long CpuAffinityMask
        {
            get { return cpuAffinityMask; }
            set { cpuAffinityMask = value; }
        }

        public void AddEnvironmentVariable(string varname, string varvalue)
        {
            Debug.Assert(!string.IsNullOrEmpty(varname));
            additionalEnvironmentVars.Add(varname, varvalue);
        }

        public void Dispose(bool disposing)
        {
            if (hProcess != IntPtr.Zero) {
                CloseHandle(hProcess);
            }
            if (hJob != IntPtr.Zero) {
                CloseHandle(hJob);
            }
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
                ApiMethods.CloseHandle(handle);
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

        private static void CheckResult(int result)
        {
            if (result == -1) {
                throw new Win32Exception();
            }
        }
    }
}
