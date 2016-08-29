// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32 {
  public class LastWin32ErrorException : Win32Exception {
    public LastWin32ErrorException()
      : base(Marshal.GetLastWin32Error()) {
    }
    public LastWin32ErrorException(string message)
      : this(Marshal.GetLastWin32Error(), message) {
    }
    public LastWin32ErrorException(int errorCode)
      : base(errorCode) {
    }
    public LastWin32ErrorException(int errorCode, string message)
      : base(errorCode, string.Format("{0}: {1}", message, new Win32Exception(errorCode).Message)) {
    }
  }
}
