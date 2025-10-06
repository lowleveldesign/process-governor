using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace ProcessGovernor.Library;

internal unsafe static class AccountPrivilegeModule
{
    public static bool TryEnablingProcessPrivileges(SafeHandle processHandle, ImmutableArray<string> privileges,
        out List<(string PrivilegeName, int ErrorCode)> errors)
    {
        if (privileges.Length == 0)
        {
            errors = [];
            return true;
        }

        if (!PInvoke.OpenProcessToken(processHandle, TOKEN_ACCESS_MASK.TOKEN_QUERY |
            TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES, out var tokenHandle))
        {
            var err = Marshal.GetLastWin32Error();
            errors = [.. privileges.Select(privilege => (privilege, err))];
            return false;
        }

        try
        {
            errors = [];
            foreach (var privilege in privileges)
            {
                try
                {
                    if (!PInvoke.LookupPrivilegeValue(null, privilege, out var luid))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    EnablePrivilege(tokenHandle, luid);
                }
                catch (Win32Exception ex)
                {
                    errors.Add((privilege, ex.NativeErrorCode));
                }
            }
            return errors.Count == 0;
        }
        finally
        {
            tokenHandle.Dispose();
        }

        static void EnablePrivilege(SafeHandle tokenHandle, LUID luid)
        {
            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new() { e0 = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED } }
            };
            var res = PInvoke.AdjustTokenPrivileges(tokenHandle, false, &privileges, null);
            var err = Marshal.GetLastWin32Error();
            if (!res || err != (int)WIN32_ERROR.NO_ERROR)
            {
                throw new Win32Exception(err);
            }
        }
    }
}
