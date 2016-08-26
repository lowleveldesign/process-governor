using System;
using System.Collections.Generic;
using System.Linq;
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
    }
}
