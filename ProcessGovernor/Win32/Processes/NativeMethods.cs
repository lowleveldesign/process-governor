// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Runtime.InteropServices;
using System.Text;
using VsChromium.Core.Win32.Interop;

namespace VsChromium.Core.Win32.Processes
{
    static class NativeMethods
    {
        [DllImport("shell32.dll", SetLastError = true)]
        public static extern IntPtr CommandLineToArgvW(
          [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine,
          out int pNumArgs);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", BestFitMapping = false, CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool CreateProcess(
          [MarshalAs(UnmanagedType.LPTStr)] string lpApplicationName,
          StringBuilder lpCommandLine,
          SecurityAttributes lpProcessAttributes,
          SecurityAttributes lpThreadAttributes,
          bool bInheritHandles,
          ProcessCreationFlags dwCreationFlags,
          StringBuilder lpEnvironment,
          [MarshalAs(UnmanagedType.LPTStr)] string lpCurrentDirectory,
          STARTUPINFO lpStartupInfo,
          [In, Out]
          PROCESS_INFORMATION lpProcessInformation);

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtReadVirtualMemory(
            SafeProcessHandle hProcess,
            IntPtr baseAddress,
            [Out] byte[] buffer,
            uint size,
            out uint lpNumberOfBytesRead);

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtWow64ReadVirtualMemory64(
            SafeProcessHandle hProcess,
            ulong baseAddress,
            IntPtr buffer,
            ulong bufferSize,
            out ulong lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(
          [MarshalAs(UnmanagedType.U4)] ProcessAccessFlags dwDesiredAccess,
          [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
          int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall,
          CharSet = CharSet.Unicode)]
        public static extern uint QueryFullProcessImageName(
          SafeProcessHandle hProcess,
          [MarshalAs(UnmanagedType.U4)] ProcessQueryImageNameMode flags,
          [Out] StringBuilder lpImageName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadProcessMemory(SafeProcessHandle hProcess, IntPtr lpBaseAddress,
          [Out] byte[] buffer, uint size, out UInt32 lpNumberOfBytesRead);

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtQueryInformationProcess(SafeProcessHandle hProcess, ProcessInfoClass pic, ref ProcessBasicInformation pbi, int cb, out int pSize);

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int NtWow64QueryInformationProcess64(SafeProcessHandle hProcess, ProcessInfoClass pic, ref ProcessBasicInformationWow64 pbi, int cb, out int pSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessAffinityMask(SafeProcessHandle hProcess, out long lpProcessAffinityMask,
            out long lpSystemAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out int lpExitCode);
    }
}
