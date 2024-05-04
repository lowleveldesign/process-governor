using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Services;

namespace ProcessGovernor;

static class WindowsServiceModule
{
    public static bool IsServiceInstalled(string name)
    {
        unsafe
        {
            if (PInvoke.OpenSCManager((PCWSTR)null, null, PInvoke.SC_MANAGER_CONNECT) is var scmHandle && scmHandle.Value == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var namePtr = Marshal.StringToHGlobalUni(name);
                try
                {
                    if (PInvoke.OpenService(scmHandle, (char*)namePtr, PInvoke.SERVICE_QUERY_STATUS) is var svcHandle && svcHandle.Value == 0)
                    {
                        if (Marshal.GetLastWin32Error() is var err && err == (int)WIN32_ERROR.ERROR_SERVICE_DOES_NOT_EXIST)
                        {
                            return false;
                        }

                        throw new Win32Exception(err);
                    }
                    PInvoke.CloseServiceHandle(svcHandle);

                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(namePtr);
                }
            }
            finally
            {
                PInvoke.CloseServiceHandle(scmHandle);
            }
        }
    }

    public static void InstallService(string name, string displayName, string binaryPath, string svcAccountName, string? svcAccountPassword)
    {
        unsafe
        {
            if (PInvoke.OpenSCManager((PCWSTR)null, null, PInvoke.SC_MANAGER_CREATE_SERVICE) is var scmHandle && scmHandle.Value == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            nint namePtr = 0, displayNamePtr = 0, binaryPathPtr = 0, svcAccountNamePtr = 0, svcAccountPasswordPtr = 0;
            try
            {
                namePtr = Marshal.StringToHGlobalUni(name);
                displayNamePtr = Marshal.StringToHGlobalUni(displayName);
                binaryPathPtr = Marshal.StringToHGlobalUni(binaryPath);
                svcAccountNamePtr = Marshal.StringToHGlobalUni(svcAccountName);
                if (svcAccountPassword is not null)
                {
                    svcAccountPasswordPtr = Marshal.StringToHGlobalUni(svcAccountPassword);
                }

                if (PInvoke.CreateService(scmHandle, (char*)namePtr, (char*)displayNamePtr, PInvoke.SERVICE_ALL_ACCESS,
                    ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS, SERVICE_START_TYPE.SERVICE_DEMAND_START, SERVICE_ERROR.SERVICE_ERROR_NORMAL,
                    (char*)binaryPathPtr, (char*)null, null, (char*)null, (char*)svcAccountNamePtr, (char*)svcAccountPasswordPtr) is var svcHandle
                    && svcHandle.Value == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                PInvoke.CloseServiceHandle(svcHandle);
            }
            finally
            {
                PInvoke.CloseServiceHandle(scmHandle);

                if (namePtr != 0) { Marshal.FreeHGlobal(namePtr); }
                if (displayNamePtr != 0) { Marshal.FreeHGlobal(displayNamePtr); }
                if (binaryPathPtr != 0) { Marshal.FreeHGlobal(binaryPathPtr); }
                if (svcAccountNamePtr != 0) { Marshal.FreeHGlobal(svcAccountNamePtr); }
                if (svcAccountPasswordPtr != 0) { Marshal.FreeHGlobal(svcAccountPasswordPtr); }
            }
        }
    }

    public static void UninstallService(string name)
    {
        unsafe
        {
            if (PInvoke.OpenSCManager((PCWSTR)null, null, PInvoke.SC_MANAGER_CONNECT) is var scmHandle && scmHandle.Value == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            nint namePtr = 0;
            try
            {
                namePtr = Marshal.StringToHGlobalUni(name);
                if (PInvoke.OpenService(scmHandle, (char*)namePtr, (uint)FILE_ACCESS_RIGHTS.DELETE) is var svcHandle && svcHandle.Value == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (!PInvoke.DeleteService(svcHandle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                PInvoke.CloseServiceHandle(svcHandle);
            }
            finally
            {
                PInvoke.CloseServiceHandle(scmHandle);

                if (namePtr != 0) { Marshal.FreeHGlobal(namePtr); }
            }
        }
    }
}
