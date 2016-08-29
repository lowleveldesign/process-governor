// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Debugging {
  public static class NativeMethods {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DebugActiveProcess(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DebugActiveProcessStop(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WaitForDebugEvent(out DEBUG_EVENT debugEvent, uint timeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ContinueDebugEvent(int processId, int threadId, CONTINUE_STATUS continuteStatus);

    [DllImport("kernel32.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DebugSetProcessKillOnExit([In] bool flag);
  }
}
