using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

internal unsafe static class AccountPrivilegeModule
{
    internal static List<(string PrivilegeName, bool IsSuccess)> EnableProcessPrivileges(SafeHandle processHandle, 
        List<(string PrivilegeName, bool Required)> privileges)
    {
        CheckWin32Result(PInvoke.OpenProcessToken(processHandle, TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES,
            out var tokenHandle));
        try
        {
            return privileges.Select(privilege =>
            {
                CheckWin32Result(PInvoke.LookupPrivilegeValue(null, privilege.PrivilegeName, out var luid));

                var privileges = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new() { e0 = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED } }
                };
                PInvoke.AdjustTokenPrivileges(tokenHandle, false, &privileges, 0, null, null);
                var lastWin32Error = Marshal.GetLastWin32Error();

                if (lastWin32Error != (int)WIN32_ERROR.NO_ERROR && privilege.Required)
                {
                    throw new Win32Exception(lastWin32Error);
                }

                return (privilege.PrivilegeName, lastWin32Error == (int)WIN32_ERROR.NO_ERROR);
            }).ToList();
        }
        finally
        {
            tokenHandle.Dispose();
        }
    }
}
