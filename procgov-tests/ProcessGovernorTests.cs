using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace LowLevelDesign
{

    [TestFixture]
    public class ProcessGovernorTests
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
                MaxProcessMemory = 1024 * 1024
            };
            session.AdditionalEnvironmentVars.Add("TEST", "TESTVAL");
            session.AdditionalEnvironmentVars.Add("TEST2", "TESTVAL2");

            var appImageExe = Path.GetFileName(@"C:\temp\test.exe");
            var debugger = Program.PrepareDebuggerCommandString(session, appImageExe);

            var envFilePath = Program.GetAppEnvironmentFilePath(appImageExe);
            Assert.True(File.Exists(envFilePath));

            try {

                var txt = File.ReadAllText(envFilePath);
                Assert.AreEqual("TEST=TESTVAL\r\nTEST2=TESTVAL2\r\n", txt);

                Assert.AreEqual(string.Format("\"{0}\" --nogui --debugger --env=\"{1}\" --cpu=0x2 --maxmem=1048576",
                    Environment.GetCommandLineArgs()[0], envFilePath), debugger);
            } finally {
                File.Delete(envFilePath);
            }
        }
    }
}
