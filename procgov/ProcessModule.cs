using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.System.Memory;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.System.SystemServices;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Wdk.System.Threading;

using PInvokeWdk = Windows.Wdk.PInvoke;

using System.Diagnostics;

using static ProcessGovernor.NtApi;
using System.Text;

namespace ProcessGovernor;

sealed class Win32Process(SafeHandle processHandle, uint processId) : IDisposable
{
    public SafeHandle Handle => processHandle;

    public uint Id => processId;

    public void Dispose()
    {
        processHandle.Dispose();
    }
}

static class ProcessModule
{
    public static unsafe (Win32Process Process, SafeHandle MainThreadHandle) CreateSuspendedProcess(
        IEnumerable<string> procArgs, bool newConsole, Dictionary<string, string> additionalEnvironmentVars)
    {
        var pi = new PROCESS_INFORMATION();
        var si = new STARTUPINFOW();
        var processCreationFlags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;
        if (newConsole)
        {
            processCreationFlags |= PROCESS_CREATION_FLAGS.CREATE_NEW_CONSOLE;
        }

        var cmdline = (string.Join(" ", procArgs.Select((string s) => s.Contains(' ') ? "\"" + s + "\"" : s)) + '\0').ToCharArray();

        fixed (char* penv = GetEnvironmentString(additionalEnvironmentVars))
        fixed (char* cmdlinePtr = cmdline)
        {
            CheckWin32Result(PInvoke.CreateProcess(null, new PWSTR(cmdlinePtr), null, null, false,
                processCreationFlags, penv, null, &si, &pi));
        }

        return (new Win32Process(new SafeFileHandle(pi.hProcess, true), pi.dwProcessId), new SafeFileHandle(pi.hThread, true));

    }

    public unsafe static (Win32Process Process, SafeHandle MainThreadHandle) CreateSuspendedProcessWithJobAssigned(
        IEnumerable<string> procArgs, bool newConsole, Dictionary<string, string> additionalEnvironmentVars, SafeHandle jobHandle)
    {
        var procThreadAttrList = new LPPROC_THREAD_ATTRIBUTE_LIST();

        nuint procThreadAttrListSize = 0;
        if (!PInvoke.InitializeProcThreadAttributeList(procThreadAttrList, 1, 0, &procThreadAttrListSize) &&
            (Marshal.GetLastWin32Error() is var err && err != (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER))
        {
            throw new Win32Exception(err);
        }

        var procThreadAttrListPtr = (void*)Marshal.AllocHGlobal((nint)procThreadAttrListSize);
        try
        {
            procThreadAttrList = new LPPROC_THREAD_ATTRIBUTE_LIST(procThreadAttrListPtr);
            if (!PInvoke.InitializeProcThreadAttributeList(procThreadAttrList, 1, 0, &procThreadAttrListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                var rawJobHandle = jobHandle.DangerousGetHandle();
                if (!PInvoke.UpdateProcThreadAttribute(procThreadAttrList, 0, PInvoke.PROC_THREAD_ATTRIBUTE_JOB_LIST, &rawJobHandle,
                        (nuint)Marshal.SizeOf(rawJobHandle), null, (nuint*)null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var startupInfo = new STARTUPINFOEXW()
                {
                    StartupInfo = new STARTUPINFOW() { cb = (uint)sizeof(STARTUPINFOEXW) },
                    lpAttributeList = procThreadAttrList
                };
                var processCreationFlags = PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT | 
                    PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;
                if (newConsole)
                {
                    processCreationFlags |= PROCESS_CREATION_FLAGS.CREATE_NEW_CONSOLE;
                }

                var processInfo = new PROCESS_INFORMATION();

                var cmdline = (string.Join(" ", procArgs.Select((string s) => s.Contains(' ') ? "\"" + s + "\"" : s)) + '\0').ToCharArray();

                fixed (char* penv = GetEnvironmentString(additionalEnvironmentVars))
                fixed (char* cmdlinePtr = cmdline)
                {
                    if (!PInvoke.CreateProcess(null, new PWSTR(cmdlinePtr), null, null, true, processCreationFlags, null, null, 
                        (STARTUPINFOW*)&startupInfo, &processInfo))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{nameof(PInvoke.CreateProcess)} failed.");
                    }
                }

                return (new Win32Process(new SafeFileHandle(processInfo.hProcess, true), processInfo.dwProcessId), 
                    new SafeFileHandle(processInfo.hThread, true));
            }
            finally
            {
                PInvoke.DeleteProcThreadAttributeList(procThreadAttrList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal((nint)procThreadAttrListPtr);
        }
    }

    public static Win32Process OpenProcess(uint processId, bool allowInjections)
    {
        const PROCESS_ACCESS_RIGHTS RequiredJobAssignmentAccessRights =
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SET_QUOTA |
            PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ;
        const PROCESS_ACCESS_RIGHTS RequiredInjectionAccessRights = PROCESS_ACCESS_RIGHTS.PROCESS_CREATE_THREAD |
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_OPERATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_WRITE;

        var processHandle = CheckWin32Result(PInvoke.OpenProcess_SafeHandle(allowInjections ?
            RequiredJobAssignmentAccessRights | RequiredInjectionAccessRights :
            RequiredJobAssignmentAccessRights, false, processId));

        return new(processHandle, processId);
    }

    static string? GetEnvironmentString(Dictionary<string, string> additionalEnvironmentVars)
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

    public static string? GetProcessEnvironmentVariable(SafeHandle processHandle, string name, int maxAcceptableLength = 1024)
    {
        unsafe
        {
            bool isWow64 = IsProcessWow64(processHandle);

            var kernel32BaseAddr = GetModuleHandle(processHandle, isWow64, "kernel32.dll");
            var fnGetEnvironmentVariableW = (nint)GetModuleExportRva(processHandle, isWow64, kernel32BaseAddr, "GetEnvironmentVariableW");

            var ntdllHandle = GetModuleHandle(processHandle, isWow64, "ntdll.dll");
            var fnRtlExitUserThread = GetModuleExportRva(processHandle, isWow64, ntdllHandle, "RtlExitUserThread");
            var remoteThreadStart = ntdllHandle + (nint)fnRtlExitUserThread;

            var processRawHandle = processHandle.DangerousGetHandle();
            if (RtlCreateUserThread(processRawHandle, nint.Zero, true, 0, 0, 0, remoteThreadStart,
                 nint.Zero, out var remoteThreadHandle, out _) is var status && status != 0)
            {
                throw new Win32Exception((int)PInvoke.RtlNtStatusToDosError(new NTSTATUS(status)));
            }

            QueueApcThread queueApcThreadFunc = isWow64 ? RtlQueueApcWow64Thread : NtQueueApcThread;

            try
            {
                nuint valueBufferSize = (uint)maxAcceptableLength * sizeof(char);

                nuint allocLength = (nuint)name.Length * sizeof(char) + sizeof(char) /* null */ + valueBufferSize;
                var allocAddr = PInvoke.VirtualAllocEx(processHandle, null, allocLength,
                    VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE | VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE);
                if (allocAddr != null)
                {
                    try
                    {
                        // VirtualAllocEx initializes memory to 0 so we don't need to write the null terminator
                        var currAddr = (byte*)allocAddr;
                        nint nameAddr = (nint)currAddr;
                        fixed (void* namePtr = name)
                        {
                            var lengthInBytes = (nuint)name.Length * sizeof(char);
                            CheckWin32Result(PInvoke.WriteProcessMemory(processHandle, currAddr, namePtr, lengthInBytes, null));
                            currAddr += lengthInBytes + sizeof(char); // + null terminator
                        }
                        nint valueAddr = (nint)currAddr;

                        status = queueApcThreadFunc(remoteThreadHandle, kernel32BaseAddr + fnGetEnvironmentVariableW,
                            nameAddr, valueAddr, maxAcceptableLength);
                        if (status != 0)
                        {
                            throw new Win32Exception((int)PInvoke.RtlNtStatusToDosError(new NTSTATUS(status)));
                        }

                        // APC is the first thing the new thread executes when resumed
                        if (PInvoke.ResumeThread((HANDLE)remoteThreadHandle) < 0)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }

                        if (PInvoke.WaitForSingleObject((HANDLE)remoteThreadHandle, 5000) is var err && err == WAIT_EVENT.WAIT_TIMEOUT)
                        {
                            throw new Win32Exception((int)WIN32_ERROR.ERROR_TIMEOUT);
                        }
                        else if (err == WAIT_EVENT.WAIT_FAILED)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }

                        var valueBufferAddr = NativeMemory.AllocZeroed(valueBufferSize + sizeof(char) /* null */);
                        try
                        {
                            CheckWin32Result(PInvoke.ReadProcessMemory(processHandle, (void*)valueAddr,
                                valueBufferAddr, valueBufferSize, null));
                            return Marshal.PtrToStringUni((nint)valueBufferAddr);
                        }
                        finally
                        {
                            NativeMemory.Free(valueBufferAddr);
                        }
                    }
                    finally
                    {
                        PInvoke.VirtualFreeEx(processHandle, allocAddr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
                    }
                }
                else
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                PInvoke.CloseHandle((HANDLE)remoteThreadHandle);
            }
        }
    }

    public static void SetProcessEnvironmentVariables(SafeHandle processHandle, Dictionary<string, string> variables)
    {
        if (variables.Count == 0)
        {
            return;
        }

        unsafe
        {
            bool isWow64 = IsProcessWow64(processHandle);

            var kernel32BaseAddr = GetModuleHandle(processHandle, isWow64, "kernel32.dll");
            var fnSetEnvironmentVariableW = (nint)GetModuleExportRva(processHandle, isWow64, kernel32BaseAddr, "SetEnvironmentVariableW");

            var ntdllHandle = GetModuleHandle(processHandle, isWow64, "ntdll.dll");
            var fnRtlExitUserThread = GetModuleExportRva(processHandle, isWow64, ntdllHandle, "RtlExitUserThread");
            var remoteThreadStart = ntdllHandle + (nint)fnRtlExitUserThread;

            var processRawHandle = processHandle.DangerousGetHandle();
            if (RtlCreateUserThread(processRawHandle, nint.Zero, true, 0, 0, 0, remoteThreadStart,
                 nint.Zero, out var remoteThreadHandle, out _) is var status && status != 0)
            {
                throw new Win32Exception((int)PInvoke.RtlNtStatusToDosError(new NTSTATUS(status)));
            }

            QueueApcThread queueApcThreadFunc = isWow64 ? RtlQueueApcWow64Thread : NtQueueApcThread;

            try
            {
                nuint allocLength = (nuint)variables.Select(kv => kv.Key.Length + kv.Value.Length + 2).Sum() * sizeof(char); // + 2 for null terminators
                var allocAddr = PInvoke.VirtualAllocEx(processHandle, null, allocLength,
                    VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE | VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE);
                if (allocAddr != null)
                {
                    try
                    {
                        // VirtualAllocEx initializes memory to 0 so we don't need to write the null terminator
                        var currAddr = (byte*)allocAddr;
                        foreach (var vrb in variables)
                        {
                            nint nameAddr = (nint)currAddr;
                            fixed (void* namePtr = vrb.Key)
                            {
                                var lengthInBytes = (nuint)vrb.Key.Length * sizeof(char);
                                CheckWin32Result(PInvoke.WriteProcessMemory(processHandle, currAddr, namePtr, lengthInBytes, null));
                                currAddr += lengthInBytes + sizeof(char); // + null terminator
                            }

                            nint valueAddr = (nint)currAddr;
                            fixed (void* valuePtr = vrb.Value)
                            {
                                var lengthInBytes = (nuint)vrb.Value.Length * sizeof(char);
                                CheckWin32Result(PInvoke.WriteProcessMemory(processHandle, currAddr, valuePtr, lengthInBytes, null));
                                currAddr += lengthInBytes + sizeof(char); // + null terminator
                            }

                            status = queueApcThreadFunc(remoteThreadHandle, kernel32BaseAddr + fnSetEnvironmentVariableW,
                                nameAddr, valueAddr, 0);
                            if (status != 0)
                            {
                                throw new Win32Exception((int)PInvoke.RtlNtStatusToDosError(new NTSTATUS(status)));
                            }
                        }

                        // APC is the first thing the new thread executes when resumed
                        if (PInvoke.ResumeThread((HANDLE)remoteThreadHandle) < 0)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }

                        if (PInvoke.WaitForSingleObject((HANDLE)remoteThreadHandle, 5000) is var err && err == WAIT_EVENT.WAIT_TIMEOUT)
                        {
                            throw new Win32Exception((int)WIN32_ERROR.ERROR_TIMEOUT);
                        }
                        else if (err == WAIT_EVENT.WAIT_FAILED)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                    }
                    finally
                    {
                        PInvoke.VirtualFreeEx(processHandle, allocAddr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
                    }

                }
                else
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                PInvoke.CloseHandle((HANDLE)remoteThreadHandle);
            }
        }
    }

    static unsafe HMODULE GetModuleHandle(SafeHandle processHandle, bool isWow64, string moduleName)
    {
        const uint MaxModulesNumber = 256;

        var moduleHandles = stackalloc HMODULE[(int)MaxModulesNumber];
        uint cb = MaxModulesNumber * (uint)Marshal.SizeOf<HMODULE>();
        uint cbNeeded = 0;

        var processRawHandle = (HANDLE)processHandle.DangerousGetHandle();
        PInvoke.EnumProcessModulesEx(processRawHandle, moduleHandles, cb, &cbNeeded,
            isWow64 ? ENUM_PROCESS_MODULES_EX_FLAGS.LIST_MODULES_32BIT : ENUM_PROCESS_MODULES_EX_FLAGS.LIST_MODULES_64BIT);

        if (cb >= cbNeeded)
        {
            moduleName = Path.DirectorySeparatorChar + moduleName.ToUpper();
            var nameBuffer = stackalloc char[(int)PInvoke.MAX_PATH];
            foreach (var iterModuleHandle in new Span<HMODULE>(moduleHandles, (int)(cbNeeded / Marshal.SizeOf<HMODULE>())))
            {
                if (PInvoke.GetModuleFileNameEx(processRawHandle, iterModuleHandle, nameBuffer,
                        PInvoke.MAX_PATH) is var iterModuleNameLength && iterModuleNameLength > moduleName.Length)
                {
                    var iterModuleNameSpan = new Span<char>(nameBuffer, (int)iterModuleNameLength);
                    if (IsTheRightModule(iterModuleNameSpan))
                    {
                        return iterModuleHandle;
                    }
                }
            }
        }

        return (HMODULE)nint.Zero;

        bool IsTheRightModule(ReadOnlySpan<char> m)
        {
            var moduleNameSpan = moduleName.AsSpan();
            for (int i = 0; i < moduleNameSpan.Length; i++)
            {
                if (char.ToUpper(m[i + m.Length - moduleNameSpan.Length]) != moduleNameSpan[i])
                {
                    return false;
                }
            }
            return true;
        }
    }

    static unsafe uint GetModuleExportRva(SafeHandle processHandle, bool isWow64, nint moduleBase, string functionName)
    {
        var exportDirectory = ReadExportDirectory(processHandle, isWow64, moduleBase);

        if (TryFindingOrdinal(out var ordinal))
        {
            var ordinalBase = exportDirectory.Base;
            var functionAddresses = new uint[exportDirectory.NumberOfFunctions];
            fixed (uint* functionAddressesPtr = functionAddresses)
            {
                if (!PInvoke.ReadProcessMemory(processHandle, (void*)(moduleBase + exportDirectory.AddressOfFunctions),
                    functionAddressesPtr, (nuint)(functionAddresses.Length * sizeof(uint)), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            return functionAddresses[ordinal - ordinalBase];
        }

        return 0;


        bool TryFindingOrdinal(out uint ordinal)
        {
            var nameAddresses = new uint[exportDirectory.NumberOfNames];
            fixed (uint* namedAddressesPtr = nameAddresses)
            {
                if (!PInvoke.ReadProcessMemory(processHandle, (void*)(moduleBase + exportDirectory.AddressOfNames),
                    namedAddressesPtr, (nuint)(nameAddresses.Length * sizeof(uint)), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            var namedOrdinals = new ushort[exportDirectory.NumberOfNames];
            fixed (ushort* namedOrdinalsPtr = namedOrdinals)
            {
                if (!PInvoke.ReadProcessMemory(processHandle, (void*)(moduleBase + exportDirectory.AddressOfNameOrdinals),
                    namedOrdinalsPtr, (nuint)(namedOrdinals.Length * sizeof(ushort)), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            var ordinalBase = exportDirectory.Base;
            var buffer = stackalloc byte[(int)PInvoke.MAX_PATH];
            for (int i = 0; i < nameAddresses.Length; i++)
            {
                if (!PInvoke.ReadProcessMemory(processHandle, (void*)(moduleBase + nameAddresses[i]), buffer, PInvoke.MAX_PATH, null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                // make sure that we always have a null-terminated string
                buffer[PInvoke.MAX_PATH - 1] = 0;
                var name = Marshal.PtrToStringAnsi((nint)buffer) ?? "";
                if (string.Equals(name, functionName, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = ordinalBase + namedOrdinals[i];
                    return true;
                }
            }

            ordinal = 0;
            return false;
        }

        static unsafe IMAGE_EXPORT_DIRECTORY ReadExportDirectory(SafeHandle processHandle, bool isWow64, nint moduleBase)
        {
            IMAGE_DATA_DIRECTORY ReadExportDirectoryEntry32(int ntHeaderOffset)
            {
                IMAGE_NT_HEADERS32 inh;
                if (!PInvoke.ReadProcessMemory(processHandle, (void*)(moduleBase + ntHeaderOffset), &inh,
                    (nuint)Marshal.SizeOf<IMAGE_NT_HEADERS32>(), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return inh.OptionalHeader.DataDirectory.AsReadOnlySpan()[(int)IMAGE_DIRECTORY_ENTRY.IMAGE_DIRECTORY_ENTRY_EXPORT];
            }

            IMAGE_DATA_DIRECTORY ReadExportDirectoryEntry64(int ntHeaderOffset)
            {
                IMAGE_NT_HEADERS64 inh;
                if (!PInvoke.ReadProcessMemory(processHandle, (void*)(moduleBase + ntHeaderOffset), &inh,
                    (nuint)Marshal.SizeOf<IMAGE_NT_HEADERS64>(), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return inh.OptionalHeader.DataDirectory.AsReadOnlySpan()[(int)IMAGE_DIRECTORY_ENTRY.IMAGE_DIRECTORY_ENTRY_EXPORT];
            }

            IMAGE_DOS_HEADER idh;
            if (!PInvoke.ReadProcessMemory(processHandle, (void*)moduleBase, &idh, (nuint)Marshal.SizeOf<IMAGE_DOS_HEADER>(), null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var exportDirectoryEntry = isWow64 ? ReadExportDirectoryEntry32(idh.e_lfanew) : ReadExportDirectoryEntry64(idh.e_lfanew);

            IMAGE_EXPORT_DIRECTORY exportDirectory;
            if (!PInvoke.ReadProcessMemory(processHandle, (void*)(moduleBase + exportDirectoryEntry.VirtualAddress), &exportDirectory,
                (nuint)Marshal.SizeOf<IMAGE_EXPORT_DIRECTORY>(), null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return exportDirectory;
        }
    }

    static unsafe bool IsProcessWow64(SafeHandle processHandle)
    {
        nint isWow64 = 0;

        uint returnLength = 0;
        CheckWin32Result(PInvokeWdk.NtQueryInformationProcess(
            (HANDLE)processHandle.DangerousGetHandle(), PROCESSINFOCLASS.ProcessWow64Information,
            &isWow64, (uint)nint.Size, ref returnLength));
        Debug.Assert(nint.Size == returnLength);

        return isWow64 != 0;
    }

    public static void TerminateProcess(SafeHandle processHandle, uint exitCode)
    {
        CheckWin32Result(PInvoke.TerminateProcess(processHandle, exitCode));
    }
}