using System;
using System.Runtime.InteropServices;

namespace LowLevelDesign.Win32.NUMA
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GROUP_AFFINITY
    {
        public UIntPtr Mask;
        public UInt16 Group;
        private UInt16 Reserved0;
        private UInt16 Reserved1;
        private UInt16 Reserved2;
    }
}
