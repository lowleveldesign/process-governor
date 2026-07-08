using ProcessGovernor.Library;

namespace ProcessGovernor.Tests.Code;

using static SharedApi;

public partial class ProgramTests
{
    static readonly IEqualityComparer<uint> PidComparer = EqualityComparer<uint>.Default;
    static readonly IEqualityComparer<GroupAffinity> GroupAffinityComparer = EqualityComparer<GroupAffinity>.Default;


    [Test]
    public async Task ParseArgsExecutionMode()
    {
        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "test.exe", "-c", "-m=123"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                await Assert.That(maxMemory).IsEqualTo<ulong>(10 * 1024 * 1024);

                var args = target is LaunchProcess { Procargs: var procargs } ? procargs : null;
                await Assert.That(args).IsNotNull();
                await Assert.That(args).IsEquivalentTo(["test.exe", "-c", "-m=123"], EqualityComparer<string>.Default);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p", "1001", "-p=1002", "--pid=1003", "--pid", "1004"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                await Assert.That(maxMemory).IsEqualTo<ulong>(10 * 1024 * 1024);
                await Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null).IsEquivalentTo(
                    [1001u, 1002u, 1003u, 1004u], PidComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }


        // legacy, undocumented behavior
        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p=1001,1002", "--pid=1003,1004"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                await Assert.That(maxMemory).IsEqualTo<ulong>(10 * 1024 * 1024);
                await Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null).IsEquivalentTo(
                    [1001u, 1002u, 1003u, 1004u], PidComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p", "1001", "-p=1001"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                await Assert.That(maxMemory).IsEqualTo<ulong>(10 * 1024 * 1024);
                await Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null).IsEquivalentTo(
                    [1001u], PidComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p", "1001", "-p=1001", "--pid=1001", "--pid", "1001"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                await Assert.That(maxMemory).IsEqualTo<ulong>(10 * 1024 * 1024);
                await Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null).IsEquivalentTo(
                    [1001u], PidComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-m=10M", "-p", "1001", "test.exe", "-c"]))
        {
            case ShowHelpAndExit { ErrorMessage: var err }:
                await Assert.That(err).IsEqualTo("invalid arguments provided");
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--monitor"]))
        {
            case RunAsMonitor:
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--service"]))
        {
            case RunAsService:
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--install", "-m=10M", "-p", "1001", "-p=1001"]))
        {
            case ShowHelpAndExit { ErrorMessage: var err }:
                await Assert.That(err).IsEqualTo("invalid arguments provided");
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--install", "--service-username=testu", "--service-password=testp",
            "--service-path=C:\\test test", "-m=10M", "test.exe"]))
        {
            case SetupProcessGovernance procgov:
                await Assert.That(procgov.JobSettings.MaxProcessMemory).IsEqualTo(10ul * 1024 * 1024);
                await Assert.That(procgov.ExecutablePath).IsEqualTo("test.exe");
                await Assert.That(procgov.ServiceUserName).IsEqualTo("testu");
                await Assert.That(procgov.ServiceUserPassword).IsEqualTo("testp");
                await Assert.That(procgov.ServiceInstallPath).IsEqualTo("C:\\test test");
                break;
            case ShowHelpAndExit err:
                Assert.Fail($"Error: {err.ErrorMessage}");
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--install", "--service-username=testu", "--service-username=testu2", "-m=10M", "test.exe"]))
        {
            case SetupProcessGovernance procgov:
                await Assert.That(procgov.JobSettings.MaxProcessMemory).IsEqualTo(10ul * 1024 * 1024);
                await Assert.That(procgov.ExecutablePath).IsEqualTo("test.exe");
                await Assert.That(procgov.ServiceUserName).IsEqualTo("testu2");
                await Assert.That(procgov.ServiceUserPassword).IsNull();
                break;
            case ShowHelpAndExit err:
                Assert.Fail($"Error: {err.ErrorMessage}");
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--install", "-m=10M", "test.exe"]))
        {
            case SetupProcessGovernance procgov:
                await Assert.That(procgov.JobSettings.MaxProcessMemory).IsEqualTo(10ul * 1024 * 1024);
                await Assert.That(procgov.ExecutablePath).IsEqualTo("test.exe");
                await Assert.That(procgov.ServiceUserName).IsEqualTo("NT AUTHORITY\\SYSTEM");
                await Assert.That(procgov.ServiceUserPassword).IsNull();
                await Assert.That(procgov.ServiceInstallPath).IsEqualTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.ServiceName));
                break;
            case ShowHelpAndExit err:
                Assert.Fail($"Error: {err.ErrorMessage}");
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--uninstall"]))
        {
            case ShowHelpAndExit { ErrorMessage: var err }:
                await Assert.That(err).IsEqualTo("invalid arguments provided");
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--uninstall", @"C:\temp\test.exe"]))
        {
            case RemoveProcessGovernance { ExecutablePath: var exePath }:
                await Assert.That(exePath).IsEqualTo(@"C:\temp\test.exe");
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--uninstall-all"]))
        {
            case RemoveAllProcessGovernance:
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }
    }

    [Test]
    public async Task ParseArgsWorkingSetLimits()
    {
        if (Program.ParseArgs(RealSystemInfo, ["--minws=1M", "--maxws=100M", "test.exe"]) is RunAsCmdApp
            {
                JobSettings: { MinWorkingSetSize: var minws, MaxWorkingSetSize: var maxws }
            })
        {
            await Assert.That((minws, maxws)).IsEqualTo((1024u * 1024, 100u * 1024 * 1024));
        }
        else { Assert.Fail("invalid parse results"); }

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--minws=1", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "minws and maxws must be set together and be greater than 0"
        }).IsTrue();
        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--maxws=1", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "minws and maxws must be set together and be greater than 0"
        }).IsTrue();
        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--minws=0", "--maxws=10M", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "minws and maxws must be set together and be greater than 0"
        }).IsTrue();
    }

    [Test]
    public async Task ParseArgsAffinityMaskFromCpuCount()
    {
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["test.exe"]))
        {
            case RunAsCmdApp { JobSettings: null }:
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "1", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0x1)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["--cpu=2", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0x3)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["--cpu", "4", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0xF)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["--cpu=9", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0xF), new(1, 0x7), new(2, 0x3)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["--cpu=9", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0x1FF)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        await Assert.That(Program.ParseArgs(TestSystemInfo2Numas4Groups, ["--cpu=64", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: var err
        } ? err : null).IsEqualTo($"you can't set affinity to more than {TestSystemInfo2Numas4Groups.CpuCores.Length} CPU cores");
    }

    [Test]
    public async Task ParseArgsAffinityMaskWithNumaNode()
    {
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0xF), new(1, 0x7)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "n0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0x7F)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0:g1", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(1, 0x7)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "n0:g0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0x7F)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0:g1:0xF", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(1, 0x7)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "n0:g0:0xF", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0xF)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0:g0", "-c", "n1", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0xF), new(2, 0xF), new(3, 0x7)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "n0:g0", "-c", "n1:g0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0x3FFF)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        await Assert.That(Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0", "-c", "n1:g0", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "processor group 0 does not belong to NUMA node 1"
        }).IsTrue();

        await Assert.That(Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n4", "-c", "n1:g0", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "invalid affinity string: 'n4'"
        }).IsTrue();
    }

    [Test]
    public async Task ParseArgsAffinityMaskWithProcessorGroup()
    {
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "g0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0xF)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "g0", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0x3FFF)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "g0:0x3", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0x3)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas1Group, ["-c", "g0:0x3", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0x3)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "g1:0xF", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(1, 0x7)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "g0", "-c", "g1", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuAffinity: var aff }:
                await Assert.That(aff).IsEquivalentTo([new(0, 0xF), new(1, 0x7)], GroupAffinityComparer);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        await Assert.That(Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "n0:g0", "-c", "g5:0x1", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "invalid affinity string: 'g5:0x1'"
        }).IsTrue();
    }

    [Test]
    public async Task ParseArgsCpuMaxRate()
    {
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-e", "20", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuMaxRate: var rate }:
                await Assert.That(rate).IsEqualTo(20u * 100);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        var cpuCoresNumber = TestSystemInfo2Numas4Groups.CpuCores.Length;
        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "1", "-e", "20", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuMaxRate: var rate }:
                await Assert.That(rate).IsEqualTo((uint)(20 * 100 / (cpuCoresNumber / 1)));
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(TestSystemInfo2Numas4Groups, ["-c", "4", "-e", "20", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.CpuMaxRate: var rate }:
                await Assert.That(rate).IsEqualTo((uint)(20 * 100 / (cpuCoresNumber / 4)));
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }
    }

    [Test]
    public async Task ParseArgsMemoryString()
    {
        switch (Program.ParseArgs(RealSystemInfo, ["-m=2K", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.MaxProcessMemory: var m }:
                await Assert.That(m).IsEqualTo(2 * 1024u);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--maxmem=3M", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.MaxProcessMemory: var m }:
                await Assert.That(m).IsEqualTo(3 * 1024u * 1024u);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-maxjobmem", "3G", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.MaxJobMemory: var m }:
                await Assert.That(m).IsEqualTo(3 * 1024u * 1024u * 1024u);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

    }

    [Test]
    public async Task ParseArgsTime()
    {

        switch (Program.ParseArgs(RealSystemInfo, ["--process-utime=10", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.ProcessUserTimeLimitInMilliseconds: var t }:
                await Assert.That(t).IsEqualTo(10u);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--job-utime", "10ms", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.JobUserTimeLimitInMilliseconds: var t }:
                await Assert.That(t).IsEqualTo(10u);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--job-utime=10s", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.JobUserTimeLimitInMilliseconds: var t }:
                await Assert.That(t).IsEqualTo(10000u);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["-t=10m", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.JobClockTimeLimitInMilliseconds: var t }:
                await Assert.That(t).IsEqualTo(600000u);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        switch (Program.ParseArgs(RealSystemInfo, ["--timeout=10h", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.JobClockTimeLimitInMilliseconds: var t }:
                await Assert.That(t).IsEqualTo(36000000u);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--timeout=sdfms", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "invalid number in one of the constraints"
        }).IsTrue();

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--timeout=10h", "--nowait", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--nowait cannot be used with --timeout"
        }).IsTrue();
    }

    [Test]
    public async Task ParseArgsCustomEnvironmentVariables()
    {
        var envVarsFile = Path.GetTempFileName();
        try
        {
            using (var writer = new StreamWriter(envVarsFile, false))
            {
                writer.WriteLine("TEST=TESTVAL");
                writer.WriteLine("  TEST2 = TEST VAL2  ");
                writer.WriteLine("  TEST3 =  ");
            }

            var parsedEnv = Program.ParseArgs(RealSystemInfo, [$"--env=\"{envVarsFile}\"", "test.exe"]) is RunAsCmdApp
            {
                JobSettings.Environment: var env
            } ? env : default;

            await Assert.That(parsedEnv).IsNotNull().And.ContainsKeyWithValue("TEST", "TESTVAL")
                .And.ContainsKeyWithValue("TEST2", "TEST VAL2")
                .And.ContainsKeyWithValue("TEST3", "");

            using (var writer = new StreamWriter(envVarsFile, false))
            {
                writer.WriteLine("  = TEST VAL2  ");
            }

            await Assert.That(Program.ParseArgs(RealSystemInfo, [$"--env=\"{envVarsFile}\"", "test.exe"]) is ShowHelpAndExit
            {
                ErrorMessage: "the environment file contains invalid data (line: 1)"
            }).IsTrue();
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
    public async Task ParseArgsEnablePrivileges()
    {
        switch (Program.ParseArgs(RealSystemInfo, ["--enable-privilege=TEST1", "--enable-privilege=TEST2", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.Privileges: var privs }:
                await Assert.That(privs).IsEquivalentTo(["TEST1", "TEST2"], EqualityComparer<string>.Default);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }

        // legacy, undocumented behavior
        switch (Program.ParseArgs(RealSystemInfo, ["--enable-privileges=TEST1,TEST2", "test.exe"]))
        {
            case RunAsCmdApp { JobSettings.Privileges: var privs }:
                await Assert.That(privs).IsEquivalentTo(["TEST1", "TEST2"], EqualityComparer<string>.Default);
                break;
            default:
                Assert.Fail("invalid parse results");
                break;
        }
    }

    [Test]
    public async Task ParseArgsJobPriorityClass()
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
                    await Assert.That(parsedPriorityClass).IsEqualTo(pc);
                }
                else { Assert.Fail($"Failed parsing of priority {name}"); }
            }
        }

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--priority=invalid", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: var msg2
        } ? msg2 : null).IsEqualTo("Requested value 'invalid' was not found.");
    }

    [Test]
    public async Task ParseArgsEfficiencyMode()
    {
        (string, PowerThrottling)[] efficiencyModes = [
            ("on", PowerThrottling.On),
            ("ON", PowerThrottling.On),
            ("Off", PowerThrottling.Off),
            ("auto", PowerThrottling.Auto)
        ];

        foreach (var (v, em) in efficiencyModes)
        {
            if (Program.ParseArgs(RealSystemInfo, [$"--efficiency-mode={v}", "test.exe"]) is RunAsCmdApp
                {
                    JobSettings.PowerThrottling: var efficiencyMode
                })
            {
                await Assert.That(efficiencyMode).IsEqualTo(em);
            }
            else { Assert.Fail($"Failed parsing of efficiency mode {v}"); }
        }

        await Assert.That(Program.ParseArgs(RealSystemInfo, [$"--efficiency-mode=invalid", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: var msg
        } ? msg : null).IsEqualTo("--efficiency-mode must be set to either: on, off, or auto");
    }

    [Test]
    public async Task ParseArgsEmptyArgs()
    {
        await Assert.That(Program.ParseArgs(RealSystemInfo, []) is ShowSystemInfoAndExit).IsTrue();
    }

    [Test]
    public async Task ParseArgsUnknownArgument()
    {
        await Assert.That(Program.ParseArgs(RealSystemInfo, ["-c", "1", "--maxmem", "100M", "--unknown", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: var err
        } ? err : "").IsEqualTo("unrecognized arguments: unknown");


        // Numa should be passed through affinity
        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--numa 2", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: var err2
        } ? err2 : "").IsEqualTo("unrecognized arguments: numa 2");
    }

    [Test]
    public async Task ParseArgsBehavioralArguments()
    {
        await Assert.That(Program.ParseArgs(RealSystemInfo, ["-m=10M", "test.exe"]) is RunAsCmdApp
        {
            StartBehavior: StartBehavior.None,
            ExitBehavior: ExitBehavior.WaitForJobCompletion,
            LaunchConfig: LaunchConfig.Default
        }).IsTrue();

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--job-name=test", "-m=10M", "--freeze", "test.exe"]) is RunAsCmdApp
        {
            StartBehavior: StartBehavior.Freeze,
            ExitBehavior: ExitBehavior.WaitForJobCompletion,
            LaunchConfig: LaunchConfig.Default
        }).IsTrue();

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--job-name=test", "-m=10M", "-q", "--nowait", "--thaw", "test.exe"]) is RunAsCmdApp
        {
            StartBehavior: StartBehavior.Thaw,
            ExitBehavior: ExitBehavior.DontWaitForJobCompletion,
            LaunchConfig: LaunchConfig.Quiet,
        }).IsTrue();


        await Assert.That(Program.ParseArgs(RealSystemInfo, ["-m=10M", "--nogui", "--terminate-job-on-exit", "test.exe"]) is RunAsCmdApp
        {
            ExitBehavior: ExitBehavior.TerminateJobOnExit,
            LaunchConfig: LaunchConfig.NoGui
        }).IsTrue();

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["-m=10M", "--freeze", "--thaw", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--thaw and --freeze cannot be set at the same time"
        }).IsTrue();

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--freeze", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--job-name is required when using --thaw or --freeze"
        }).IsTrue();

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--thaw", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--job-name is required when using --thaw or --freeze"
        }).IsTrue();

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["-m=10M", "--terminate-job-on-exit", "--nowait", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--terminate-job-on-exit and --nowait cannot be used together"
        }).IsTrue();

        await Assert.That(Program.ParseArgs(RealSystemInfo, ["--job-name", "test", "--isolate", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--isolate is incompatible with --job-name - you need to use either one"
        }).IsTrue();
    }
}
