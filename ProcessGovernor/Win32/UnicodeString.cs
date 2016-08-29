// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32 {
  // Win32 UNICODE_STRING structure.
  [StructLayout(LayoutKind.Sequential, Pack=1)]
  public struct UnicodeString {
    // The length in bytes of the string pointed to by buffer, not including the null-terminator.
    private ushort length;
    // The total allocated size in memory pointed to by buffer.
    private ushort maximumLength;
    // A pointer to the buffer containing the string data.
    private IntPtr buffer;

    public ushort Length { get { return length; } }
    public ushort MaximumLength { get { return maximumLength; } }
    public IntPtr Buffer { get { return buffer; } }
  }

  [StructLayout(LayoutKind.Sequential, Pack = 1)] 
  public struct UnicodeStringWow64 {
    // The length in bytes of the string pointed to by buffer, not including the null-terminator.
    private ushort length;
    // The total allocated size in memory pointed to by buffer.
    private ushort maximumLength;

    // 4 bytes of padding.
    private uint padding;

    // A 64-bit pointer to the buffer containing the string data.
    private ulong buffer;

    public ushort Length { get { return length; } }
    public ushort MaximumLength { get { return maximumLength; } }
    public ulong Buffer { get { return buffer; } }
  }
}
