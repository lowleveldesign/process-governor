// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;

namespace VsChromium.Core.Win32.Processes {
  // The target architecture of a given executable image.  The various values correspond
  // to the magic numbers defined by the PE Executable Image File Format.
  // http://www.microsoft.com/whdc/system/platform/firmware/PECOFF.mspx
  public enum MachineType : ushort {
    Unknown = 0x0,
    X64 = 0x8664,
    X86 = 0x14c,
    Ia64 = 0x200
  }

  // A flag indicating the format of the path string that Windows returns from a call to
  // QueryFullProcessImageName().
  public enum ProcessQueryImageNameMode : uint {
    Win32 = 0,
    NativeSystemFormat = 1
  }

  // Flags indicating the level of permission requested when opening a handle to an external
  // process.  Used by OpenProcess().
  [Flags]
  public enum ProcessAccessFlags : uint {
    None = 0x0,
    All = 0x001F0FFF,
    VmOperation = 0x00000008,
    VmRead = 0x00000010,
    QueryInformation = 0x00000400,
    QueryLimitedInformation = 0x00001000
  }

  // Determines the amount of information requested (and hence the type of structure returned)
  // by a call to NtQueryInformationProcess.
  public enum ProcessInfoClass : int {
    BasicInformation = 0
  };

  [Flags]
  public enum SHGFI : uint {
    Icon = 0x000000100,
    DisplayName = 0x000000200,
    TypeName = 0x000000400,
    Attributes = 0x000000800,
    IconLocation = 0x000001000,
    ExeType = 0x000002000,
    SysIconIndex = 0x000004000,
    LinkOverlay = 0x000008000,
    Selected = 0x000010000,
    Attr_Specified = 0x000020000,
    LargeIcon = 0x000000000,
    SmallIcon = 0x000000001,
    OpenIcon = 0x000000002,
    ShellIconSize = 0x000000004,
    PIDL = 0x000000008,
    UseFileAttributes = 0x000000010,
    AddOverlays = 0x000000020,
    OverlayIndex = 0x000000040,
  }
}
