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
        if (privileges.Count == 0)
        {
            return [];
        }

        if (!PInvoke.OpenProcessToken(processHandle, TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES,
            out var tokenHandle))
        {
            var err = Marshal.GetLastWin32Error();
            if (privileges.Any(priv => priv.Required))
            {
                throw new Win32Exception(err);
            }
            return privileges.Select(priv => (priv.PrivilegeName, false)).ToList();
        }

        try
        {
            return privileges.Select(priv =>
            {
                var err = (int)WIN32_ERROR.NO_ERROR;

                if (PInvoke.LookupPrivilegeValue(null, priv.PrivilegeName, out var luid))
                {
                    var privileges = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Privileges = new() { e0 = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED } }
                    };
                    PInvoke.AdjustTokenPrivileges(tokenHandle, false, &privileges, 0, null, null);
                    err = Marshal.GetLastWin32Error();
                }
                else
                {
                    err = Marshal.GetLastWin32Error();
                }

                if (err != (int)WIN32_ERROR.NO_ERROR && priv.Required)
                {
                    throw new Win32Exception(err);
                }

                return (priv.PrivilegeName, err == (int)WIN32_ERROR.NO_ERROR);
            }).ToList();
        }
        finally
        {
            tokenHandle.Dispose();
        }
    }
}
