using System.ComponentModel;
using System.Threading;

namespace ProcessGovernor.Tests;

[TestFixture]
public class LaunchProcessTests
{
    [Test]
    public void ProcessStartFailureTest()
    {
        var exception = Assert.Catch<Win32Exception>(() =>
        {
            Program.Execute(new LaunchProcess(new JobSettings(), ["____wrong-executable.exe"],
                false, [], [], false, false, ExitBehavior.WaitForJobCompletion), CancellationToken.None);
        });
        Assert.That(exception?.NativeErrorCode, Is.EqualTo(2));
    }

    [Test]
    public void ProcessExitCodeForwardingTest()
    {
        var exitCode = Program.Execute(new LaunchProcess(new JobSettings(), ["cmd.exe", "/c", "exit 5"],
                false, [], [], false, false, ExitBehavior.WaitForJobCompletion), CancellationToken.None);
        Assert.That(exitCode, Is.EqualTo(5));
    }
}
