using ProcessGovernor.Library;
using System.Collections.Immutable;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

namespace ProcessGovernor.Tests.Code;

public class SerializationTests
{
    [Test]
    public async Task JobSettingsSerializationTest()
    {
        var settings = new JobSettings(
            MaxProcessMemory: 10000,
            MaxJobMemory: 101,
            MaxWorkingSetSize: 100,
            MinWorkingSetSize: 10,
            CpuAffinity: [new(0, 0x1)],
            CpuMaxRate: 90,
            MaxBandwidth: 100,
            ProcessUserTimeLimitInMilliseconds: 1000,
            JobUserTimeLimitInMilliseconds: 1000,
            JobClockTimeLimitInMilliseconds: 5000,
            PropagateOnChildProcesses: false,
            ActiveProcessLimit: 10,
            PriorityClass: PriorityClass.Normal,
            RunMode: RunModes.Default,
            Privileges: ["priv1", "priv2"],
            Environment: ImmutableDictionary.CreateRange([new KeyValuePair<string, string>("Env1", "Val1")]),
            EfficiencyMode: EfficiencyMode.Off
        );

        var serialized = MsgPackSerializer.Serialize(settings);
        var deserialized = MsgPackSerializer.Deserialize<JobSettings>(serialized);

        await Assert.That(deserialized).IsEqualTo(settings);
    }
}
