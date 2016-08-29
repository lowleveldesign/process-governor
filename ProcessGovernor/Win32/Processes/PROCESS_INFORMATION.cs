// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Processes {
  [StructLayout(LayoutKind.Sequential)]
  public class PROCESS_INFORMATION {
    /// <summary>
    /// Handle to the created process. The caller *must* call CloseHandle to avoid leaks.
    /// </summary>
    public IntPtr hProcess;
    /// <summary>
    /// Handle to the created process main thread. The caller *must* call CloseHandle to avoid leaks.
    /// </summary>
    public IntPtr hThread;
    public int dwProcessId;
    public int dwThreadId;
  }
}
