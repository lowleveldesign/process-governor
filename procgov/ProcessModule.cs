using System.Diagnostics;
using System.Text;
using Windows.Win32;
using Windows.Win32.System.Threading;
using Microsoft.Win32.SafeHandles;
using static LowLevelDesign.Win32Commons;

namespace LowLevelDesign
{
    internal static class ProcessModule
    {
        private static readonly TraceSource logger = Program.Logger;

        public static (Win32Job, List<AccountPrivilege>) AttachToProcess(uint pid, SessionSettings session)
        {
            var currentProcessHandle = Process.GetCurrentProcess().SafeHandle;
            var dbgpriv = AccountPrivilegeModule.EnablePrivileges(currentProcessHandle, new[] { "SeDebugPrivilege" });
            try
            {
                var hProcess = CheckWin32Result(PInvoke.OpenProcess_SafeHandle(
                    PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION |
                    PROCESS_ACCESS_RIGHTS.PROCESS_SET_QUOTA |
                    PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE |
                    PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE, false, pid));

                if (!Win32JobModule.TryOpen(hProcess, pid, session.PropagateOnChildProcesses,
                        session.ClockTimeLimitInMilliseconds, out var job))
                {
                    job = Win32JobModule.CreateAndAssignToProcess(hProcess, pid,
                            session.PropagateOnChildProcesses, session.ClockTimeLimitInMilliseconds);
                }
                Debug.Assert(job != null);
                Win32JobModule.SetLimits(job, session);

                var privs = AccountPrivilegeModule.EnablePrivileges(hProcess, session.Privileges );

                return (job, privs);
            }
            finally
            {
                AccountPrivilegeModule.RestorePrivileges(currentProcessHandle, dbgpriv);
            }
        }

        public static unsafe (Win32Job, List<AccountPrivilege>) StartProcess(IList<string> procargs, SessionSettings session)
        {
            var pi = new PROCESS_INFORMATION();
            var si = new STARTUPINFOW();
            var processCreationFlags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT |
                                       PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;
            if (session.SpawnNewConsoleWindow)
            {
                processCreationFlags |= PROCESS_CREATION_FLAGS.CREATE_NEW_CONSOLE;
            }

            var args = string.Join(" ", procargs.Select((string s) => s.Contains(' ') ? "\"" + s + "\"" : s));
            var env = GetEnvironmentString(session.AdditionalEnvironmentVars);
            fixed (char* pargs = args)
            {
                fixed (char* penv = env)
                {
                    CheckWin32Result(PInvoke.CreateProcess(null, pargs, null, null, false,
                        processCreationFlags, penv, null, si, out pi));
                }
            }
            var hProcess = new SafeFileHandle(pi.hProcess, true);

            try
            {
                var job = Win32JobModule.CreateAndAssignToProcess(hProcess, pi.dwProcessId,
                            session.PropagateOnChildProcesses, session.ClockTimeLimitInMilliseconds);
                Win32JobModule.SetLimits(job, session);

                var privs = AccountPrivilegeModule.EnablePrivileges(hProcess, session.Privileges);

                // resume process main thread
                CheckWin32Result(PInvoke.ResumeThread(pi.hThread));

                return (job, privs);
            }
            catch
            {
                PInvoke.TerminateProcess(hProcess, 1);
                throw;
            }
            finally
            {
                // and we can close the thread handle
                PInvoke.CloseHandle(pi.hThread);
            }
        }

        public static unsafe (Win32Job, List<AccountPrivilege>) StartProcessUnderDebuggerAndDetach(IList<string> procargs, SessionSettings session)
        {
            var pi = new PROCESS_INFORMATION();
            var si = new STARTUPINFOW();
            var processCreationFlags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT |
                                       PROCESS_CREATION_FLAGS.DEBUG_ONLY_THIS_PROCESS;
            if (session.SpawnNewConsoleWindow)
            {
                processCreationFlags |= PROCESS_CREATION_FLAGS.CREATE_NEW_CONSOLE;
            }

            var args = string.Join(" ", procargs);
            var env = GetEnvironmentString(session.AdditionalEnvironmentVars);
            fixed (char* pargs = args)
            {
                fixed (char* penv = env)
                {
                    CheckWin32Result(PInvoke.CreateProcess(null, pargs, null, null, false,
                        processCreationFlags, penv, null, si, out pi));
                }
            }

            var hProcess = new SafeFileHandle(pi.hProcess, true);
            CheckWin32Result(PInvoke.DebugSetProcessKillOnExit(false));

            var job = Win32JobModule.CreateAndAssignToProcess(hProcess, pi.dwProcessId,
                            session.PropagateOnChildProcesses, session.ClockTimeLimitInMilliseconds);
            Win32JobModule.SetLimits(job, session);

            var privs = AccountPrivilegeModule.EnablePrivileges(hProcess, session.Privileges);

            // resume process main thread by detaching from the debuggee
            CheckWin32Result(PInvoke.DebugActiveProcessStop(pi.dwProcessId));
            // and we can close the thread handle
            PInvoke.CloseHandle(pi.hThread);

            return (job, privs);
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
}