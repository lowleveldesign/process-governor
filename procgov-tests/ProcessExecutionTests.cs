using System.ComponentModel;
using System.Threading;

namespace ProcessGovernor.Tests;

[TestFixture]
public static class ProcessExecutionTests
{
    [Test]
    public static void ProcessStartFailureTest()
    {
        var exception = Assert.Catch<Win32Exception>(() =>
        {
            Program.Execute(new LaunchProcess(new JobSettings(), ["____wrong-executable.exe"],
                false, [], [], false, false, ExitBehavior.WaitForJobCompletion), CancellationToken.None);
        });
        Assert.That(exception?.NativeErrorCode, Is.EqualTo(2));
    }

    [Test]
    public static void ProcessExitCodeForwardingTest()
    {
        var exitCode = Program.Execute(new LaunchProcess(new JobSettings(), ["cmd.exe", "/c", "exit 5"],
                false, [], [], false, false, ExitBehavior.WaitForJobCompletion), CancellationToken.None);
        Assert.That(exitCode, Is.EqualTo(5));
    }

    // FIXME: tests for affinity, environment variables, priority, etc.
}
