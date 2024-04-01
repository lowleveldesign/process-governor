using System.Text;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.System.Threading;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

internal static class ProcessModule
{
    private const string JobNameEnvironmentVariable = "PROCGOV_JOB";

    private static readonly TraceSource logger = Program.Logger;

    public static Win32Job AssignProcessToJobObject(int pid, SessionSettings session)
    {
        var currentProcessId = (uint)Environment.ProcessId;
        using var currentProcessHandle = PInvoke.GetCurrentProcess_SafeHandle();
        var dbgpriv = AccountPrivilegeModule.EnablePrivileges(currentProcessId, currentProcessHandle, new[] { "SeDebugPrivilege" },
                        TraceEventType.Information);

        try
        {
            using var targetProcessHandle = CheckWin32Result(PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION |
                PROCESS_ACCESS_RIGHTS.PROCESS_SET_QUOTA | PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE |
                PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)pid));

            if (!IsRemoteProcessTheSameBitness(targetProcessHandle))
            {
                throw new ArgumentException($"The target process has different bitness than procgov. Please use " +
                    "procgov32 for 32-bit processes and procgov64 for 64-bit processes");
            }

            Win32Job OpenOrCreateJob()
            {
                if (GetProcessEnvironmentVariable(targetProcessHandle, JobNameEnvironmentVariable) is string jobName &&
                    Win32JobModule.TryOpen(jobName, out var jobHandle) &&
                    CheckWin32Result(PInvoke.IsProcessInJob(targetProcessHandle, jobHandle, out var jobNameMatches)) && jobNameMatches)
                {
                    SetProcessEnvironmentVariables(pid, session.AdditionalEnvironmentVars);
                    return new Win32Job(jobHandle, jobName, null, session.ClockTimeLimitInMilliseconds);
                }
                else
                {
                    jobName = GetNewJobName();
                    session.AdditionalEnvironmentVars.Add(JobNameEnvironmentVariable, jobName);
                    SetProcessEnvironmentVariables(pid, session.AdditionalEnvironmentVars);

                    return Win32JobModule.CreateJobObjectAndAssignProcess(targetProcessHandle, jobName,
                        session.PropagateOnChildProcesses, session.ClockTimeLimitInMilliseconds);
                }
            }

            var job = OpenOrCreateJob();
            Debug.Assert(job != null);
            Win32JobModule.SetLimits(job, session, GetSystemOrProcessorGroupAffinity(targetProcessHandle, session));

            AccountPrivilegeModule.EnablePrivileges((uint)pid, targetProcessHandle, session.Privileges, TraceEventType.Error);

            return job;
        }
        finally
        {
            AccountPrivilegeModule.RestorePrivileges(currentProcessId, currentProcessHandle, dbgpriv, TraceEventType.Information);
        }
    }

    public static Win32Job AssignProcessesToJobObject(int[] pids, SessionSettings session)
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

    public static unsafe Win32Job StartProcessAndAssignToJobObject(
        IList<string> procargs, SessionSettings session)
    {
        var pi = new PROCESS_INFORMATION();
        var si = new STARTUPINFOW();
        var processCreationFlags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;
        if (session.SpawnNewConsoleWindow)
        {
            processCreationFlags |= PROCESS_CREATION_FLAGS.CREATE_NEW_CONSOLE;
        }

        var jobName = $"procgov-{Guid.NewGuid():D}";
        session.AdditionalEnvironmentVars.Add(JobNameEnvironmentVariable, jobName);

        fixed (char* penv = GetEnvironmentString(session.AdditionalEnvironmentVars))
        {
            var args = (string.Join(" ", procargs.Select((string s) => s.Contains(' ') ? "\"" + s + "\"" : s)) + '\0').ToCharArray();
            fixed (char* pargs = args)
            {
                var argsSpan = new Span<char>(pargs, args.Length);
                CheckWin32Result(PInvoke.CreateProcess(null, ref argsSpan, null, null, false, processCreationFlags,
                    penv, null, si, out pi));
            }
        }

        var processHandle = new SafeFileHandle(pi.hProcess, true);
        try
        {
            var job = Win32JobModule.CreateJobObjectAndAssignProcess(processHandle, jobName, session.PropagateOnChildProcesses,
                        session.ClockTimeLimitInMilliseconds);
            Win32JobModule.SetLimits(job, session, GetSystemOrProcessorGroupAffinity(processHandle, session));

            AccountPrivilegeModule.EnablePrivileges(pi.dwProcessId, processHandle, session.Privileges, TraceEventType.Error);

            CheckWin32Result(PInvoke.ResumeThread(pi.hThread));

            return job;
        }
        catch
        {
            PInvoke.TerminateProcess(processHandle, 1);
            throw;
        }
        finally
        {
            PInvoke.CloseHandle(pi.hThread);
        }
    }

    public static unsafe Win32Job StartProcessUnderDebuggerAndAssignToJobObject(
        IList<string> procargs, SessionSettings session)
    {
        var pi = new PROCESS_INFORMATION();
        var si = new STARTUPINFOW();
        var processCreationFlags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT |
                                   PROCESS_CREATION_FLAGS.DEBUG_ONLY_THIS_PROCESS;
        if (session.SpawnNewConsoleWindow)
        {
            processCreationFlags |= PROCESS_CREATION_FLAGS.CREATE_NEW_CONSOLE;
        }

        var jobName = GetNewJobName();
        session.AdditionalEnvironmentVars.Add(JobNameEnvironmentVariable, jobName);

        fixed (char* penv = GetEnvironmentString(session.AdditionalEnvironmentVars))
        {
            var args = (string.Join(" ", procargs.Select((string s) => s.Contains(' ') ? "\"" + s + "\"" : s)) + '\0').ToCharArray();
            fixed (char* pargs = args)
            {
                var argsSpan = new Span<char>(pargs, args.Length);
                CheckWin32Result(PInvoke.CreateProcess(null, ref argsSpan, null, null, false,
                    processCreationFlags, penv, null, si, out pi));
            }
        }

        var processHandle = new SafeFileHandle(pi.hProcess, true);
        try
        {
            CheckWin32Result(PInvoke.DebugSetProcessKillOnExit(false));

            var job = Win32JobModule.CreateJobObjectAndAssignProcess(processHandle, jobName, session.PropagateOnChildProcesses,
                        session.ClockTimeLimitInMilliseconds);
            Win32JobModule.SetLimits(job, session, GetSystemOrProcessorGroupAffinity(processHandle, session));

            AccountPrivilegeModule.EnablePrivileges(pi.dwProcessId, processHandle, session.Privileges, TraceEventType.Error);

            // resume process main thread by detaching from the debuggee
            CheckWin32Result(PInvoke.DebugActiveProcessStop(pi.dwProcessId));

            return job;
        }
        finally
        {
            PInvoke.CloseHandle(pi.hThread);
        }
    }

    private static ulong GetSystemOrProcessorGroupAffinity(SafeHandle processHandle, SessionSettings session)
    {
        CheckWin32Result(PInvoke.GetProcessAffinityMask(processHandle, out _, out var sysaff));
        if (sysaff == 0 && (session.CpuAffinityMask != 0 || session.NumaNode != 0xffff))
        {
            logger.TraceEvent(TraceEventType.Error, 0, "The process belongs to more than 1 processor group. " +
                "Procgov is not able to set the process affinity.");
            session.CpuAffinityMask = 0;
            session.NumaNode = 0xffff;
        }

        return (ulong)sysaff;
    }

    private static string GetNewJobName()
    {
        return $"procgov-{Guid.NewGuid():D}";
    }

    private static string? GetEnvironmentString(IDictionary<string, string> additionalEnvironmentVars)
    {
        if (additionalEnvironmentVars.Count == 0)
        {
            return null;
        }

        StringBuilder envEntries = new();
        foreach (string env in Environment.GetEnvironmentVariables().Keys)
        {
            if (additionalEnvironmentVars.ContainsKey(env))
            {
                continue; // overwrite existing env
            }

            envEntries.Append(env).Append('=').Append(
                Environment.GetEnvironmentVariable(env)).Append('\0');
        }

        foreach (var kv in additionalEnvironmentVars)
        {
            envEntries.Append(kv.Key).Append('=').Append(
                kv.Value).Append('\0');
        }

        envEntries.Append('\0');

        return envEntries.ToString();
    }
}