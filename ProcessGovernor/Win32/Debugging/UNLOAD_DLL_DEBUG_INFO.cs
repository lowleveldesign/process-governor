using System;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Debugging {
  [StructLayout(LayoutKind.Sequential)]
  public struct UNLOAD_DLL_DEBUG_INFO {
    public IntPtr lpBaseOfDll;
  }
}