using ProcessGovernor.Library;
using ProcessGovernor.Tests.Code;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

[assembly: ExcludeFromCodeCoverage]
// Process Governor tests are often testing various process behaviour
// and were not designed to be run in parallel
[assembly: NotInParallel]

namespace ProcessGovernor.Tests;

public class GlobalHooks
{
    [Before(TestSession)]
    public static Task BeforeTestSession()
    {
        var debugTraceListener = new DefaultTraceListener() { TraceOutputOptions = TraceOptions.DateTime };
        var consoleTraceListener = new ConsoleTraceListener() { TraceOutputOptions = TraceOptions.DateTime };

        Program.Logger.Switch.Level = SourceLevels.Verbose;
        Program.Logger.Listeners.Clear();
        Program.Logger.Listeners.Add(debugTraceListener);
        Program.Logger.Listeners.Add(consoleTraceListener);

        ProcessGovernorLibraryApi.SetLibraryLoggerLevel(SourceLevels.Verbose);
        ProcessGovernorLibraryApi.SetLibraryLoggerListeners([debugTraceListener, consoleTraceListener]);

        // Runs once before all tests - e.g. start a test container, seed a database
        return Task.CompletedTask;
    }

    [After(TestSession)]
    public static Task AfterTestSession()
    {
        return Task.CompletedTask;
    }
}
