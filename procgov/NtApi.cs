using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace ProcessGovernor.Win32;
static partial class Helpers
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

    public static HANDLE ToWin32Handle(this SafeHandle h) => (HANDLE)h.DangerousGetHandle();
}

/* ** NT JOB API ** */

[Flags]
enum FreezeInformationFlags : uint
{
    FreezeOperation = 0x0001,
    FilterOperation = 0x0002,
    SwapOperation = 0x0004
}

[StructLayout(LayoutKind.Sequential)]
struct JOBOBJECT_WAKE_FILTER
{
    public uint HighWatermark;
    public uint LowWatermark;
}

[StructLayout(LayoutKind.Sequential)]
struct JOBOBJECT_FREEZE_INFORMATION
{
    public FreezeInformationFlags Flags;
    public byte Freeze;
    public byte Swap;
    public byte Reserved0;
    public byte Reserved1;
    JOBOBJECT_WAKE_FILTER WakeFilter;
}

/* ** NT API ** */

[StructLayout(LayoutKind.Sequential)]
public struct CLIENT_ID
{
    public IntPtr UniqueProcess;
    public IntPtr UniqueThread;
}

/* ** ** */

static partial class PInvoke
{
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

