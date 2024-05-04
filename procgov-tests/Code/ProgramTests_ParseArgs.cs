using System;
using System.Collections.Generic;
using System.IO;

namespace ProcessGovernor.Tests.Code;

using static SharedApi;

public static partial class ProgramTests
{
    static readonly SystemInfo TestSystemInfo2Numas4Groups = new(
        NumaNodes: [
            new(0, [new(0, 0xF), new(1, 0x7)]),
            new(1, [new(2, 0xF), new(3, 0x7)])
        ],
        ProcessorGroups: [
            new(0, 0xF), new(1, 0x7), new(2, 0xF), new(3, 0x7)
        ],
        CpuCores: [
            new(true, new(0, 0x1)), new(true, new(0, 0x2)), new(true, new(0, 0x4)), new(true, new(0, 0x8)),
            new(true, new(1, 0x1)), new(true, new(1, 0x2)), new(true, new(1, 0x4)),
            new(true, new(2, 0x1)), new(true, new(2, 0x2)), new(true, new(2, 0x4)), new(true, new(2, 0x8)),
            new(true, new(3, 0x1)), new(true, new(3, 0x2)), new(true, new(3, 0x4)),
    ]);

    static readonly SystemInfo TestSystemInfo2Numas1Group = new(
        NumaNodes: [
            new(0, [new(0, 0x007F)]),
            new(1, [new(0, 0x3F80)])
        ],
        ProcessorGroups: [
            new(0, 0x3FFF)
        ],
        CpuCores: [
            new(true, new(0, 0x0001)), new(true, new(0, 0x0002)), new(true, new(0, 0x0004)), new(true, new(0, 0x0008)),
            new(true, new(0, 0x0010)), new(true, new(0, 0x0020)), new(true, new(0, 0x0040)), new(true, new(0, 0x0080)), 
            new(true, new(0, 0x0100)), new(true, new(0, 0x0200)), new(true, new(0, 0x0400)), new(true, new(0, 0x0800)),
            new(true, new(0, 0x1000)), new(true, new(3, 0x2000)),
        ]
    );

    static ProgramTests()
    {
        ProcessGovernorTestContext.Initialize();
    }

    [Test]
    public static void ParseArgsExecutionMode()
    {
        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "test.exe", "-c", "-m=123"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                Assert.That(maxMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(target is LaunchProcess { Procargs: var procargs } ? procargs : null,
                    Is.EquivalentTo(new[] { "test.exe", "-c", "-m=123" }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p", "1001", "-p=1002", "--pid=1003", "--pid", "1004"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                Assert.That(maxMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null,
                    Is.EqualTo(new[] { 1001, 1002, 1003, 1004 }));
                break;
            default:
                Assert.Fail();
                break;
        }


        // legacy, undocumented behavior
        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p=1001,1002", "--pid=1003,1004"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                Assert.That(maxMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null,
                    Is.EqualTo(new[] { 1001, 1002, 1003, 1004 }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p", "1001", "-p=1001"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                Assert.That(maxMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null,
                    Is.EqualTo(new[] { 1001 }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p", "1001", "-p=1001", "--pid=1001", "--pid", "1001"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                Assert.That(maxMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null,
                    Is.EqualTo(new[] { 1001 }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p", "1001", "test.exe", "-c"]))
        {
            case ShowHelpAndExit { ErrorMessage: var err }:
                Assert.That(err, Is.EqualTo("invalid arguments provided"));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--monitor"]))
        {
            case RunAsMonitor:
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--service"]))
        {
            case RunAsService:
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--install", "-m=10M", "-p", "1001", "-p=1001"]))
        {
            case ShowHelpAndExit { ErrorMessage: var err }:
                Assert.That(err, Is.EqualTo("invalid arguments provided"));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--install", "--service-username=testu", "--service-password=testp",
            "--service-path=C:\\test test", "-m=10M", "test.exe"]))
        {
            case SetupProcessGovernance procgov:
                Assert.That(procgov.JobSettings.MaxProcessMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(procgov.ExecutablePath, Is.EqualTo("test.exe"));
                Assert.That(procgov.ServiceUserName, Is.EqualTo("testu"));
                Assert.That(procgov.ServiceUserPassword, Is.EqualTo("testp"));
                Assert.That(procgov.ServiceInstallPath, Is.EqualTo("C:\\test test"));
                break;
            case ShowHelpAndExit err:
                Assert.Fail($"Error: {err.ErrorMessage}");
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--install", "--service-username=testu", "--service-username=testu2", "-m=10M", "test.exe"]))
        {
            case SetupProcessGovernance procgov:
                Assert.That(procgov.JobSettings.MaxProcessMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(procgov.ExecutablePath, Is.EqualTo("test.exe"));
                Assert.That(procgov.ServiceUserName, Is.EqualTo("testu2"));
                Assert.That(procgov.ServiceUserPassword, Is.Null);
                break;
            case ShowHelpAndExit err:
                Assert.Fail($"Error: {err.ErrorMessage}");
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--install", "-m=10M", "test.exe"]))
        {
            case SetupProcessGovernance procgov:
                Assert.That(procgov.JobSettings.MaxProcessMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(procgov.ExecutablePath, Is.EqualTo("test.exe"));
                Assert.That(procgov.ServiceUserName, Is.EqualTo("NT AUTHORITY\\SYSTEM"));
                Assert.That(procgov.ServiceUserPassword, Is.Null);
                Assert.That(procgov.ServiceInstallPath, Is.EqualTo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.ServiceName)));
                break;
            case ShowHelpAndExit err:
                Assert.Fail($"Error: {err.ErrorMessage}");
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--uninstall"]))
        {
            case ShowHelpAndExit { ErrorMessage: var err }:
                Assert.That(err, Is.EqualTo("invalid arguments provided"));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--uninstall", @"C:\\temp\\test.exe"]))
        {
            case RemoveProcessGovernance { ExecutablePath: var exePath }:
                Assert.That(exePath, Is.SamePath(@"C:\\temp\\test.exe"));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--uninstall-all"]))
        {
            case RemoveAllProcessGovernance:
                Assert.Pass();
                break;
            default:
                Assert.Fail();
                break;
        }
    }

    [Test]
    public static void ParseArgsWorkingSetLimits()
    {
        if (Program.ParseArgs(RealSystemInfo, ["--minws=1M", "--maxws=100M", "test.exe"]) is RunAsCmdApp
            {
                JobSettings: { MinWorkingSetSize: var minws, MaxWorkingSetSize: var maxws }
            })
        {
            Assert.That((minws, maxws), Is.EqualTo((1024 * 1024, 100 * 1024 * 1024)));
        }
        else { Assert.Fail(); }

        Assert.That(Program.ParseArgs(RealSystemInfo, ["--minws=1", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "minws and maxws must be set together and be greater than 0"
        });
        Assert.That(Program.ParseArgs(RealSystemInfo, ["--maxws=1", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "minws and maxws must be set together and be greater than 0"
        });
        Assert.That(Program.ParseArgs(RealSystemInfo, ["--minws=0", "--maxws=10M", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "minws and maxws must be set together and be greater than 0"
        });
    }

    [Test]
    public static void ParseArgsAffinityMaskFromCpuCount()
    {
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: null }:
                Assert.Pass();
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "1", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0x1) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["--cpu=2", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0x3) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["--cpu", "4", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0xF) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["--cpu=9", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0xF), new(1, 0x7), new(2, 0x3) }));
                break;
            default:
                Assert.Fail();
                break;
        }
        
        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["--cpu=9", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0x1FF) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        Assert.That(Program.ParseArgs(TestSystemInfo2Numas4Groups, ["--cpu=64", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: var err
        } ? err : null, Is.EqualTo($"you can't set affinity to more than {TestSystemInfo2Numas4Groups.CpuCores.Length} CPU cores"));
    }

    [Test]
    public static void ParseArgsAffinityMaskWithNumaNode()
    {
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0xF), new(1, 0x7) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "n0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0x7F) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0:g1", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(1, 0x7) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "n0:g0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0x7F) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0:g1:0xF", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(1, 0x7) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "n0:g0:0xF", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0xF) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0:g0", "-c", "n1", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0xF), new(2, 0xF), new(3, 0x7) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "n0:g0", "-c", "n1:g0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0x3FFF) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        Assert.That(Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0", "-c", "n1:g0", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "processor group 0 does not belong to NUMA node 1"
        });

        Assert.That(Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n4", "-c", "n1:g0", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "invalid affinity string: 'n4'"
        });
    }

    [Test]
    public static void ParseArgsAffinityMaskWithProcessorGroup()
    {
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "g0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0xF) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "g0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0x3FFF) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "g0:0x3", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0x3) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "g0:0x3", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0x3) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "g1:0xF", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(1, 0x7) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "g0", "-c", "g1", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                Assert.That(aff, Is.EquivalentTo(new GroupAffinity[] { new(0, 0xF), new(1, 0x7) }));
                break;
            default:
                Assert.Fail();
                break;
        }

        Assert.That(Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0:g0", "-c", "g5:0x1", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "invalid affinity string: 'g5:0x1'"
        });
    }

    [Test]
    public static void ParseArgsCpuMaxRate()
    {
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-e", "20", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuMaxRate: var rate }:
                Assert.That(rate, Is.EqualTo(20 * 100));
                break;
            default:
                Assert.Fail();
                break;
        }

        var cpuCoresNumber = TestSystemInfo2Numas4Groups.CpuCores.Length;
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "1", "-e", "20", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuMaxRate: var rate }:
                Assert.That(rate, Is.EqualTo(20 * 100 / (cpuCoresNumber / 1)));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "4", "-e", "20", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuMaxRate: var rate }:
                Assert.That(rate, Is.EqualTo(20 * 100 / (cpuCoresNumber / 4)));
                break;
            default:
                Assert.Fail();
                break;
        }
    }

    [Test]
    public static void ParseArgsMemoryString()
    {
        switch (Program.ParseArgs(RealSystemInfo, ["-m=2K", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.MaxProcessMemory: var m }:
                Assert.That(m, Is.EqualTo(2 * 1024u));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--maxmem=3M", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.MaxProcessMemory: var m }:
                Assert.That(m, Is.EqualTo(3 * 1024u * 1024u));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-maxjobmem", "3G", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.MaxJobMemory: var m }:
                Assert.That(m, Is.EqualTo(3 * 1024u * 1024u * 1024u));
                break;
            default:
                Assert.Fail();
                break;
        }

    }

    [Test]
    public static void ParseArgsTime()
    {

        switch (Program.ParseArgs(RealSystemInfo, ["--process-utime=10", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.ProcessUserTimeLimitInMilliseconds: var t }:
                Assert.That(t, Is.EqualTo(10u));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--job-utime", "10ms", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.JobUserTimeLimitInMilliseconds: var t }:
                Assert.That(t, Is.EqualTo(10u));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--job-utime=10s", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.JobUserTimeLimitInMilliseconds: var t }:
                Assert.That(t, Is.EqualTo(10000u));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-t=10m", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.ClockTimeLimitInMilliseconds: var t }:
                Assert.That(t, Is.EqualTo(600000u));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--timeout=10h", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.ClockTimeLimitInMilliseconds: var t }:
                Assert.That(t, Is.EqualTo(36000000u));
                break;
            default:
                Assert.Fail();
                break;
        }

        Assert.That(Program.ParseArgs(RealSystemInfo, ["--timeout=sdfms", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "invalid number in one of the constraints"
        });

        Assert.That(Program.ParseArgs(RealSystemInfo, ["--timeout=10h", "--nowait", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--nowait cannot be used with --timeout"
        });
    }

    [Test]
    public static void ParseArgsCustomEnvironmentVariables()
    {
        var envVarsFile = Path.GetTempFileName();
        try
        {
            using (var writer = new StreamWriter(envVarsFile, false))
            {
                writer.WriteLine("TEST=TESTVAL");
                writer.WriteLine("  TEST2 = TEST VAL2  ");
            }

            Assert.That(Program.ParseArgs(RealSystemInfo, [$"--env=\"{envVarsFile}\"", "test.exe"]) is RunAsCmdApp
            {
                Environment: var env
            } ? env : default, Is.EquivalentTo(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "TEST", "TESTVAL" },
                { "TEST2", "TEST VAL2" }
            }));

            using (var writer = new StreamWriter(envVarsFile, false))
            {
                writer.WriteLine("  = TEST VAL2  ");
            }

            Assert.That(Program.ParseArgs(RealSystemInfo, [$"--env=\"{envVarsFile}\"", "test.exe"]) is ShowHelpAndExit
            {
                ErrorMessage: "the environment file contains invalid data (line: 1)"
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
    public static void ParseArgsEnablePrivileges()
    {
        switch (Program.ParseArgs(RealSystemInfo, ["--enable-privilege=TEST1", "--enable-privilege=TEST2", "test.exe"]))
        {
            case RunAsCmdApp { Privileges: var privs }:
                Assert.That(privs, Is.EquivalentTo(new[] { "TEST1", "TEST2" }));
                break;
            default:
                Assert.Fail();
                break;
        }

        // legacy, undocumented behavior
        switch (Program.ParseArgs(RealSystemInfo, ["--enable-privileges=TEST1,TEST2", "test.exe"]))
        {
            case RunAsCmdApp { Privileges: var privs }:
                Assert.That(privs, Is.EquivalentTo(new[] { "TEST1", "TEST2" }));
                break;
            default:
                Assert.Fail();
                break;
        }
    }

    [Test]
    public static void ParseArgsPriorityClass()
    {
        var priorityClassNamesMap = new Dictionary<string, PriorityClass>()
        {
            ["Idle"] = PriorityClass.Idle,
            ["Normal"] = PriorityClass.Normal,
            ["AboveNormal"] = PriorityClass.AboveNormal,
            ["BelowNormal"] = PriorityClass.BelowNormal,
            ["High"] = PriorityClass.High,
            ["Realtime"] = PriorityClass.Realtime,
        };

        foreach (var (exactName, pc) in priorityClassNamesMap)
        {
            foreach (var name in new[] { exactName, exactName.ToLowerInvariant() })
            {
                if (Program.ParseArgs(RealSystemInfo, [$"--priority={name}", "test.exe"]) is RunAsCmdApp
                    {
                        JobSettings.PriorityClass: var parsedPriorityClass
                    })
                {
                    Assert.That(parsedPriorityClass, Is.EqualTo(pc));
                }
                else { Assert.Fail($"Failed parsing of priority {name}"); }
            }
        }
    }

    [Test]
    public static void ParseEmptyArgs()
    {
        Assert.That(Program.ParseArgs(RealSystemInfo, []) is ShowSystemInfoAndExit);
    }

    [Test]
    public static void ParseArgsUnknownArgument()
    {
        Assert.That(Program.ParseArgs(RealSystemInfo, ["-c", "1", "--maxmem", "100M", "--unknown", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: var err
        } ? err : "", Is.EqualTo("unrecognized arguments: unknown"));


        // Numa should be passed through affinity
        Assert.That(Program.ParseArgs(RealSystemInfo, ["--numa 2", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: var err2
        } ? err2 : "", Is.EqualTo("unrecognized arguments: numa 2"));
    }

    [Test]
    public static void ParseArgsExitBehaviorArguments()
    {
        Assert.That(Program.ParseArgs(RealSystemInfo, ["-m=10M", "test.exe"]) is RunAsCmdApp
        {
            ExitBehavior: ExitBehavior.WaitForJobCompletion,
            LaunchConfig: LaunchConfig.Default
        });

        Assert.That(Program.ParseArgs(RealSystemInfo, ["-m=10M", "-q", "--nowait", "test.exe"]) is RunAsCmdApp
        {
            ExitBehavior: ExitBehavior.DontWaitForJobCompletion,
            LaunchConfig: LaunchConfig.Quiet
        });


        Assert.That(Program.ParseArgs(RealSystemInfo, ["-m=10M", "--nogui", "--nomonitor", "--terminate-job-on-exit", "test.exe"]) is RunAsCmdApp
        {
            ExitBehavior: ExitBehavior.TerminateJobOnExit,
            LaunchConfig: LaunchConfig.NoGui | LaunchConfig.NoMonitor
        });

        Assert.That(Program.ParseArgs(RealSystemInfo, ["-m=10M", "--terminate-job-on-exit", "--nowait", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--terminate-job-on-exit and --nowait cannot be used together"
        });
    }
}
