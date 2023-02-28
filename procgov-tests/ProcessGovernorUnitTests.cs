using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace ProcessGovernor.Tests
{

    [TestFixture]
    public class ProcessGovernorUnitTests
    {
        [Test]
        public void CalculateAffinityMaskFromCpuCountTest()
        {
            Assert.AreEqual(0x1UL, Program.CalculateAffinityMaskFromCpuCount(1));
            Assert.AreEqual(0x3UL, Program.CalculateAffinityMaskFromCpuCount(2));
            Assert.AreEqual(0xfUL, Program.CalculateAffinityMaskFromCpuCount(4));
            Assert.AreEqual(0x1ffUL, Program.CalculateAffinityMaskFromCpuCount(9));
            Assert.AreEqual(0xffffffffffffffffUL, Program.CalculateAffinityMaskFromCpuCount(64));
        }

        [Test]
        public void LoadCustomEnvironmentVariablesTest()
        {
            var envVarsFile = Path.GetTempFileName();
            try {
                using (var writer = new StreamWriter(envVarsFile, false)) {
                    writer.WriteLine("TEST=TESTVAL");
                    writer.WriteLine("  TEST2 = TEST VAL2  ");
                }

                var session = new SessionSettings();
                Program.LoadCustomEnvironmentVariables(session, envVarsFile);
                CollectionAssert.AreEqual(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    { "TEST", "TESTVAL" },
                    { "TEST2", "TEST VAL2" }
                }, session.AdditionalEnvironmentVars);


                using (var writer = new StreamWriter(envVarsFile, false)) {
                    writer.WriteLine("  = TEST VAL2  ");
                }

                Assert.Throws<ArgumentException>(() => {
                    Program.LoadCustomEnvironmentVariables(session, envVarsFile);
                });
            } finally {
                if (File.Exists(envVarsFile)) {
                    File.Delete(envVarsFile);
                }
            }
        }

        [Test]
        public void ParseMemoryStringTest()
        {
            Assert.AreEqual(2 * 1024u, Program.ParseMemoryString("2K"));
            Assert.AreEqual(3 * 1024u * 1024u, Program.ParseMemoryString("3M"));
            Assert.AreEqual(3 * 1024u * 1024u * 1024u, Program.ParseMemoryString("3G"));
        }

        [Test]
        public void ParseTimeStringToMillisecondsTest()
        {
            Assert.AreEqual(10u, Program.ParseTimeStringToMilliseconds("10"));
            Assert.AreEqual(10u, Program.ParseTimeStringToMilliseconds("10ms"));
            Assert.AreEqual(10000u, Program.ParseTimeStringToMilliseconds("10s"));
            Assert.AreEqual(600000u, Program.ParseTimeStringToMilliseconds("10m"));
            Assert.AreEqual(36000000u, Program.ParseTimeStringToMilliseconds("10h"));
            Assert.Throws<FormatException>(() => Program.ParseTimeStringToMilliseconds("sdfms"));
        }

        [Test]
        public void PrepareDebuggerCommandStringTest()
        {
            var session = new SessionSettings() {
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
                Privileges = new[] { "SeDebugPrivilege", "SeShutdownPrivilege" },
                PropagateOnChildProcesses = true,
                SpawnNewConsoleWindow = true
            };
            session.AdditionalEnvironmentVars.Add("TEST", "TESTVAL");
            session.AdditionalEnvironmentVars.Add("TEST2", "TESTVAL2");

            var appImageExe = Path.GetFileName(@"C:\temp\test.exe");
            var debugger = Program.PrepareDebuggerCommandString(session, appImageExe, true);

            var envFilePath = Program.GetAppEnvironmentFilePath(appImageExe);
            Assert.True(File.Exists(envFilePath));

            try {

                var txt = File.ReadAllText(envFilePath);
                Assert.AreEqual("TEST=TESTVAL\r\nTEST2=TESTVAL2\r\n", txt);

                var expectedCmdLine =
                    $"\"{Environment.GetCommandLineArgs()[0]}\" --nogui --debugger --env=\"{envFilePath}\" --cpu=0x2 --maxmem=1048576 " +
                    "--maxjobmem=2097152 --maxws=1048576 --minws=1024 --node=1 --cpurate=90 --bandwidth=100 --recursive " +
                    "--timeout=2000 --process-utime=500 --job-utime=1000 --enable-privileges=SeDebugPrivilege,SeShutdownPrivilege --nowait";

                Assert.AreEqual(expectedCmdLine, debugger);
            } finally {
                File.Delete(envFilePath);
            }
        }
    }
}
