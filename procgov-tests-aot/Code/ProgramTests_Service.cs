using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using ProcessGovernor.Library;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using Windows.Win32;
using InstalledProcessSettingsModule = ProcessGovernor.Program.InstalledProcessSettingsModule;

namespace ProcessGovernor.Tests.Code;

public partial class ProgramTests
{
    [Test]
    public async Task ServiceProcessSavedSettings()
    {
        var jobName = $"procgov-test-{Guid.NewGuid():D}";
        var jobSettings = JobSettings.Empty with
        {
            MaxProcessMemory = 100 * 1024 * 1024,
            CpuAffinity = [new(SharedApi.GetDefaultProcessorGroup().Number, 0x1)],
            ActiveProcessLimit = 10,
            RunMode = new RunInNamedJob(jobName),
            Environment = ImmutableDictionary.CreateRange(new KeyValuePair<string, string>[] {
                new("TESTVAR1", "TESTVAL1"),
                new("TESTVAR2", "TESTVAL2")
            }),
            Privileges = ["TestPriv1", "TestPriv2"]
        };

        InstalledProcessSettingsModule.SaveProcessAndJobSettings("test.exe", jobSettings);
        InstalledProcessSettingsModule.SaveProcessAndJobSettings("broken.exe",
            JobSettings.Empty with { RunMode = RunModes.SharedJob });

        try
        {
            // manually break the settings
            using (var procgovKey = InstalledProcessSettingsModule.RootKey.OpenSubKey(InstalledProcessSettingsModule.RegistrySubKeyPath))
            {
                await Assert.That(procgovKey).IsNotNull();
                using var processesKey = procgovKey!.OpenSubKey("Processes", true);
                await Assert.That(processesKey).IsNotNull();

                string? brokenJobId = (string?)processesKey!.GetValue("broken.exe");
                await Assert.That(brokenJobId).IsNotNull();
                using var jobsKey = procgovKey!.OpenSubKey("Jobs", true);

                jobsKey!.SetValue(brokenJobId, new byte[] { 1 }, RegistryValueKind.Binary);
            }

            InstalledProcessSettingsModule.GetAllSavedProcessAndJobSettings(out var savedJobsSettings, out var savedProcessesSettings);

            var jobId = new ConfigJobId(jobName);
            await Assert.That(savedJobsSettings).ContainsKeyWithValue(jobId, jobSettings);

            await Assert.That(savedProcessesSettings).ContainsKeyWithValue("test.exe", new ConfigJobId(jobName));

            await Assert.That(savedProcessesSettings).ContainsKey("broken.exe");
            var brokenExeJobId = savedProcessesSettings["broken.exe"];
            await Assert.That(savedJobsSettings[brokenExeJobId]).IsEqualTo(JobSettings.Empty with
            {
                RunMode = RunModes.SharedJob
            });
        }
        finally
        {
            InstalledProcessSettingsModule.RemoveSavedProcessSettings("test.exe");
            InstalledProcessSettingsModule.RemoveSavedProcessSettings("broken.exe");
        }
    }

    [Test]
    public async Task ServiceGovernNewProcess()
    {
        const string executablePath = "winver.exe";

        using var cts = new CancellationTokenSource(30000);

        // start the monitor so the run command (started by service) won't hang
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(TimeSpan.FromSeconds(5), true), cts.Token));
        await Assert.That(monitorTask.IsCompleted).IsFalse(); // should be running

        // should not start monitor
        var jobId = new ConfigJobId(Guid.NewGuid().ToString());
        var jobSettings = JobSettings.Empty with
        {
            MaxProcessMemory = 100 * 1024 * 1024,
            ActiveProcessLimit = 10,
            RunMode = new RunInNamedJob(jobId.Id)
        };
        InstalledProcessSettingsModule.SaveProcessAndJobSettings(executablePath, jobSettings);

        try
        {
            using var pipe = new NamedPipeClientStream(".", ProcessGovernorLibraryApi.DefaultPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            var svc = new Program.ProcessGovernorService();

            svc.Start();

            // give it some time to start (it enumerates running processes) and we need to make
            // sure that it will treat our newly started process as new
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            // the monitor should start with the first monitored process
            using var monitoredProcess = Process.Start(executablePath);

            // give it time to discover a new process
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds, cts.Token);

            await Assert.That(monitorTask.IsCompleted).IsFalse(); // should be running

            try
            {
                bool processInJob = Win32JobModule.IsProcessInJob(monitoredProcess.SafeHandle, new SafeFileHandle());
                for (int i = 0; i < 6 && !processInJob; i++)
                {

                    await Task.Delay(1000);
                    processInJob = Win32JobModule.IsProcessInJob(monitoredProcess.SafeHandle, new SafeFileHandle());
                }
                await Assert.That(processInJob).IsTrue();

                svc.Stop();
            }
            finally
            {
                monitoredProcess.CloseMainWindow();
                if (!monitoredProcess.WaitForExit(500))
                {
                    monitoredProcess.Kill();
                }
            }
        }
        finally
        {
            InstalledProcessSettingsModule.RemoveSavedProcessSettings("winver.exe");
        }

        await monitorTask;
    }

    [Test]
    public async Task ServiceClockTimeLimitOnProcess()
    {
        const string executablePath = "winver.exe";

        using var cts = new CancellationTokenSource(30000);

        // start the monitor so the run command (started by service) won't hang
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(TimeSpan.FromSeconds(5), true), cts.Token));
        await Assert.That(monitorTask.IsCompleted).IsFalse(); // should be running

        const int WaitTimeMilliseconds = Program.ServiceProcessObserverIntervalInMilliseconds * 3;

        var jobName = Guid.NewGuid().ToString();
        var jobSettings = JobSettings.Empty with
        {
            CpuAffinity = [new(SharedApi.GetDefaultProcessorGroup().Number, 0x1)],
            CpuMaxRate = 20,
            JobClockTimeLimitInMilliseconds = WaitTimeMilliseconds,
            RunMode = new RunInNamedJob(jobName),
        };
        InstalledProcessSettingsModule.SaveProcessAndJobSettings(executablePath, jobSettings);

        try
        {
            using var pipe = new NamedPipeClientStream(".", ProcessGovernorLibraryApi.DefaultPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            var svc = new Program.ProcessGovernorService();

            svc.Start();

            // give it some time to start (it enumerates running processes) and we need to make
            // sure that it will treat our newly started process as new
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            // the monitor should start with the first monitored process
            using var monitoredProcess = Process.Start(executablePath);

            // give it time to discover a new process
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds, cts.Token);

            await Assert.That(monitorTask.IsCompleted).IsFalse(); // should be running

            try
            {
                var jobId = await SharedApi.GetJobIdFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                for (int i = 0; i < 3 && jobId.IsInvalid(); i++)
                {
                    await Task.Delay(1000);
                    jobId = await SharedApi.GetJobIdFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                }

                using var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
                using (Assert.Multiple())
                {
                    await Assert.That(jobId.GetJobName()).IsEqualTo(jobName);
                    await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo((Win32JobSettings)jobSettings);
                }
                ;

                await Task.Delay(WaitTimeMilliseconds);

                await Assert.That(monitoredProcess.HasExited).IsTrue();

                svc.Stop();
            }
            finally
            {
                monitoredProcess.Kill();
            }
        }
        finally
        {
            InstalledProcessSettingsModule.RemoveSavedProcessSettings("winver.exe");
        }

        await monitorTask;
    }

    [Test]
    public async Task ServiceGovernExistingProcess()
    {
        using var cts = new CancellationTokenSource(30000);

        // start the monitor so the run command (started by service) won't hang
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(TimeSpan.FromSeconds(2), true), cts.Token));
        await Assert.That(monitorTask.IsCompleted).IsFalse(); // should be running

        var jobName = Guid.NewGuid().ToString();
        var jobSettings = JobSettings.Empty with
        {
            MaxProcessMemory = 100 * 1024 * 1024,
            PropagateOnChildProcesses = true,
            RunMode = new RunInNamedJob(jobName)
        };
        InstalledProcessSettingsModule.SaveProcessAndJobSettings("winver.exe", jobSettings);

        try
        {
            using var pipe = new NamedPipeClientStream(".", ProcessGovernorLibraryApi.DefaultPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            var svc = new Program.ProcessGovernorService();

            svc.Start();

            // give it some time to start (it enumerates running processes) and we need to make
            // sure that it will treat our newly started process as new
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            using var monitoredProcess = Process.Start(new ProcessStartInfo() { FileName = "winver.exe", });
            Debug.Assert(monitoredProcess != null);
            Program.Logger.TraceInformation($"[test] {nameof(monitoredProcess)}.Id = {monitoredProcess.Id}");

            // give it time to discover a new process
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            await Assert.That(monitorTask.IsCompleted).IsFalse(); // should be running

            try
            {
                var jobId = await SharedApi.GetJobIdFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                for (int i = 0; i < 10 && jobId.IsInvalid(); i++)
                {
                    await Task.Delay(1000);
                    jobId = await SharedApi.GetJobIdFromMonitor(pipe, (uint)monitoredProcess.Id, cts.Token);
                }

                await Assert.That(jobId.GetJobName()).IsEqualTo(jobName);

                svc.Stop();
            }
            finally
            {
                monitoredProcess.Kill();
            }
        }
        finally
        {
            InstalledProcessSettingsModule.RemoveSavedProcessSettings("winver.exe");
        }

        await monitorTask;
    }

    [Test]
    public async Task ServiceTwoProcessesInSameJob()
    {
        const string winverPath = "winver.exe";
        const string cmdPath = "cmd.exe";

        using var cts = new CancellationTokenSource(10000);

        // start the monitor so the run command (started by service) won't hang
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(TimeSpan.FromSeconds(5), true), cts.Token));
        await Assert.That(monitorTask.IsCompleted).IsFalse(); // should be running

        var jobName = Guid.NewGuid().ToString();
        var jobSettings = JobSettings.Empty with
        {
            MaxProcessMemory = 100 * 1024 * 1024,
            ActiveProcessLimit = 10,
            RunMode = new RunInNamedJob(jobName)
        };
        InstalledProcessSettingsModule.SaveProcessAndJobSettings(winverPath, jobSettings);

        // same job for cmd
        InstalledProcessSettingsModule.SaveProcessAndJobSettings(cmdPath, jobSettings);

        try
        {
            using var pipe = new NamedPipeClientStream(".", ProcessGovernorLibraryApi.DefaultPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            var svc = new Program.ProcessGovernorService();

            svc.Start();

            // give it some time to start (it enumerates running processes) and we need to make
            // sure that it will treat our newly started process as new
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            // the monitor should start with the first monitored process
            using var winverProcess = Process.Start(winverPath);
            using var cmdProcess = Process.Start(cmdPath);

            // give it time to discover a new process
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds, cts.Token);

            await Assert.That(monitorTask.IsCompleted).IsFalse(); // should be running

            var jobHandle = Win32JobModule.OpenJobHandle(jobName, PInvoke.JOB_OBJECT_QUERY);
            try
            {
                var winverJobId = await SharedApi.GetJobIdFromMonitor(pipe, (uint)winverProcess.Id, cts.Token);
                var cmdJobId = await SharedApi.GetJobIdFromMonitor(pipe, (uint)cmdProcess.Id, cts.Token);
                for (int i = 0; i < 3 && (cmdJobId.IsInvalid() || winverJobId.IsInvalid()); i++)
                {
                    await Task.Delay(1000);
                    winverJobId = await SharedApi.GetJobIdFromMonitor(pipe, (uint)winverProcess.Id, cts.Token);
                    cmdJobId = await SharedApi.GetJobIdFromMonitor(pipe, (uint)cmdProcess.Id, cts.Token);
                }

                using (Assert.Multiple())
                {
                    await Assert.That(winverJobId.GetJobName()).IsEqualTo(jobName);
                    await Assert.That(cmdJobId.GetJobName()).IsEqualTo(jobName);
                }
                ;

                await Assert.That(Win32JobModule.QueryJobSettings(jobHandle)).IsEqualTo((Win32JobSettings)jobSettings);

                svc.Stop();
            }
            finally
            {
                jobHandle.Dispose();

                winverProcess.CloseMainWindow();
                if (!winverProcess.WaitForExit(500))
                {
                    winverProcess.Kill();
                }
                cmdProcess.Kill();
            }
        }
        finally
        {
            InstalledProcessSettingsModule.RemoveSavedProcessSettings(winverPath);
            InstalledProcessSettingsModule.RemoveSavedProcessSettings(cmdPath);
        }

        await monitorTask;
    }

    [Test]
    public async Task ServiceProcessSavedLegacySettings_v3()
    {
        string[] env = [@"TESTPATH=C:\temp", "TESTVAR1=VAL1", "TESTVAR2="];
        string[] privileges = ["SeDebugPrivilege"];

        // procgov.exe --install -c 2 -m 200M --env C:\temp\vars.txt --enable-privilege=SeDebugPrivilege winver.exe
        using var procgovKey = InstalledProcessSettingsModule.RootKey.CreateSubKey(InstalledProcessSettingsModule.RegistrySubKeyPath);
        await Assert.That(procgovKey).IsNotNull();
        try
        {
            using (var processKey = procgovKey!.CreateSubKey("test.exe", true))
            {
                await Assert.That(processKey).IsNotNull();

                await FillAndVerifyLegacyProcessKey(processKey);

                // ------- manually break the settings
                processKey.SetValue("JobSettings", (byte[])[0x1], RegistryValueKind.Binary);

                // values without = are not accepted
                env = [@"TESTPATH=C:\temp", "TESTVAR3"];
                processKey.SetValue("Environment", env, RegistryValueKind.MultiString);

                var jobSettings = JobSettings.Empty with
                {
                    Environment = [.. env.Select(v => v.Split('=')).Where(v => v.Length == 2)
                    .Select(v => new KeyValuePair<string, string>(v[0], v[1]))],
                    Privileges = [.. privileges]
                };

                InstalledProcessSettingsModule.GetAllSavedProcessAndJobSettings(out var savedJobsSettings, out var savedProcessesSettings);
                await Assert.That(savedProcessesSettings).ContainsKey("test.exe");
                var jobId = savedProcessesSettings["test.exe"];
                await Assert.That(savedJobsSettings[jobId]).IsEqualTo(jobSettings);
            }

            // this should remove and replace the legacy settings
            InstalledProcessSettingsModule.SaveProcessAndJobSettings("test.exe", JobSettings.Empty with
            {
                Privileges = ["SeDebugPrivilege"]
            });

            await Assert.That(procgovKey.OpenSubKey("test.exe")).IsNull();

            // remove new settings
            InstalledProcessSettingsModule.RemoveSavedProcessSettings("test.exe");
            InstalledProcessSettingsModule.GetAllSavedProcessAndJobSettings(out var _, out var s);
            await Assert.That(s).DoesNotContainKey("test.exe");

            using (var processKey = procgovKey!.CreateSubKey("test.exe", true))
            {
                await Assert.That(processKey).IsNotNull();

                await FillAndVerifyLegacyProcessKey(processKey);
            }

            // this should remove the legacy settings
            InstalledProcessSettingsModule.RemoveSavedProcessSettings("test.exe");
            InstalledProcessSettingsModule.GetAllSavedProcessAndJobSettings(out var _, out s);
            await Assert.That(s).DoesNotContainKey("test.exe");

        }
        finally
        {
            procgovKey.DeleteSubKey("test.exe", throwOnMissingSubKey: false);
        }

        // Helpers

        async Task FillAndVerifyLegacyProcessKey(RegistryKey processKey)
        {
            processKey.SetValue("Environment", env, RegistryValueKind.MultiString);

            processKey.SetValue("Privileges", privileges, RegistryValueKind.MultiString);

            byte[] serializedJobBytes = [0x9d, 0xce, 0xc, 0x80, 0, 0, 0, 0, 0, 0x91, 0x92, 0, 3, 0, 0, 0, 0, 0, 0xc2, 0, 0];
            processKey.SetValue("JobSettings", serializedJobBytes, RegistryValueKind.Binary);

            var jobSettings = JobSettings.Empty with
            {
                MaxProcessMemory = 200 * 1024 * 1024,
                CpuAffinity = [new(SharedApi.GetDefaultProcessorGroup().Number, 0x3)],
                RunMode = new RunInAnonymousSharedJob(),
                Environment = [.. env.Select(v => v.Split('=')).Where(v => v.Length == 2)
                    .Select(v => new KeyValuePair<string, string>(v[0], v[1]))],
                Privileges = [.. privileges]
            };

            InstalledProcessSettingsModule.GetAllSavedProcessAndJobSettings(out var savedJobsSettings, out var savedProcessesSettings);
            await Assert.That(savedProcessesSettings).ContainsKey("test.exe");
            var jobId = savedProcessesSettings["test.exe"];
            await Assert.That(savedJobsSettings[jobId]).IsEqualTo(jobSettings);
        }
    }

    [Test]
    public async Task ServiceProcessSavedLegacySettings_v4()
    {
        using var procgovKey = InstalledProcessSettingsModule.RootKey.CreateSubKey(InstalledProcessSettingsModule.RegistrySubKeyPath);
        await Assert.That(procgovKey).IsNotNull();

        using var jobsKey = procgovKey.CreateSubKey("Jobs", true);
        using var processesKey = procgovKey.CreateSubKey("Processes", true);

        // procgov --install --priority=idle --maxmem 1G test.exe
        byte[] serializedJobBytes = [0x04,0xdc,0x00,0x11,0xce,0x40,0x00,0x00,0x00,0x00,0x00,0x00,
                    0x90,0x00,0x00,0x00,0x00,0x00,0xc2,0x00,0x40,0x92,0x02,0x80,0x90,0x80,0x00];
        jobsKey.SetValue("10dd053c-079b-4432-bed9-8b0e79ccdbba", serializedJobBytes, RegistryValueKind.Binary);
        processesKey.SetValue("test.exe", "10dd053c-079b-4432-bed9-8b0e79ccdbba");
        try
        {
            InstalledProcessSettingsModule.GetAllSavedProcessAndJobSettings(out var savedJobsSettings, out var savedProcessesSettings);
            await Assert.That(savedProcessesSettings).ContainsKey("test.exe");
            var jobId = savedProcessesSettings["test.exe"];
            await Assert.That(savedJobsSettings[jobId]).IsEqualTo(JobSettings.Empty with
            {
                PriorityClass = PriorityClass.Idle,
                MaxProcessMemory = 1_073_741_824
            });
        }
        finally
        {
            processesKey.DeleteValue("test.exe");
            jobsKey.DeleteValue("10dd053c-079b-4432-bed9-8b0e79ccdbba");
        }
    }
}
