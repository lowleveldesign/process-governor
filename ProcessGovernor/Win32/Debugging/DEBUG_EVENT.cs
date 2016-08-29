using System;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Debugging {
  [StructLayout(LayoutKind.Sequential)]
  public struct DEBUG_EVENT {
    public DEBUG_EVENT_CODE dwDebugEventCode;
    public int dwProcessId;
    public int dwThreadId;

    // Note: We need 164 bytes because:
    // * On x86, offset is 12 and size is 84
    // * On x64, offset is 16 and size is 160 (so we skip first 4 bytes).
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 164, ArraySubType = UnmanagedType.U1)]
    public byte[] debugInfo;

    public EXCEPTION_DEBUG_INFO Exception {
      get { return GetDebugInfo<EXCEPTION_DEBUG_INFO>(); }
    }

    public CREATE_THREAD_DEBUG_INFO CreateThread {
      get { return GetDebugInfo<CREATE_THREAD_DEBUG_INFO>(); }
    }

    public CREATE_PROCESS_DEBUG_INFO CreateProcessInfo {
      get { return GetDebugInfo<CREATE_PROCESS_DEBUG_INFO>(); }
    }

    public EXIT_THREAD_DEBUG_INFO ExitThread {
      get { return GetDebugInfo<EXIT_THREAD_DEBUG_INFO>(); }
    }

    public EXIT_PROCESS_DEBUG_INFO ExitProcess {
      get { return GetDebugInfo<EXIT_PROCESS_DEBUG_INFO>(); }
    }

    public LOAD_DLL_DEBUG_INFO LoadDll {
      get { return GetDebugInfo<LOAD_DLL_DEBUG_INFO>(); }
    }

    public UNLOAD_DLL_DEBUG_INFO UnloadDll {
      get { return GetDebugInfo<UNLOAD_DLL_DEBUG_INFO>(); }
    }

    public OUTPUT_DEBUG_STRING_INFO DebugString {
      get { return GetDebugInfo<OUTPUT_DEBUG_STRING_INFO>(); }
    }

    public RIP_INFO RipInfo {
      get { return GetDebugInfo<RIP_INFO>(); }
    }

    private T GetDebugInfo<T>() where T : struct {
      var structSize = Marshal.SizeOf(typeof(T));
      var pointer = Marshal.AllocHGlobal(structSize);
      try {
        // See note above: offset of union is 4 on x64.
        int offset = IntPtr.Size == 4 ? 0 : 4;
        Marshal.Copy(debugInfo, offset, pointer, structSize);

        var result = Marshal.PtrToStructure(pointer, typeof(T));
        return (T)result;
      }
      finally {
        Marshal.FreeHGlobal(pointer);
      }
    }
  }
}