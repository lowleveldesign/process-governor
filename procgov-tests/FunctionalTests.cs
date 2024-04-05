using NUnit.Framework;
using System.ComponentModel;
using System.Threading;

namespace ProcessGovernor.Tests;

[TestFixture]
public class FunctionTests
{
    [Test]
    public void ProcessStartFailureTest()
    {
        var session = new SessionSettings();
        var exception = Assert.Catch<Win32Exception>(() =>
        {
            ProcessModule.StartProcessUnderDebuggerAndAssignToJobObject(["____wrong-executable.exe"], session);
        });
        Assert.That(exception?.NativeErrorCode, Is.EqualTo(2));
    }

    [Test]
    public void ProcessExitCodeForwardingTest()
    {
        var session = new SessionSettings();
        var job = ProcessModule.StartProcessAndAssignToJobObject(["cmd.exe", "/c", "exit 5"], session);
        Win32JobModule.WaitForTheJobToComplete(job, CancellationToken.None);
        var exitCode = ProcessModule.GetProcessExitCode(job.FirstProcessHandle!);
        Assert.That(exitCode, Is.EqualTo(5));
    }
}
