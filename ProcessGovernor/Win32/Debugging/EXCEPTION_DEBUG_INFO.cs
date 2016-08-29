using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Debugging {
  [StructLayout(LayoutKind.Sequential)]
  public struct EXCEPTION_DEBUG_INFO {
    public EXCEPTION_RECORD ExceptionRecord;
    public uint dwFirstChance;
  }
}