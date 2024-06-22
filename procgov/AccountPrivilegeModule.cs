using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

internal record class AccountPrivilege(string PrivilegeName, int Result, TOKEN_PRIVILEGES ReplacedPrivilege);

internal sealed class ScopedAccountPrivileges : IDisposable
{
    private readonly List<AccountPrivilege> accountPrivileges;

    public ScopedAccountPrivileges(IEnumerable<string> privilegeNames)
    {
        var pid = (uint)Environment.ProcessId;
        using var processHandle = PInvoke.GetCurrentProcess_SafeHandle();

        accountPrivileges = AccountPrivilegeModule.EnablePrivileges(processHandle, privilegeNames);

        foreach (var accountPrivilege in accountPrivileges.Where(ap => ap.Result != (int)WIN32_ERROR.NO_ERROR))
        {
            Program.Logger.TraceInformation("Acquiring privilege {accountPrivilege.PrivilegeName} for process " +
                $"{pid} failed - 0x{accountPrivilege.Result:x}");
        }
    }

    public void Dispose()
    {
        var pid = (uint)Environment.ProcessId;
        using var processHandle = PInvoke.GetCurrentProcess_SafeHandle();

        foreach (var (privilegeName, winError) in AccountPrivilegeModule.RestorePrivileges(processHandle, accountPrivileges))
        {
            Program.Logger.TraceInformation($"Error while reverting the {privilegeName} privilege for process {pid}: 0x{winError:x}");
        }
    }
}

internal unsafe static class AccountPrivilegeModule
{
    private static readonly TraceSource logger = Program.Logger;

    public static bool IsCurrentUserAdministrator()
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static List<AccountPrivilege> EnablePrivileges(SafeHandle processHandle, IEnumerable<string> privilegeNames)
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
                var result = PInvoke.AdjustTokenPrivileges(tokenHandle, false, privileges, (uint)Marshal.SizeOf(previousPrivileges), &previousPrivileges, &length);
                var lastWin32Error = Marshal.GetLastWin32Error();

                return result ? new AccountPrivilege(privilegeName, lastWin32Error, previousPrivileges) :
                    new AccountPrivilege(privilegeName, lastWin32Error, new TOKEN_PRIVILEGES { PrivilegeCount = 0 });
            }).ToList();
        }
        finally
        {
            tokenHandle.Dispose();
        }
    }

    // does not throw an exception
    internal static IEnumerable<(string, int)> RestorePrivileges(SafeHandle processHandle, List<AccountPrivilege> privileges)
    {
        if (PInvoke.OpenProcessToken(processHandle, TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES, out var tokenHandle))
        {
            try
            {
                return privileges.Where(priv => priv.Result == (int)WIN32_ERROR.NO_ERROR).Select(priv =>
                {
                    return PInvoke.AdjustTokenPrivileges(tokenHandle, false, priv.ReplacedPrivilege, 0, null, null) ?
                        (priv.PrivilegeName, (int)WIN32_ERROR.NO_ERROR) : (priv.PrivilegeName, Marshal.GetLastWin32Error());
                });
            }
            finally
            {
                tokenHandle.Dispose();
            }
        }
        else
        {
            int winerr = Marshal.GetLastWin32Error();
            return [("ProcessToken", winerr)];
        }
    }
}
