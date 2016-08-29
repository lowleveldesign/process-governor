using System;
using System.Runtime.InteropServices;

namespace VsChromium.Core.Win32.Debugging
{
    [StructLayout(LayoutKind.Sequential)]
    public struct EXCEPTION_RECORD
    {
        public UInt32 ExceptionCode;
        public UInt32 ExceptionFlags;
        public IntPtr ExceptionRecord;
        public IntPtr ExceptionAddress;
        public UInt32 NumberParameters;
#if X64
        public UInt32 __unusedAlignment;
#endif
#if X64
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15, ArraySubType = UnmanagedType.U8)]
        public UInt64[] ExceptionInformation;
#else
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15, ArraySubType = UnmanagedType.U4)]
        public uint[] ExceptionInformation;
#endif
    }
}