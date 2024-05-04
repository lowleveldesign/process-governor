using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace ProcessGovernor;

static partial class NtApi
{
    public static T CheckWin32Result<T>(T result)
    {
        var lastError = Marshal.GetLastPInvokeError();

        return result switch
        {
            SafeHandle handle when !handle.IsInvalid => result,
            HANDLE handle when (nint)WIN32_ERROR.ERROR_INVALID_HANDLE != handle.Value => result,
            uint n when n != 0xffffffff => result,
            bool b when b => result,
            BOOL b when b => result,
            WIN32_ERROR err when err == WIN32_ERROR.NO_ERROR => result,
            WIN32_ERROR err => throw new Win32Exception((int)err),
            WAIT_EVENT ev when ev != WAIT_EVENT.WAIT_FAILED => result,
            NTSTATUS nt when nt.Value == 0 => result,
            NTSTATUS nt => throw new Win32Exception(nt.Value),
            _ => throw new Win32Exception(lastError)
        };
    }

    public delegate int QueueApcThread(nint ThreadHandle, nint ApcRoutine, nint ApcArgument1, nint ApcArgument2, nint ApcArgument3);

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

    [LibraryImport("ntdll.dll")]
    internal static partial int RtlCreateUserThread(
        nint ProcessHandle,
        nint ThreadSecurityDescriptor,
        [MarshalAs(UnmanagedType.Bool)]
            bool CreateSuspended,
        uint ZeroBits,
        nuint MaximumStackSize,
        nuint CommittedStackSize,
        nint StartAddress,
        nint Parameter,
        out nint ThreadHandle,
        out CLIENT_ID ClientId
    );

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueueApcThread(
         nint ThreadHandle,
         nint ApcRoutine,
         nint ApcArgument1,
         nint ApcArgument2,
         nint ApcArgument3
    );

    [LibraryImport("ntdll.dll")]
    internal static partial int RtlQueueApcWow64Thread(
        nint ThreadHandle,
        nint ApcRoutine,
        nint ApcArgument1,
        nint ApcArgument2,
        nint ApcArgument3
    );
}

