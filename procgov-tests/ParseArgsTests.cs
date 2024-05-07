using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace ProcessGovernor.Tests;

[TestFixture]
public class ParseArgsTests
{
    [Test]
    public void ExecutionModeTest()
    {
        // FIXME: test various execution modes
    }

    [Test]
    public void WorkingSetLimitsTest()
    {
        if (Program.ParseArgs(["--minws=1M --maxws=100M test.exe"], out _) is LaunchProcess
            {
                JobSettings: { MinWorkingSetSize: var minws, MaxWorkingSetSize: var maxws }
            })
        {
            Assert.That((minws, maxws), Is.EqualTo((1024 * 1024, 100 * 1024 * 1024)));
        }
        else { Assert.Fail(); }

        Assert.That(Program.ParseArgs(["--minws=1 test.exe"], out _),
            Throws.ArgumentException.With.Message.EqualTo("minws and maxws must be set together and be greater than 0."));
        Assert.That(Program.ParseArgs(["--maxws=1 test.exe"], out _),
            Throws.ArgumentException.With.Message.EqualTo("minws and maxws must be set together and be greater than 0."));
        Assert.That(Program.ParseArgs(["--minws=0 --maxws=10M test.exe"], out _),
            Throws.ArgumentException.With.Message.EqualTo("minws and maxws must be set together and be greater than 0."));
    }

    [Test]
    public void AffinityMaskFromCpuCountTest()
    {
        Assert.That(Program.ParseArgs(["-c 1 test.exe"], out _) is LaunchProcess
        {
            JobSettings.CpuAffinityMask: var am1
        } ? am1 : 0, Is.EqualTo(0x1UL));

        Assert.That(Program.ParseArgs(["--cpu=2 test.exe"], out _) is LaunchProcess
        {
            JobSettings.CpuAffinityMask: var am2
        } ? am2 : 0, Is.EqualTo(0x3UL));

        Assert.That(Program.ParseArgs(["--cpu 4 test.exe"], out _) is LaunchProcess
        {
            JobSettings.CpuAffinityMask: var am4
        } ? am4 : 0, Is.EqualTo(0xfUL));

        Assert.That(Program.ParseArgs(["--cpu=9 test.exe"], out _) is LaunchProcess
        {
            JobSettings.CpuAffinityMask: var am9
        } ? am9 : 0, Is.EqualTo(0x1ffUL));

        Assert.That(Program.ParseArgs(["--cpu=64 test.exe"], out _) is LaunchProcess
        {
            JobSettings.CpuAffinityMask: var am64
        } ? am64 : 0, Is.EqualTo(0xffffffffffffffffUL));
    }

    [Test]
    public void MemoryStringTest()
    {
        Assert.That(Program.ParseArgs(["-m=2K test.exe"], out _) is LaunchProcess
        {
            JobSettings.MaxProcessMemory: var m2k
        } ? m2k : 0, Is.EqualTo(2 * 1024u));

        Assert.That(Program.ParseArgs(["--maxmem=3M test.exe"], out _) is LaunchProcess
        {
            JobSettings.MaxProcessMemory: var m3m
        } ? m3m : 0, Is.EqualTo(3 * 1024u * 1024u));

        Assert.That(Program.ParseArgs(["-maxjobmem 3G test.exe"], out _) is LaunchProcess
        {
            JobSettings.MaxProcessMemory: var m3g
        } ? m3g : 0, Is.EqualTo(3 * 1024u * 1024u * 1024u));
    }

    [Test]
    public void TimeStringToMillisecondsTest()
    {
        Assert.That(Program.ParseArgs(["--process-utime=10 test.exe"], out _) is LaunchProcess
        {
            JobSettings.ProcessUserTimeLimitInMilliseconds: var t10
        } ? t10 : 0, Is.EqualTo(10u));

        Assert.That(Program.ParseArgs(["--job-utime 10ms test.exe"], out _) is LaunchProcess
        {
            JobSettings.JobUserTimeLimitInMilliseconds: var t10ms
        } ? t10ms : 0, Is.EqualTo(10u));

        Assert.That(Program.ParseArgs(["--job-utime=10s test.exe"], out _) is LaunchProcess
        {
            JobSettings.JobUserTimeLimitInMilliseconds: var t10s
        } ? t10s : 0, Is.EqualTo(10000u));

        Assert.That(Program.ParseArgs(["-t=10m test.exe"], out _) is LaunchProcess
        {
            JobSettings.ClockTimeLimitInMilliseconds: var t10m
        } ? t10m : 0, Is.EqualTo(600000u));

        Assert.That(Program.ParseArgs(["--timeout=10h test.exe"], out _) is LaunchProcess
        {
            JobSettings.ClockTimeLimitInMilliseconds: var t10h
        } ? t10h : 0, Is.EqualTo(36000000u));

        Assert.That(Program.ParseArgs(["--timeout=sdfms test.exe"], out _), 
            Throws.ArgumentException.With.Message.EqualTo("invalid number in one of the constraints"));
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

    // FIXME: to replace by config
    //[Test]
    //public void PrepareDebuggerCommandStringTest()
    //{
    //    var session = new JobSettings()
    //    {
    //        CpuAffinityMask = 0x2,
    //        MaxProcessMemory = 1024 * 1024,
    //        MaxJobMemory = 2048 * 1024,
    //        ProcessUserTimeLimitInMilliseconds = 500,
    //        JobUserTimeLimitInMilliseconds = 1000,
    //        ClockTimeLimitInMilliseconds = 2000,
    //        CpuMaxRate = 90,
    //        MaxBandwidth = 100,
    //        MinWorkingSetSize = 1024,
    //        MaxWorkingSetSize = 1024 * 1024,
    //        NumaNode = 1,
    //        Privileges = ["SeDebugPrivilege", "SeShutdownPrivilege"],
    //        PropagateOnChildProcesses = true,
    //        SpawnNewConsoleWindow = true
    //    };
    //    session.AdditionalEnvironmentVars.Add("TEST", "TESTVAL");
    //    session.AdditionalEnvironmentVars.Add("TEST2", "TESTVAL2");

    //    var appImageExe = Path.GetFileName(@"C:\temp\test.exe");
    //    var debugger = Program.PrepareDebuggerCommandString(session, appImageExe, true);

    //    var envFilePath = Program.GetAppEnvironmentFilePath(appImageExe);
    //    Assert.That(File.Exists(envFilePath), Is.True);

    //    try
    //    {

    //        var txt = File.ReadAllText(envFilePath);
    //        Assert.That(txt, Is.EqualTo("TEST=TESTVAL\r\nTEST2=TESTVAL2\r\n"));

    //        var expectedCmdLine =
    //            $"\"{Environment.GetCommandLineArgs()[0]}\" --nogui --debugger --env=\"{envFilePath}\" --cpu=0x2 --maxmem=1048576 " +
    //            "--maxjobmem=2097152 --maxws=1048576 --minws=1024 --node=1 --cpurate=90 --bandwidth=100 --recursive " +
    //            "--timeout=2000 --process-utime=500 --job-utime=1000 --enable-privileges=SeDebugPrivilege,SeShutdownPrivilege --nowait";

    //        Assert.That(debugger, Is.EqualTo(expectedCmdLine));
    //    }
    //    finally
    //    {
    //        File.Delete(envFilePath);
    //    }
    //}
}
