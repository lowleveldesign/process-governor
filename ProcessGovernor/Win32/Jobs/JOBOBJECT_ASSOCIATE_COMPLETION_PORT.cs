using System;
using System.Runtime.InteropServices;

namespace LowLevelDesign.Win32.Jobs
{
    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT
    {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }
}
