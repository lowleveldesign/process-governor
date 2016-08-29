using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Debugging {
  [StructLayout(LayoutKind.Sequential)]
  public struct RIP_INFO {
    public uint dwError;
    public uint dwType;
  }
}