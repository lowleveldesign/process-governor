// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using Microsoft.Win32.SafeHandles;

namespace VsChromium.Core.Win32.Processes {
  public sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid {
    public SafeThreadHandle()
      : base(true) {
    }

    public SafeThreadHandle(IntPtr handle)
      : base(true) {
      base.SetHandle(handle);
    }

    public void InitialSetHandle(IntPtr h) {
      base.SetHandle(h);
    }

    protected override bool ReleaseHandle() {
      return Handles.NativeMethods.CloseHandle(handle);
    }
  }
}
