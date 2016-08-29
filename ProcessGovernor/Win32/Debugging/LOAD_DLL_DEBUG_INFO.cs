using System;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Debugging {
  [StructLayout(LayoutKind.Sequential)]
  public struct LOAD_DLL_DEBUG_INFO {
    public IntPtr hFile;
    public IntPtr lpBaseOfDll;
    public uint dwDebugInfoFileOffset;
    public uint nDebugInfoSize;
    public IntPtr lpImageName;
    public ushort fUnicode;
  }
}