// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VsChromium.Core.Win32.Processes {
  [StructLayout(LayoutKind.Sequential)]
  public class STARTUPINFO : IDisposable {
    public int cb;
    public IntPtr lpReserved = IntPtr.Zero;
    public IntPtr lpDesktop = IntPtr.Zero;
    public IntPtr lpTitle = IntPtr.Zero;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2 = IntPtr.Zero;
    public SafeFileHandle hStdInput = new SafeFileHandle(IntPtr.Zero, false);
    public SafeFileHandle hStdOutput = new SafeFileHandle(IntPtr.Zero, false);
    public SafeFileHandle hStdError = new SafeFileHandle(IntPtr.Zero, false);

    public STARTUPINFO() {
      cb = Marshal.SizeOf(this);
    }

    public void Dispose() {
      if (hStdInput != null && !hStdInput.IsInvalid) {
        hStdInput.Close();
        hStdInput = null;
      }
      if (hStdOutput != null && !hStdOutput.IsInvalid) {
        hStdOutput.Close();
        hStdOutput = null;
      }
      if (hStdError != null && !hStdError.IsInvalid) {
        hStdError.Close();
        hStdError = null;
      }
    }
  }
}
