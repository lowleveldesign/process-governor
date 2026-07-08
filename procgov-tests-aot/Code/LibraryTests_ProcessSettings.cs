using ProcessGovernor.Library;
using System.Diagnostics;

namespace ProcessGovernor.Tests.Code;

public partial class LibraryTests
{
    [Test]
    [MatrixDataSource]
    public async Task CmdAppUpdateProcessEnvironmentVariables(
        [Matrix(Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86)] Environment.SpecialFolder systemFolder)
    {
        Environment.SetEnvironmentVariable("TESTEMPTY", "SOMETHING");

        string executablePath = Path.Combine(Environment.GetFolderPath(systemFolder), "winver.exe");
        using var proc = Process.Start(executablePath);

        await Task.Delay(1000);

        try
        {
            Dictionary<string, string> configuredEnvVars = new()
            {
                ["TESTVAR1"] = "TESTVAR1_VAL;%USERPROFILE%",
                ["TESTVAR2"] = "TESTVAR2_VAL",
                ["TESTEMPTY"] = ""
            };


            Dictionary<string, string?> expectedEnvVars = new()
            {
                ["TESTVAR1"] = Environment.ExpandEnvironmentVariables(configuredEnvVars["TESTVAR1"]!),
                ["TESTVAR2"] = "TESTVAR2_VAL",
                ["TESTEMPTY"] = null
            };

            Win32ProcessModule.SetProcessEnvironmentVariables(proc.SafeHandle, configuredEnvVars);

            foreach (var (k, v) in expectedEnvVars)
            {
                var actualEnvValue = Win32ProcessModule.GetProcessEnvironmentVariable(proc.SafeHandle, k);

                await Assert.That(actualEnvValue).IsEqualTo(v);
            }
        }
        finally
        {
            proc.CloseMainWindow();

            if (!proc.WaitForExit(2000))
            {
                proc.Kill();
            }
        }
    }
}
