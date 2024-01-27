using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.System.Memory;
using Windows.Wdk.System.Threading;

using WinProcessModule = System.Diagnostics.ProcessModule;
using PInvokeWdk = Windows.Wdk.PInvoke;

using System.Diagnostics;

namespace ProcessGovernor;

public static class NtApi
{
    private static unsafe bool IsRemoteProcessWow64(SafeHandle processHandle)
    {
        nint isWow64 = 0;

        uint returnLength = 0;
        CheckWin32Result(PInvokeWdk.NtQueryInformationProcess(
            (HANDLE)processHandle.DangerousGetHandle(), PROCESSINFOCLASS.ProcessWow64Information,
            &isWow64, (uint)nint.Size, ref returnLength));
        Debug.Assert(nint.Size == returnLength);

        return isWow64 != 0;
    }

    public static bool IsRemoteProcessTheSameBitness(SafeHandle processHandle)
    {
        return !Environment.Is64BitOperatingSystem || (
                Environment.Is64BitProcess && !IsRemoteProcessWow64(processHandle) ||
                    !Environment.Is64BitProcess && IsRemoteProcessWow64(processHandle));
    }

    public static unsafe Dictionary<string, string> GetProcessEnvironmentVariables(SafeHandle processHandle)
    {
        void ReadProcessMemory(void* src, void* dest, nint size)
        {
            nuint returnLength = 0;
            nuint expectedLength = (nuint)size;
            CheckWin32Result(PInvoke.ReadProcessMemory(processHandle, src, dest, expectedLength, &returnLength));
            Debug.Assert(expectedLength == returnLength);
        }

        Dictionary<string, string> ParseEnvironmentBlock(char[] environmentChars)
        {
            var variables = new Dictionary<string, string>();

            var name = new ReadOnlySpan<char>();
            var tokenBeginIndex = 0;
            for (int i = 0; i < environmentChars.Length; i++)
            {
                char c = environmentChars[i];
                if (name.Length == 0)
                {
                    if (c == '\0')
                    {
                        // this will fail if the environment starts with a null character
                        break;
                    }
                    else if (c == '=')
                    {
                        name = environmentChars.AsSpan(tokenBeginIndex, i - tokenBeginIndex);
                        tokenBeginIndex = i + 1;
                    }
                }
                else if (c == '\0')
                {
                    variables.Add(new string(name), new string(environmentChars.AsSpan(tokenBeginIndex, i - tokenBeginIndex)));
                    tokenBeginIndex = i + 1;
                    name = new ReadOnlySpan<char>();
                }
            }

            return variables;
        }

        Imports.PROCESS_BASIC_INFORMATION pbi = new();
        {
            uint actualLength = (uint)Marshal.SizeOf(pbi);
            uint returnLength = 0;
            CheckWin32Result(PInvokeWdk.NtQueryInformationProcess(
                (HANDLE)processHandle.DangerousGetHandle(), PROCESSINFOCLASS.ProcessBasicInformation,
                &pbi, actualLength, ref returnLength));
            Debug.Assert(actualLength == returnLength);
        }

        Imports.PartialPEB peb = new();
        ReadProcessMemory((void*)pbi.PebBaseAddress, &peb, Marshal.SizeOf(peb));

        nint environmentPtr = 0;
        ReadProcessMemory((byte*)peb.ProcessParameters + Marshal.OffsetOf<Imports.RTL_USER_PROCESS_PARAMETERS>(
            nameof(Imports.RTL_USER_PROCESS_PARAMETERS.Environment)), &environmentPtr, nint.Size);
        nint environmentSize = 0;
        ReadProcessMemory((byte*)peb.ProcessParameters + Marshal.OffsetOf<Imports.RTL_USER_PROCESS_PARAMETERS>(
            nameof(Imports.RTL_USER_PROCESS_PARAMETERS.EnvironmentSize)), &environmentSize, nint.Size);

        var environmentChars = new char[environmentSize / 2];
        fixed (void* environmentCharsPtr = environmentChars)
        {
            ReadProcessMemory((void*)environmentPtr, environmentCharsPtr, environmentSize);
        }

        return ParseEnvironmentBlock(environmentChars);
    }

    public static string? GetProcessEnvironmentVariable(SafeHandle processHandle, string name)
    {
        return GetProcessEnvironmentVariables(processHandle).TryGetValue(name, out var value) ? value : null;
    }

    public static unsafe void SetProcessEnvironmentVariables(int pid, Dictionary<string, string> variables)
    {
        if (variables.Count == 0)
        {
            return;
        }

        static nint GetExportedProcedureOffsetInCurrentProcess(string moduleName, string procedureName)
        {
            using var process = Process.GetCurrentProcess();
            var m = process.Modules.Cast<WinProcessModule>().FirstOrDefault(m => string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));
            return m is not null ? NativeLibrary.GetExport(m.BaseAddress, procedureName) - m.BaseAddress : nint.Zero;
        }

        // FIXME: I should rather use the path from the remote process modules (withdll)
        var fnRtlExitUserThread = GetExportedProcedureOffsetInCurrentProcess("ntdll.dll", "RtlExitUserThread");
        var fnSetEnvironmentVariableW = GetExportedProcedureOffsetInCurrentProcess("kernel32.dll", "SetEnvironmentVariableW");

        using var remoteProcess = Process.GetProcessById(pid);
        var remoteNtdllAddress = remoteProcess.Modules.Cast<WinProcessModule>().First(
            m => string.Equals(m.ModuleName, "ntdll.dll", StringComparison.OrdinalIgnoreCase)).BaseAddress;
        var remoteKernel32Address = remoteProcess.Modules.Cast<WinProcessModule>().First(
            m => string.Equals(m.ModuleName, "kernel32.dll", StringComparison.OrdinalIgnoreCase)).BaseAddress;

        using var remoteProcessHandle = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_CREATE_THREAD | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION |
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_OPERATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_WRITE | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)pid);

        nint remoteThreadStart = remoteNtdllAddress + fnRtlExitUserThread;

        CheckWin32Result(Imports.RtlCreateUserThread(remoteProcessHandle, nint.Zero, true, 0, 0, 0, remoteThreadStart,
            nint.Zero, out var remoteThreadHandle, out _));
        try
        {
            nuint allocLength = (nuint)variables.Select(kv => kv.Key.Length + kv.Value.Length + 2).Sum() * sizeof(char); // + 2 for null terminators
            var allocAddr = PInvoke.VirtualAllocEx(remoteProcessHandle, null, allocLength,
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
                            CheckWin32Result(PInvoke.WriteProcessMemory(remoteProcessHandle, currAddr, namePtr, lengthInBytes, null));
                            currAddr += lengthInBytes + sizeof(char); // + null terminator
                        }

                        nint valueAddr = (nint)currAddr;
                        fixed (void* valuePtr = vrb.Value)
                        {
                            var lengthInBytes = (nuint)vrb.Value.Length * sizeof(char);
                            CheckWin32Result(PInvoke.WriteProcessMemory(remoteProcessHandle, currAddr, valuePtr, lengthInBytes, null));
                            currAddr += lengthInBytes + sizeof(char); // + null terminator
                        }

                        CheckWin32Result(Imports.NtQueueApcThread(remoteThreadHandle, (remoteKernel32Address + fnSetEnvironmentVariableW),
                            nameAddr, valueAddr, nint.Zero));
                    }

                    // APC is the first thing the new thread executes when resumed
                    CheckWin32Result(PInvoke.ResumeThread(remoteThreadHandle));

                    CheckWin32Result(PInvoke.WaitForSingleObject(remoteThreadHandle, 5000));
                }
                finally
                {
                    PInvoke.VirtualFreeEx(remoteProcessHandle, allocAddr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
                }
            }
            else
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            remoteThreadHandle.Dispose();
        }
    }

    public static T CheckWin32Result<T>(T result)
    {
        return result switch
        {
            SafeHandle handle when !handle.IsInvalid => result,
            HANDLE handle when (nint)WIN32_ERROR.ERROR_INVALID_HANDLE != handle.Value => result,
            uint n when n != 0xffffffff => result,
            bool b when b => result,
            BOOL b when b => result,
            WIN32_ERROR err when err == WIN32_ERROR.NO_ERROR => result,
            WIN32_ERROR err => throw new Win32Exception((int)err),
            NTSTATUS nt when nt.Value == 0 => result,
            NTSTATUS nt => throw new Win32Exception(nt.Value),
            _ => throw new Win32Exception()
        };
    }

    /// <summary>
    /// NT function definitions are modified (or not) versions
    /// from https://github.com/googleprojectzero/sandbox-attacksurface-analysis-tools
    /// 
    /// //  Copyright 2019 Google Inc. All Rights Reserved.
    ///
    ///  Licensed under the Apache License, Version 2.0 (the "License");
    ///  you may not use this file except in compliance with the License.
    ///  You may obtain a copy of the License at
    ///
    ///  http://www.apache.org/licenses/LICENSE-2.0
    ///
    ///  Unless required by applicable law or agreed to in writing, software
    ///  distributed under the License is distributed on an "AS IS" BASIS,
    ///  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    ///  See the License for the specific language governing permissions and
    ///  limitations under the License.
    /// </summary>
    private static class Imports
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_BASIC_INFORMATION
        {
            public int ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [Flags]
        public enum PebFlags : byte
        {
            None = 0,
            ImageUsesLargePages = 0x01,
            IsProtectedProcess = 0x02,
            IsImageDynamicallyRelocated = 0x04,
            SkipPatchingUser32Forwarders = 0x08,
            IsPackagedProcess = 0x10,
            IsAppContainer = 0x20,
            IsProtectedProcessLight = 0x40,
            IsLongPathAwareProcess = 0x80,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PartialPEB
        {
            [MarshalAs(UnmanagedType.U1)]
            public bool InheritedAddressSpace;
            [MarshalAs(UnmanagedType.U1)]
            public bool ReadImageFileExecOptions;
            [MarshalAs(UnmanagedType.U1)]
            public bool BeingDebugged;
            public PebFlags PebFlags;
            public IntPtr Mutant;
            public IntPtr ImageBaseAddress;
            public IntPtr Ldr; // PPEB_LDR_DATA
            public IntPtr ProcessParameters; // PRTL_USER_PROCESS_PARAMETERS
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct UNICODE_STRING
        {
            ushort Length;
            ushort MaximumLength;
            [MarshalAs(UnmanagedType.LPWStr)]
            string? Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURDIR
        {
            public UNICODE_STRING DosPath;
            public IntPtr Handle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct STRING
        {
            private ushort Length;
            private ushort MaximumLength;
            private string? Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RTL_DRIVE_LETTER_CURDIR
        {
            public ushort Flags;
            public ushort Length;
            public uint TimeStamp;
            public STRING DosPath;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RTL_USER_PROCESS_PARAMETERS
        {
            public int MaximumLength;
            public int Length;
            public int Flags;
            public int DebugFlags;
            public IntPtr ConsoleHandle;
            public int ConsoleFlags;
            public IntPtr StdInputHandle;
            public IntPtr StdOutputHandle;
            public IntPtr StdErrorHandle;
            public CURDIR CurrentDirectory;
            public UNICODE_STRING DllPath;
            public UNICODE_STRING ImagePathName;
            public UNICODE_STRING CommandLine;
            public IntPtr Environment;
            public int StartingPositionLeft;
            public int StartingPositionTop;
            public int Width;
            public int Height;
            public int CharWidth;
            public int CharHeight;
            public int ConsoleTextAttributes;
            public int WindowFlags;
            public int ShowWindowFlags;
            public UNICODE_STRING WindowTitle;
            public UNICODE_STRING DesktopName;
            public UNICODE_STRING ShellInfo;
            public UNICODE_STRING RuntimeData;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x20)]
            public RTL_DRIVE_LETTER_CURDIR[] CurrentDirectories;
            public IntPtr EnvironmentSize;
            public IntPtr EnvironmentVersion;
            public IntPtr PackageDependencyData;
            public int ProcessGroupId;
            public int LoaderThreads;
            public UNICODE_STRING RedirectionDllName;
            public UNICODE_STRING HeapPartitionName;
            public IntPtr DefaultThreadpoolCpuSetMasks;
            public int DefaultThreadpoolCpuSetMaskCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CLIENT_ID
        {
            public IntPtr UniqueProcess;
            public IntPtr UniqueThread;
        }

        [DllImport("ntdll.dll")]
        public static extern NTSTATUS NtQueueApcThread(
             SafeHandle ThreadHandle,
             IntPtr ApcRoutine,
             IntPtr ApcArgument1,
             IntPtr ApcArgument2,
             IntPtr ApcArgument3
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeFileHandle CreateRemoteThread(
            SafeHandle hProcess,
            nint lpThreadAttributes,
            nuint dwStackSize,
            nint lpStartAddress,
            nint lpParameter,
            uint dwCreationFlags,
            nint lpThreadId
        );

        [DllImport("ntdll.dll", SetLastError = true)]
        internal static extern NTSTATUS RtlCreateUserThread(
            SafeHandle ProcessHandle,
            nint ThreadSecurityDescriptor,
            bool CreateSuspended,
            uint ZeroBits,
            nuint MaximumStackSize,
            nuint CommittedStackSize,
            nint StartAddress,
            nint Parameter,
            out SafeFileHandle ThreadHandle,
            out CLIENT_ID ClientId
        );
    }
}

