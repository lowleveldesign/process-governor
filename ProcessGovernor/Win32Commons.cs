using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace LowLevelDesign
{
    internal static class Win32Commons
    {
        public static T CheckWin32Result<T>(T result)
        {
            return result switch {
                SafeHandle handle when !handle.IsInvalid => result,
                HANDLE handle when Constants.INVALID_HANDLE_VALUE != handle => result,
                uint n when n != 0xffffffff => result,
                bool b when b => result,
                _ => throw new Win32Exception()
            };
        }
    }
}
