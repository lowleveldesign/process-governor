using System.Diagnostics;
using Windows.Win32;
using System.Runtime.InteropServices;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

sealed class Win32Process(SafeHandle processHandle, SafeHandle mainThreadHandle, uint processId) : IDisposable
{
    public SafeHandle Handle => processHandle;

    public uint Id => processId;

    public SafeHandle MainThreadHandle => mainThreadHandle;

    public void Dispose()
    {
        processHandle.Dispose();
    }
}

static class ProcessModule
{
    private static readonly TraceSource logger = Program.Logger;

    /*

    public static Win32Job AssignProcessesToJobObject(int[] pids, JobSettings session)
    {
        static bool TryOpenProcGovJob(SafeHandle processHandle, out SafeHandle jobHandle, out string jobName)
        {
            bool found;
            if (GetProcessEnvironmentVariable(processHandle, JobNameEnvironmentVariable) is string name)
            {
                if (Win32JobModule.TryOpen(name, out var handle) &&
                    CheckWin32Result(PInvoke.IsProcessInJob(processHandle, handle, out var jobNameMatches)) && jobNameMatches)
                {
                    jobName = name;
                    jobHandle = handle;
                    found = true;
                }
                else
                {
                    handle.Dispose();

                    found = false;
                    jobName = "";
                    jobHandle = new SafeFileHandle();
                }
            }
            else
            {
                found = false;
                jobName = "";
                jobHandle = new SafeFileHandle();
            }

            return found;
        }

        void AssignProcessToExistingJobObject(int processId, Win32Job job, bool checkIfAlreadyAssigned)
        {
            using var processHandle = CheckWin32Result(PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION |
               PROCESS_ACCESS_RIGHTS.PROCESS_SET_QUOTA | PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE |
               PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)processId));

            if (!IsRemoteProcessTheSameBitness(processHandle))
            {
                logger.TraceEvent(TraceEventType.Error, 0, $"Process {processId} bitness does not match prcogov bitness. Skipping.");
                return;
            }

            if (checkIfAlreadyAssigned && TryOpenProcGovJob(processHandle, out var processJobHandle, out var jobName))
            {
                processJobHandle.Dispose();
                if (jobName == job.JobName)
                {
                    logger.TraceEvent(TraceEventType.Verbose, 0, $"Process {processId} already assigned to job '{jobName}'.");
                    SetProcessEnvironmentVariables(processId, session.AdditionalEnvironmentVars);

                    AccountPrivilegeModule.EnablePrivileges((uint)processId, processHandle, session.Privileges, TraceEventType.Error);
                }
                else
                {
                    logger.TraceEvent(TraceEventType.Warning, 0,
                        $"Process {processId} assigned to a different procgov job ('{jobName}'). Skipping.");
                }
            }
            else
            {
                session.AdditionalEnvironmentVars[JobNameEnvironmentVariable] = job.JobName;
                SetProcessEnvironmentVariables(processId, session.AdditionalEnvironmentVars);

                logger.TraceEvent(TraceEventType.Verbose, 0, $"Assigning process {processId} to job '{job.JobName}'");
                Win32JobModule.AssignProcess(job, processHandle, session.PropagateOnChildProcesses);

                AccountPrivilegeModule.EnablePrivileges((uint)processId, processHandle, session.Privileges, TraceEventType.Error);
            }
        }

        var currentProcessId = (uint)Environment.ProcessId;
        using var currentProcessHandle = PInvoke.GetCurrentProcess_SafeHandle();
        var dbgpriv = AccountPrivilegeModule.EnablePrivileges(currentProcessId, currentProcessHandle, new[] { "SeDebugPrivilege" },
                        TraceEventType.Information);

        try
        {
            Win32Job? job = null;

            // firstly, we need to check if any of the processes hasn't been already assigned to a procgov job
            for (int i = 0; i < pids.Length && job is null; i++)
            {
                var jobProcessId = pids[i];

                using var jobProcessHandle = CheckWin32Result(PInvoke.OpenProcess_SafeHandle(
                    PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)jobProcessId));

                if (!IsRemoteProcessTheSameBitness(jobProcessHandle))
                {
                    logger.TraceEvent(TraceEventType.Error, 0, $"Process {jobProcessId} bitness does not match prcogov bitness. Skipping.");
                    continue;
                }

                if (TryOpenProcGovJob(jobProcessHandle, out var jobHandle, out var jobName))
                {
                    // we need to update variables and priviles in the 'job process' manually as we
                    // won't be assigning it to the job to which it is already assigned
                    SetProcessEnvironmentVariables(jobProcessId, session.AdditionalEnvironmentVars);
                    AccountPrivilegeModule.EnablePrivileges((uint)jobProcessId, jobProcessHandle, session.Privileges, TraceEventType.Error);

                    job = new Win32Job(jobHandle, jobName, null, session.ClockTimeLimitInMilliseconds);

                    logger.TraceEvent(TraceEventType.Verbose, 0,
                        $"Procgov job already exists ('{jobName}') for process {jobProcessId} and we will use it for other processes.");

                    foreach (var targetProcessId in pids.Where(pid => pid != jobProcessId))
                    {
                        AssignProcessToExistingJobObject(targetProcessId, job, true);
                    }
                }
            }

            if (job is null)
            {
                job = Win32JobModule.CreateJobObject(GetNewJobName(), session.ClockTimeLimitInMilliseconds);
                logger.TraceEvent(TraceEventType.Verbose, 0, $"All processes will be assigned to a newly created job ({job.JobName}).");

                foreach (var targetProcessId in pids)
                {
                    AssignProcessToExistingJobObject(targetProcessId, job, false);
                }
            }

            Debug.Assert(job is not null);
            Win32JobModule.SetLimits(job, session, GetSystemOrProcessorGroupAffinity(currentProcessHandle, session));

            return job;
        }
        finally
        {
            AccountPrivilegeModule.RestorePrivileges(currentProcessId, currentProcessHandle, dbgpriv, TraceEventType.Information);
        }
    }
    */

    public static ulong GetSystemOrProcessorGroupAffinity()
    {
        nuint processAffinityMask = 0, systemAffinityMask = 0;
        unsafe
        {
            CheckWin32Result(PInvoke.GetProcessAffinityMask(
                PInvoke.GetCurrentProcess(), &processAffinityMask, &systemAffinityMask));
            if (systemAffinityMask == 0 && processAffinityMask == 0)
            {
                logger.TraceEvent(TraceEventType.Warning, 0, "The process belongs to more than 1 processor group. " +
                    "Procgov will not able to set the process affinity.");
            }
        }
        return systemAffinityMask;
    }
}