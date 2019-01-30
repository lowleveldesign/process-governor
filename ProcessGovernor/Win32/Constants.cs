namespace VsChromium.Core.Win32
{
    public static class Constants
    {
        public const uint INFINITE = 0xFFFFFFFF;
    }
    
    public static class WaitConstants
    {
        // from https://docs.microsoft.com/en-us/windows/desktop/api/synchapi/nf-synchapi-waitforsingleobject
        public const uint WAIT_ABANDONED = 0x00000080;
        public const uint WAIT_OBJECT_0 = 0x0;
        public const uint WAIT_TIMEOUT = 0x00000102;
        public const uint WAIT_FAILED = 0xFFFFFFFF;
    }
}