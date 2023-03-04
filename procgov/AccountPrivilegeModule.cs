using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

internal record class AccountPrivilege(string PrivilegeName, int Result, TOKEN_PRIVILEGES ReplacedPrivilege);

internal unsafe static class AccountPrivilegeModule
{
    private static readonly TraceSource logger = Program.Logger;

    public static bool IsCurrentUserAdministrator()
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static List<AccountPrivilege> EnablePrivileges(uint pid, SafeHandle processHandle,
        string[] privilegeNames, TraceEventType errorSeverity)
    {
        CheckWin32Result(PInvoke.OpenProcessToken(processHandle, TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES,
            out var tokenHandle));
        try
        {
            return privilegeNames.Select(privilegeName =>
            {
                CheckWin32Result(PInvoke.LookupPrivilegeValue(null, privilegeName, out var luid));

                var privileges = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new() { _0 = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED } }
                };
                var previousPrivileges = new TOKEN_PRIVILEGES();
                uint length = 0;
                if (PInvoke.AdjustTokenPrivileges(tokenHandle, false, privileges, (uint)Marshal.SizeOf(previousPrivileges), &previousPrivileges, &length))
                {
                    Debug.Assert(length == Marshal.SizeOf(previousPrivileges));
                    var result = Marshal.GetLastWin32Error();
                    if (result != (int)WIN32_ERROR.NO_ERROR)
                    {
                        logger.TraceEvent(errorSeverity, 0, $"Setting privilege {privilegeName} for process {pid} failed - 0x{result:x} " +
                            "(probably privilege is not available)");
                    }
                    return new AccountPrivilege(privilegeName, result, previousPrivileges);
                }
                else
                {
                    var result = Marshal.GetLastWin32Error();
                    if (result != (int)WIN32_ERROR.NO_ERROR)
                    {
                        logger.TraceEvent(errorSeverity, 0, $"Setting privilege {privilegeName} for process {pid} failed - 0x{result:x} ");
                    }
                    return new AccountPrivilege(privilegeName, result, new TOKEN_PRIVILEGES { PrivilegeCount = 0 });
                }
            }).ToList();
        }
        finally
        {
            tokenHandle.Dispose();
        }
    }

    internal static void RestorePrivileges(uint pid, SafeHandle processHandle, List<AccountPrivilege> privileges,
        TraceEventType errorSeverity)
    {
        if (PInvoke.OpenProcessToken(processHandle, TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES, out var tokenHandle))
        {
            try
            {
                foreach (var priv in privileges.Where(priv => priv.Result == (int)WIN32_ERROR.NO_ERROR))
                {
                    if (!PInvoke.AdjustTokenPrivileges(tokenHandle, false, priv.ReplacedPrivilege, 0, null, null))
                    {
                        int winerr = Marshal.GetLastWin32Error();
                        logger.TraceEvent(errorSeverity, 0,
                            $"Error while reverting the {priv.PrivilegeName} privilege for process {pid}: 0x{winerr:x}");
                    }
                }
            }
            finally
            {
                tokenHandle.Dispose();
            }
        }
        else
        {
            int winerr = Marshal.GetLastWin32Error();
            logger.TraceEvent(errorSeverity, 0, $"Error while reverting the privileges for process {pid}: 0x{winerr:x}");
        }
    }

}
