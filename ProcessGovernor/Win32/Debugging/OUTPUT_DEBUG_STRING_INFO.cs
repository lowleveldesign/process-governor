using System;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Debugging {
  [StructLayout(LayoutKind.Sequential)]
  public struct OUTPUT_DEBUG_STRING_INFO {
    /// <summary>
    /// The debugging string in the calling process's address space. The
    /// debugger can use the ReadProcessMemory function to retrieve the value of
    /// the string.
    /// </summary>
    public IntPtr lpDebugStringData;
    /// <summary>
    /// The format of the debugging string. If this member is zero, the debugging
    /// string is ANSI; if it is nonzero, the string is Unicode.
    /// </summary>
    public ushort fUnicode;
    /// <summary>
    /// The size of the debugging string, in characters. The length includes the
    /// string's terminating null character.
    /// </summary>
    public ushort nDebugStringLength;
  }
}