using System.Diagnostics;

namespace ProcessGovernor.Tests;

static class ProcessGovernorTestContext
{
    static ProcessGovernorTestContext()
    {
        Program.Logger.Switch.Level = SourceLevels.Verbose;
        Program.Logger.Listeners.Clear();
        Program.Logger.Listeners.Add(new TestTraceListener());
    }

    public static void Initialize() { }

    class TestTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            TestContext.Out.Write(message);
        }

        public override void WriteLine(string? message)
        {
            TestContext.Out.WriteLine(message);
        }
    }
}
