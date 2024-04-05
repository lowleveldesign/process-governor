using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace ProcessGovernor.Tests;

[TestFixture]
public class UnitTests
{
    [Test]
    public void CalculateAffinityMaskFromCpuCountTest()
    {
        Assert.That(Program.CalculateAffinityMaskFromCpuCount(1), Is.EqualTo(0x1UL));
        Assert.That(Program.CalculateAffinityMaskFromCpuCount(2), Is.EqualTo(0x3UL));
        Assert.That(Program.CalculateAffinityMaskFromCpuCount(4), Is.EqualTo(0xfUL));
        Assert.That(Program.CalculateAffinityMaskFromCpuCount(9), Is.EqualTo(0x1ffUL));
        Assert.That(Program.CalculateAffinityMaskFromCpuCount(64), Is.EqualTo(0xffffffffffffffffUL));
    }

    [Test]
    public void LoadCustomEnvironmentVariablesTest()
    {
        var envVarsFile = Path.GetTempFileName();
        try
        {
            using (var writer = new StreamWriter(envVarsFile, false))
            {
                writer.WriteLine("TEST=TESTVAL");
                writer.WriteLine("  TEST2 = TEST VAL2  ");
            }

            var session = new SessionSettings();
            Program.LoadCustomEnvironmentVariables(session, envVarsFile);
            Assert.That(session.AdditionalEnvironmentVars, Is.EquivalentTo(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "TEST", "TESTVAL" },
                { "TEST2", "TEST VAL2" }
            }));

            using (var writer = new StreamWriter(envVarsFile, false))
            {
                writer.WriteLine("  = TEST VAL2  ");
            }

            Assert.Throws<ArgumentException>(() =>
            {
                Program.LoadCustomEnvironmentVariables(session, envVarsFile);
            });
        }
        finally
        {
            if (File.Exists(envVarsFile))
            {
                File.Delete(envVarsFile);
            }
        }
    }

    [Test]
    public void ParseMemoryStringTest()
    {
        Assert.That(Program.ParseMemoryString("2K"), Is.EqualTo(2 * 1024u));
        Assert.That(Program.ParseMemoryString("3M"), Is.EqualTo(3 * 1024u * 1024u));
        Assert.That(Program.ParseMemoryString("3G"), Is.EqualTo(3 * 1024u * 1024u * 1024u));
    }

    [Test]
    public void ParseTimeStringToMillisecondsTest()
    {
        Assert.That(Program.ParseTimeStringToMilliseconds("10"), Is.EqualTo(10u));
        Assert.That(Program.ParseTimeStringToMilliseconds("10ms"), Is.EqualTo(10u));
        Assert.That(Program.ParseTimeStringToMilliseconds("10s"), Is.EqualTo(10000u));
        Assert.That(Program.ParseTimeStringToMilliseconds("10m"), Is.EqualTo(600000u));
        Assert.That(Program.ParseTimeStringToMilliseconds("10h"), Is.EqualTo(36000000u));
        Assert.Throws<FormatException>(() => Program.ParseTimeStringToMilliseconds("sdfms"));
    }

    [Test]
    public void PrepareDebuggerCommandStringTest()
    {
        var session = new SessionSettings()
        {
            CpuAffinityMask = 0x2,
            MaxProcessMemory = 1024 * 1024,
            MaxJobMemory = 2048 * 1024,
            ProcessUserTimeLimitInMilliseconds = 500,
            JobUserTimeLimitInMilliseconds = 1000,
            ClockTimeLimitInMilliseconds = 2000,
            CpuMaxRate = 90,
            MaxBandwidth = 100,
            MinWorkingSetSize = 1024,
            MaxWorkingSetSize = 1024 * 1024,
            NumaNode = 1,
            Privileges = ["SeDebugPrivilege", "SeShutdownPrivilege"],
            PropagateOnChildProcesses = true,
            SpawnNewConsoleWindow = true
        };
        session.AdditionalEnvironmentVars.Add("TEST", "TESTVAL");
        session.AdditionalEnvironmentVars.Add("TEST2", "TESTVAL2");

        var appImageExe = Path.GetFileName(@"C:\temp\test.exe");
        var debugger = Program.PrepareDebuggerCommandString(session, appImageExe, true);

        var envFilePath = Program.GetAppEnvironmentFilePath(appImageExe);
        Assert.That(File.Exists(envFilePath), Is.True);

        try
        {

            var txt = File.ReadAllText(envFilePath);
            Assert.That(txt, Is.EqualTo("TEST=TESTVAL\r\nTEST2=TESTVAL2\r\n"));

            var expectedCmdLine =
                $"\"{Environment.GetCommandLineArgs()[0]}\" --nogui --debugger --env=\"{envFilePath}\" --cpu=0x2 --maxmem=1048576 " +
                "--maxjobmem=2097152 --maxws=1048576 --minws=1024 --node=1 --cpurate=90 --bandwidth=100 --recursive " +
                "--timeout=2000 --process-utime=500 --job-utime=1000 --enable-privileges=SeDebugPrivilege,SeShutdownPrivilege --nowait";

            Assert.That(debugger, Is.EqualTo(expectedCmdLine));
        }
        finally
        {
            File.Delete(envFilePath);
        }
    }
}
