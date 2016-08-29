// Copyright 2014 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32 {
  public struct ComPtr<T> : IDisposable  where T : class {
    private T ptr;

    public ComPtr(T ptr) {
      Debug.Assert(ptr == null || Marshal.IsComObject(ptr));
      this.ptr = ptr;
    }

    public T Ptr { get { return ptr; } }

    public void Dispose() {
      if (ptr != null)
        Marshal.ReleaseComObject(ptr);

      ptr = null;
    }

    public T Detach() {
      T result = ptr;
      ptr = null;
      return result;
    }
  }

  public static class ComPtr {
    public static ComPtr<T> Create<T>(T ptr) where T : class {
      return new ComPtr<T>(ptr);
    }
  }
}
