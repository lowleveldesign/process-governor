using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.SystemServices;
using Windows.Win32.System.Threading;
using Windows.Win32.Storage.FileSystem;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using static LowLevelDesign.Win32Commons;
using static Windows.Win32.Constants;
using System.Threading;

namespace LowLevelDesign
{
    internal static class Win32JobModule
    {
        private static readonly TraceSource logger = Program.Logger;

        public static unsafe Win32Job CreateAndAssignToProcess(SafeFileHandle hProcess, uint processId,
            bool propagateOnChildProcesses, long clockTimeLimitInMilliseconds)
        {
            var securityAttributes = new SECURITY_ATTRIBUTES();
            securityAttributes.nLength = (uint)Marshal.SizeOf(securityAttributes);

            var hJob = CheckWin32Result(PInvoke.CreateJobObject(securityAttributes, $"Local\\procgov-{processId}"));

            var hCurrentProcess = PInvoke.GetCurrentProcess();
            HANDLE hRemoteJob;
            CheckWin32Result(PInvoke.DuplicateHandle(hCurrentProcess, (HANDLE)hJob.DangerousGetHandle(), (HANDLE)hProcess.DangerousGetHandle(),
                &hRemoteJob, 0, false, DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS));
            Console.WriteLine("Remote handle: 0x{0:X}", (IntPtr)hRemoteJob);

            // assign a process to a job to apply constraints
            CheckWin32Result(PInvoke.AssignProcessToJobObject(hJob, hProcess));

            return new Win32Job(hJob, hProcess, propagateOnChildProcesses, clockTimeLimitInMilliseconds);
        }

        public static unsafe bool TryOpen(SafeHandle hProcess, uint processId, bool propagateOnChildProcesses,
            long clockTimeLimitInMilliseconds, out Win32Job job)
        {
            var hJob = PInvoke.OpenJobObject(JOB_OBJECT_QUERY | JOB_OBJECT_SET_ATTRIBUTES | JOB_OBJECT_TERMINATE | (uint)FILE_ACCESS_FLAGS.SYNCHRONIZE,
                            false, $"Local\\procgov-{processId}");
            if (hJob.IsInvalid)
            {
                job = null;
                return false;
            }
            job = new Win32Job(hJob, hProcess, propagateOnChildProcesses, clockTimeLimitInMilliseconds);

            return true;
        }

        public static void SetLimits(Win32Job job, SessionSettings session)
        {
            if (session.NumaNode != 0xffff)
            {
                CheckWin32Result(PInvoke.GetNumaHighestNodeNumber(out var highestNodeNumber));
                if (session.NumaNode > highestNodeNumber)
                {
                    throw new ArgumentException($"Incorrect NUMA node number. The highest accepted number is {highestNodeNumber}.");
                }
            }

            CheckWin32Result(PInvoke.GetProcessAffinityMask(job.ProcessHandle, out _, out var aff));
            var systemAffinityMask = (ulong)aff;

            SetBasicLimits(job, session, systemAffinityMask);
        }

        private static unsafe void SetBasicLimits(Win32Job job, SessionSettings session, ulong systemAffinityMask)
        {
            JOB_OBJECT_LIMIT flags = 0;
            if (!session.PropagateOnChildProcesses)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
            }

            if (session.MaxProcessMemory > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            }
            if (session.MaxWorkingSetSize > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_WORKINGSET;
            }

            if (session.ProcessUserTimeLimitInMilliseconds > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_TIME;
            }

            if (session.JobUserTimeLimitInMilliseconds > 0)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_TIME;
            }

            // only if NUMA node is not provided we will set the CPU affinity,
            // otherwise we will set the affinity on a selected NUMA node
            if (session.CpuAffinityMask != 0 && session.NumaNode == 0xffff)
            {
                flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_AFFINITY;
            }

            if (flags != 0)
            {
                // configure basic constraints
                var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION {
                        LimitFlags = flags,
                        // NOTE: this could result in an overflow on 32-bit apps, but I can't
                        // think of a nice way of handling it here
                        Affinity = new UIntPtr(systemAffinityMask & session.CpuAffinityMask),
                        PerProcessUserTimeLimit = 10_000 * session.ProcessUserTimeLimitInMilliseconds, // in 100ns
                        PerJobUserTimeLimit = 10_000 * session.JobUserTimeLimitInMilliseconds, // in 100ns
                        MaximumWorkingSetSize = (UIntPtr)session.MaxWorkingSetSize,
                        MinimumWorkingSetSize = (UIntPtr)session.MinWorkingSetSize
                    },
                    ProcessMemoryLimit = (UIntPtr)session.MaxProcessMemory
                };
                var size = (uint)Marshal.SizeOf(limitInfo);
                CheckWin32Result(PInvoke.SetInformationJobObject(job.JobHandle,
                    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limitInfo, size));
            }
        }

        public static unsafe int WaitForTheJobToComplete(Win32Job job, CancellationToken ct)
        {
            var shouldTerminate = false;

            while (!shouldTerminate && !ct.IsCancellationRequested)
            {
                switch (PInvoke.WaitForSingleObject(job.JobHandle, 200 /* ms */))
                {
                    case WAIT_RETURN_CAUSE.WAIT_OBJECT_0:
                        logger.TraceEvent(TraceEventType.Information, 0, "End of job time limit passed - terminating.");
                        shouldTerminate = true;
                        break;
                    case WAIT_RETURN_CAUSE.WAIT_FAILED:
                        throw new Win32Exception();
                    default:
                        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION jobBasicAcctInfo;
                        uint length;
                        CheckWin32Result(PInvoke.QueryInformationJobObject(job.JobHandle, JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                            &jobBasicAcctInfo, (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), &length));
                        Debug.Assert((uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() == length);

                        if (jobBasicAcctInfo.ActiveProcesses == 0)
                        {
                            logger.TraceEvent(TraceEventType.Information, 0, "No active processes in the job - terminating.");
                            shouldTerminate = true;
                        } else if (job.IsTimedOut)
                        {
                            logger.TraceEvent(TraceEventType.Information, 0, "Clock time limit passed - terminating.");
                            PInvoke.TerminateJobObject(job.JobHandle, 1);
                            shouldTerminate = true;
                        }
                        break;
                }
            }

            // Get the exit code and return it
            if (PInvoke.GetExitCodeProcess(job.ProcessHandle, out var masterProcessExitCode))
            {
                // could be STILL_ACTIVE if the process is still running
                return (int)masterProcessExitCode;
            }

            Debug.Fail("Getting the exit code from a process was unsuccessful");
            return 1;
        }
    }
}
