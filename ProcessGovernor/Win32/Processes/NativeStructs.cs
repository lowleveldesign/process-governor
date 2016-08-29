// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Processes {
  // In general, for all structures below which contains a pointer (represented here by IntPtr),
  // the pointers refer to memory in the address space of the process from which the original
  // structure was read.  While this seems obvious, it means we cannot provide an elegant
  // interface to the various fields in the structure due to the de-reference requiring a
  // handle to the target process.  Instead, that functionality needs to be provided at a
  // higher level.
  //
  // Additionally, since we usually explicitly define the fields that we're interested in along
  // with their respective offsets, we frequently specify the exact size of the native structure.

  // Win32 RTL_USER_PROCESS_PARAMETERS structure.
  [StructLayout(LayoutKind.Sequential, Pack=1)]
  public struct RtlUserProcessParameters {
    private uint maximumLength;
    private uint length;
    private uint flags;
    private uint debugFlags;
    private IntPtr consoleHandle;
    private uint consoleFlags;
    private IntPtr stdInputHandle;
    private IntPtr stdOutputHandle;
    private IntPtr stdErrorHandle;
    private UnicodeString currentDirectoryPath;
    private IntPtr currentDirectoryHandle;
    private UnicodeString dllPath;
    private UnicodeString imagePathName;
    private UnicodeString commandLine;

    public UnicodeString CommandLine { get { return commandLine; } }
  };

  // Win32 RTL_USER_PROCESS_PARAMETERS structure.
  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  public struct RtlUserProcessParametersWow64 {
    private uint maximumLength;
    private uint length;
    private uint flags;
    private uint debugFlags;
    private ulong consoleHandle;
    private uint consoleFlags;
    private uint padding;
    private ulong stdInputHandle;
    private ulong stdOutputHandle;
    private ulong stdErrorHandle;
    private UnicodeStringWow64 currentDirectoryPath;
    private ulong currentDirectoryHandle;
    private UnicodeStringWow64 dllPath;
    private UnicodeStringWow64 imagePathName;
    private UnicodeStringWow64 commandLine;

    public UnicodeStringWow64 CommandLine { get { return commandLine; } }
  };

  // Win32 PEB structure.  Represents the process environment block of a process.
  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  public struct Peb {
    private byte inheritedAddressSpace;
    private byte readImageFileExecutionOptions;
    private byte isBeingDebugged;
    private byte reserved;
    private IntPtr mutant;
    private IntPtr imageBaseAddress;
    private IntPtr loaderData;
    private IntPtr processParameters;

    public bool IsBeingDebugged { get { return !(isBeingDebugged == 0); } }
    public IntPtr ProcessParameters { get { return processParameters; } }
  };

  // Win32 PEB structure.  Represents the process environment block of a process.
  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  public struct PebWow64 {
    private byte inheritedAddressSpace;
    private byte readImageFileExecutionOptions;
    private byte isBeingDebugged;
    private byte reserved;
    private uint padding;
    private ulong mutant;
    private ulong imageBaseAddress;
    private ulong loaderData;
    private ulong processParameters;

    public bool IsBeingDebugged { get { return !(isBeingDebugged == 0); } }
    public ulong ProcessParameters { get { return processParameters; } }
  };
  // Win32 PROCESS_BASIC_INFORMATION.  Contains a pointer to the PEB, and various other
  // information about a process.
  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  public struct ProcessBasicInformation {
    private IntPtr reserved;
    private IntPtr pebBaseAddress;
    private IntPtr reserved2;
    private IntPtr reserved3;
    private UIntPtr uniqueProcessId;
    private IntPtr parentProcessId;

    public IntPtr PebBaseAddress { get { return pebBaseAddress; } }
    public UIntPtr UniqueProcessId { get { return uniqueProcessId; } }
    public IntPtr ParentProcessId { get { return parentProcessId; } }
  }

  // Win32 PROCESS_BASIC_INFORMATION.  Contains a pointer to the PEB, and various other
  // information about a process.
  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  public struct ProcessBasicInformationWow64 {
    private ulong reserved;
    private ulong pebBaseAddress;
    private ulong reserved2;
    private ulong reserved3;
    private ulong uniqueProcessId;
    private ulong parentProcessId;

    public ulong PebBaseAddress { get { return pebBaseAddress; } }
    public ulong UniqueProcessId { get { return uniqueProcessId; } }
    public ulong ParentProcessId { get { return parentProcessId; } }
  }
}
