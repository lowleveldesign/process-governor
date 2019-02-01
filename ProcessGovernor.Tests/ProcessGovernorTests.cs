using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace LowLevelDesign
{
    public class ProcessGovernorTests
    {
        [Fact]
        public void CalculateAffinityMaskFromCpuCountTest()
        {
            Assert.Equal(0x1, Program.CalculateAffinityMaskFromCpuCount(1));
            Assert.Equal(0x3, Program.CalculateAffinityMaskFromCpuCount(2));
            Assert.Equal(0xf, Program.CalculateAffinityMaskFromCpuCount(4));
            Assert.Equal(0x1ff, Program.CalculateAffinityMaskFromCpuCount(9));
            Assert.Equal(-1L, Program.CalculateAffinityMaskFromCpuCount(64));
        }

        [Fact]
        public void LoadCustomEnvironmentVariablesTest()
        {
            var envVarsFile = Path.GetTempFileName();
            try {
                using (var writer = new StreamWriter(envVarsFile, false)) {
                    writer.WriteLine("TEST=TESTVAL");
                    writer.WriteLine("  TEST2 = TEST VAL2  ");
                }

                var procgov = new ProcessGovernor();
                Program.LoadCustomEnvironmentVariables(procgov, envVarsFile);
                Assert.Equal<KeyValuePair<string, string>>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    { "TEST", "TESTVAL" },
                    { "TEST2", "TEST VAL2" }
                }, procgov.AdditionalEnvironmentVars);


                using (var writer = new StreamWriter(envVarsFile, false)) {
                    writer.WriteLine("  = TEST VAL2  ");
                }

                Assert.Throws<ArgumentException>(() => {
                    Program.LoadCustomEnvironmentVariables(procgov, envVarsFile);
                });
            } finally {
                if (File.Exists(envVarsFile)) {
                    File.Delete(envVarsFile);
                }
            }
        }

        [Fact]
        public void ParseMemoryStringTest()
        {
            Assert.Equal(2 * 1024u, Program.ParseMemoryString("2K"));
            Assert.Equal(3 * 1024u * 1024u, Program.ParseMemoryString("3M"));
            Assert.Equal(3 * 1024u * 1024u * 1024u, Program.ParseMemoryString("3G"));
        }

        [Fact]
        public void ParseTimeStringToMillisecondsTest()
        {
            Assert.Equal(10u, Program.ParseTimeStringToMilliseconds("10"));
            Assert.Equal(10u, Program.ParseTimeStringToMilliseconds("10ms"));
            Assert.Equal(10000u, Program.ParseTimeStringToMilliseconds("10s"));
            Assert.Equal(600000u, Program.ParseTimeStringToMilliseconds("10m"));
            Assert.Equal(36000000u, Program.ParseTimeStringToMilliseconds("10h"));
            Assert.Throws<FormatException>(() => Program.ParseTimeStringToMilliseconds("sdfms"));
        }

        [Fact]
        public void PrepareDebuggerCommandStringTest()
        {
            var procgov = new ProcessGovernor() {
                CpuAffinityMask = 0x2,
                MaxProcessMemory = 1024 * 1024
            };
            procgov.AdditionalEnvironmentVars.Add("TEST", "TESTVAL");
            procgov.AdditionalEnvironmentVars.Add("TEST2", "TESTVAL2");

            var appImageExe = Path.GetFileName(@"C:\temp\test.exe");
            var debugger = Program.PrepareDebuggerCommandString(procgov, appImageExe);

            var envFilePath = Program.GetAppEnvironmentFilePath(appImageExe);
            Assert.True(File.Exists(envFilePath));

            try {

                var txt = File.ReadAllText(envFilePath);
                Assert.Equal("TEST=TESTVAL\r\nTEST2=TESTVAL2\r\n", txt);

                Assert.Equal(string.Format("\"{0}\" --nogui --debugger --env=\"{1}\" --cpu=0x2 --maxmem=1048576",
                    Assembly.GetAssembly(typeof(ProcessGovernor)).Location, envFilePath), debugger);
            } finally {
                File.Delete(envFilePath);
            }
        }
    }
}
