using Microsoft.Win32;
using ProcessGovernor.Library;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.System.Services;
using Windows.Win32.Storage.FileSystem;
using System.Collections.Immutable;
using System.Threading.Channels;

using static ProcessGovernor.Library.ProcessGovernorLibraryApi;

namespace ProcessGovernor;

public static partial class Program
{
    public const string ServiceName = "ProcessGovernor";
    public const int ServiceProcessObserverIntervalInMilliseconds = 1500;

    public static int Execute(RunAsService _)
    {
        if (Environment.UserInteractive)
        {
            throw new InvalidOperationException("the provided parameters require a service session");
        }

        Logger.Listeners.Add(new EventLogTraceListener(ServiceName));

        using var service = new ProcessGovernorService();

        ServiceBase.Run(service);

        return 0;
    }

    public static int Execute(SetupProcessGovernance installData)
    {
        Logger.Listeners.Add(new ConsoleTraceListener());

        if (!Environment.IsPrivilegedProcess)
        {
            throw new InvalidOperationException($"admin privileges are required to install the {ServiceName} service");
        }

        if (!EventLog.SourceExists(ServiceName))
        {
            EventLog.CreateEventSource(ServiceName, "Application");
        }

        if (!ProcessGovernorService.IsServiceInstalled(ServiceName))
        {
            if (Environment.ProcessPath is { } executablePath && Path.GetFileName(executablePath) is { } executableName)
            {
                var installExecutablePath = Path.Combine(installData.ServiceInstallPath, executableName);

                if (string.Compare(Path.TrimEndingDirectorySeparator(installData.ServiceInstallPath),
                        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) != 0)
                {
                    Directory.CreateDirectory(installData.ServiceInstallPath);
                    File.Copy(executablePath, installExecutablePath);
                }

                var serviceBinaryPath = $"\"{installExecutablePath}\" --service";
                ProcessGovernorService.InstallService(ServiceName, ServiceName, serviceBinaryPath,
                    installData.ServiceUserName, installData.ServiceUserPassword);
            }
            else
            {
                throw new InvalidOperationException("could not determine the executable name");
            }
        }

        InstalledProcessSettingsModule.SaveProcessAndJobSettings(installData.ExecutablePath, installData.JobSettings);

        using var serviceControl = new ServiceController(ServiceName);
        if (serviceControl.Status != ServiceControllerStatus.Running)
        {
            serviceControl.Start();
        }

        return 0;
    }

    public static int Execute(RemoveProcessGovernance uninstallData)
    {
        Logger.Listeners.Add(new ConsoleTraceListener());

        if (!Environment.IsPrivilegedProcess)
        {
            throw new InvalidOperationException($"admin privileges are required to update the {ServiceName} service settings");
        }

        InstalledProcessSettingsModule.RemoveSavedProcessSettings(uninstallData.ExecutablePath);

        if (!AnyProcessSavedSettingsLeft())
        {
            FullUninstall(new RemoveAllProcessGovernance(uninstallData.ServiceInstallPath));
        }

        return 0;


        static bool AnyProcessSavedSettingsLeft()
        {
            if (InstalledProcessSettingsModule.RootKey.OpenSubKey($"{InstalledProcessSettingsModule.RegistrySubKeyPath}\\Processes") is { } procgovKey)
            {
                try
                {
                    return procgovKey.GetSubKeyNames().Length != 0;
                }
                finally
                {
                    procgovKey.Dispose();
                }
            }

            return false;
        }
    }

    public static int Execute(RemoveAllProcessGovernance removeRequest)
    {
        Logger.Listeners.Add(new ConsoleTraceListener());

        if (!Environment.IsPrivilegedProcess)
        {
            throw new InvalidOperationException($"admin privileges are required to uninstall the {ServiceName} service");
        }

        FullUninstall(removeRequest);
        return 0;
    }

    static void FullUninstall(RemoveAllProcessGovernance removeRequest)
    {
        Debug.Assert(Environment.IsPrivilegedProcess);
        if (ProcessGovernorService.IsServiceInstalled(ServiceName))
        {
            using (var serviceControl = new ServiceController(ServiceName))
            {
                if (serviceControl.Status == ServiceControllerStatus.Running)
                {
                    serviceControl.Stop();
                    serviceControl.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
            }
            ProcessGovernorService.UninstallService(ServiceName);

            if (EventLog.SourceExists(ServiceName))
            {
                EventLog.DeleteEventSource(ServiceName);
            }

            InstalledProcessSettingsModule.RootKey.DeleteSubKeyTree(InstalledProcessSettingsModule.RegistrySubKeyPath, throwOnMissingSubKey: false);

            if (Directory.Exists(removeRequest.ServiceInstallPath) && string.Compare(
                Path.TrimEndingDirectorySeparator(removeRequest.ServiceInstallPath),
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) != 0)
            {
                if (TerminateMonitor())
                {
                    // there should be no other process keeping the service folder busy
                    Directory.Delete(removeRequest.ServiceInstallPath, recursive: true);
                }
                else
                {
                    Logger.TraceError("[service] I could not delete the {0} folder. Please remove it manually.",
                        removeRequest.ServiceInstallPath);
                }
            }
        }


        static bool TerminateMonitor()
        {
            using var pipe = new NamedPipeClientStream(".", DefaultPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(10);

                // we need to terminate the monitor process
                if (PInvoke.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var monitorProcessId))
                {
                    Logger.TraceVerbose("[service] Monitor process ID is: {0}", monitorProcessId);
                    var monitorProcessHandle = PInvoke.OpenProcess(
                        PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE, false, monitorProcessId);
                    if (PInvoke.TerminateProcess(monitorProcessHandle, 0xff))
                    {
                        return PInvoke.WaitForSingleObject(monitorProcessHandle, 1000) == WAIT_EVENT.WAIT_OBJECT_0;
                    }
                    else
                    {
                        Logger.TraceError("[service] Error when terminating the monitor process: {0:x}", Marshal.GetLastPInvokeError());
                        return false;
                    }
                }
                else
                {
                    Logger.TraceError("[service] Error when reading the monitor process ID: {0:x}", Marshal.GetLastPInvokeError());
                    return false;
                }
            }
            catch
            {
                Logger.TraceVerbose("[service] No monitor instance running.");
                return true;
            }
        }
    }

    public sealed class ProcessGovernorService : ServiceBase
    {
        private readonly CancellationTokenSource cts = new();

        private readonly Channel<IMonitorNotification> notificationChannel =
            Channel.CreateUnbounded<IMonitorNotification>(new UnboundedChannelOptions { SingleReader = true });

        private Task[] backgroundTasks = [];

        public ProcessGovernorService()
        {
            ServiceName = Program.ServiceName;
        }

        public void Start()
        {
            TryEnablingDebugPrivilege();

            backgroundTasks = [
                RunBackgroundTask((ct) => RunGovernor(notificationChannel, ct), cts.Token),
                RunBackgroundTask((ct) => RunNotificationListener(notificationChannel, ct), cts.Token)
            ];

            static async Task RunGovernor(ChannelWriter<IMonitorNotification> notificationSink, CancellationToken ct)
            {
                InstalledProcessSettingsModule.GetAllSavedProcessAndJobSettings(out var jobsSettings, out var processesSettings);
                using var governor = new ProcessGovernorInstance(notificationSink, CreateMonitorClientStream, 1000, ct)
                {
                    ProcessMonitorIntervalMilliseconds = ServiceProcessObserverIntervalInMilliseconds,
                    AutoAssignJobSettings = jobsSettings,
                    AutoAssignProcessSettings = processesSettings
                };

                await governor.WaitForStop();
            }

            static NamedPipeClientStream? CreateMonitorClientStream(CancellationToken ct)
            {
                var pipe = new NamedPipeClientStream(".", DefaultPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    pipe.Connect(10);

                    return pipe;
                }
                catch
                {
                    Logger.TraceVerbose("[service] Launching monitor...");
                    StartMonitor();

                    var maxTryCount = 20;
                    while (!ct.IsCancellationRequested && maxTryCount-- > 0)
                    {
                        try { pipe.Connect(100); } catch { }
                    }
                    Logger.TraceVerbose("[service] Monitor connection status: {0}",
                        pipe.IsConnected ? "connected" : "failed to connect");

                    return pipe.IsConnected ? pipe : null;
                }

                static unsafe void StartMonitor()
                {
                    if (Environment.ProcessPath is null)
                    {
                        throw new InvalidOperationException("Can't launch monitor process because the ProcessPath is unknown.");
                    }

                    // we always enable verbose logs for the monitor since it uses ETW or Debug output
                    string cmdline = $"{Environment.ProcessPath} --monitor --verbose --nogui";

                    var pi = new PROCESS_INFORMATION();
                    var si = new STARTUPINFOW();

                    fixed (char* cmdlinePtr = cmdline)
                    {
                        if (!PInvoke.CreateProcess(null, new PWSTR(cmdlinePtr), null, null, false, 0, null, null, &si, &pi))
                        {
                            throw new Win32Exception();
                        }
                    }

                    PInvoke.CloseHandle(pi.hProcess);
                    PInvoke.CloseHandle(pi.hThread);
                }
            }

            static async Task RunNotificationListener(ChannelReader<IMonitorNotification> notificationReader, CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var n = await notificationReader.ReadAsync(ct);
                        Logger.TraceVerbose("[service] Notification received: {0}", n);
                    }
                    catch (Exception ex) when (ex.IsCancelledException()) { }
                }
            }
        }

        protected override void OnStart(string[] args)
        {
            Start();
        }

        protected override void OnStop()
        {
            cts.Cancel();
            if (!Task.WaitAll(backgroundTasks, 4000))
            {
                Logger.TraceWarning("[service] Some backgroung tasks did not finish in the alotted time.");
            }
            else
            {
                Logger.TraceVerbose("[service] Service stopped gracefully.");
            }

            backgroundTasks = [];
        }

        public static bool IsServiceInstalled(string name)
        {
            unsafe
            {
                if (PInvoke.OpenSCManager((PCWSTR)null, null, PInvoke.SC_MANAGER_CONNECT) is var scmHandle && scmHandle.IsNull)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    var namePtr = Marshal.StringToHGlobalUni(name);
                    try
                    {
                        if (PInvoke.OpenService(scmHandle, (char*)namePtr, PInvoke.SERVICE_QUERY_STATUS) is var svcHandle && svcHandle.IsNull)
                        {
                            if (Marshal.GetLastWin32Error() is var err && err == (int)WIN32_ERROR.ERROR_SERVICE_DOES_NOT_EXIST)
                            {
                                return false;
                            }

                            throw new Win32Exception(err);
                        }
                        PInvoke.CloseServiceHandle(svcHandle);

                        return true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(namePtr);
                    }
                }
                finally
                {
                    PInvoke.CloseServiceHandle(scmHandle);
                }
            }
        }

        public static void InstallService(string name, string displayName, string binaryPath, string svcAccountName, string? svcAccountPassword)
        {
            unsafe
            {
                if (PInvoke.OpenSCManager((PCWSTR)null, null, PInvoke.SC_MANAGER_CREATE_SERVICE) is var scmHandle && scmHandle.IsNull)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                nint namePtr = 0, displayNamePtr = 0, binaryPathPtr = 0, svcAccountNamePtr = 0, svcAccountPasswordPtr = 0;
                try
                {
                    namePtr = Marshal.StringToHGlobalUni(name);
                    displayNamePtr = Marshal.StringToHGlobalUni(displayName);
                    binaryPathPtr = Marshal.StringToHGlobalUni(binaryPath);
                    svcAccountNamePtr = Marshal.StringToHGlobalUni(svcAccountName);
                    if (svcAccountPassword is not null)
                    {
                        svcAccountPasswordPtr = Marshal.StringToHGlobalUni(svcAccountPassword);
                    }

                    if (PInvoke.CreateService(scmHandle, (char*)namePtr, (char*)displayNamePtr, PInvoke.SERVICE_ALL_ACCESS,
                        ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS, SERVICE_START_TYPE.SERVICE_DEMAND_START, SERVICE_ERROR.SERVICE_ERROR_NORMAL,
                        (char*)binaryPathPtr, (char*)null, null, (char*)null, (char*)svcAccountNamePtr, (char*)svcAccountPasswordPtr) is var svcHandle
                        && svcHandle.IsNull)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    PInvoke.CloseServiceHandle(svcHandle);
                }
                finally
                {
                    PInvoke.CloseServiceHandle(scmHandle);

                    if (namePtr != 0) { Marshal.FreeHGlobal(namePtr); }
                    if (displayNamePtr != 0) { Marshal.FreeHGlobal(displayNamePtr); }
                    if (binaryPathPtr != 0) { Marshal.FreeHGlobal(binaryPathPtr); }
                    if (svcAccountNamePtr != 0) { Marshal.FreeHGlobal(svcAccountNamePtr); }
                    if (svcAccountPasswordPtr != 0) { Marshal.FreeHGlobal(svcAccountPasswordPtr); }
                }
            }
        }

        public static void UninstallService(string name)
        {
            unsafe
            {
                if (PInvoke.OpenSCManager((PCWSTR)null, null, PInvoke.SC_MANAGER_CONNECT) is var scmHandle && scmHandle.IsNull)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                nint namePtr = 0;
                try
                {
                    namePtr = Marshal.StringToHGlobalUni(name);
                    if (PInvoke.OpenService(scmHandle, (char*)namePtr, (uint)FILE_ACCESS_RIGHTS.DELETE) is var svcHandle && svcHandle.IsNull)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    if (!PInvoke.DeleteService(svcHandle))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    PInvoke.CloseServiceHandle(svcHandle);
                }
                finally
                {
                    PInvoke.CloseServiceHandle(scmHandle);

                    if (namePtr != 0) { Marshal.FreeHGlobal(namePtr); }
                }
            }
        }
    }

    public static class InstalledProcessSettingsModule
    {
        public const string RegistrySubKeyPath = @"SOFTWARE\ProcessGovernor";

        // used in tests only to make them work in a regular user scope
        public static bool IsUserScoped { get; set; } = false;

        public static RegistryKey RootKey => IsUserScoped ? Registry.CurrentUser : Registry.LocalMachine;

        public static void SaveProcessAndJobSettings(string executablePath, JobSettings settings)
        {
            var jobId = settings.RunMode switch
            {
                RunInNamedJob n => n.JobName,
                _ => Guid.NewGuid().ToString()
            };

            using var procgovKey = RootKey.CreateSubKey(RegistrySubKeyPath);

            using var jobsKey = OpenOrCreateRegistryKey(procgovKey, "Jobs");
            byte[] bytes = [.. (byte[])[JobSettings.Version], .. MsgPackSerializer.Serialize(settings)];
            jobsKey.SetValue(jobId, bytes, RegistryValueKind.Binary);

            using var processesKey = OpenOrCreateRegistryKey(procgovKey, "Processes");
            processesKey.SetValue(executablePath, jobId, RegistryValueKind.String);

            // we should remove process keys used in older procgov versions
            procgovKey.DeleteSubKey(executablePath, throwOnMissingSubKey: false);

            // Helpers

            static RegistryKey OpenOrCreateRegistryKey(RegistryKey procgovKey, string name)
            {
                if (procgovKey.OpenSubKey(name, writable: true) is not { } key)
                {
                    key = procgovKey.CreateSubKey(name, writable: true);
                }
                return key;
            }
        }

        public static void RemoveSavedProcessSettings(string executablePath)
        {
            using var procgovKey = RootKey.CreateSubKey(RegistrySubKeyPath);

            using (var processesKey = procgovKey.OpenSubKey("Processes", writable: true))
            {
                if (processesKey is not null)
                {
                    if (processesKey.GetValue(executablePath) is string jobId)
                    {
                        processesKey.DeleteValue(executablePath, throwOnMissingValue: false);

                        // check if there are any more processes using this job id and if not, remove it as well
                        if (!processesKey.GetValueNames().Any((n) =>
                                processesKey.GetValue(n) is string otherJobId &&
                                    jobId.Equals(otherJobId, StringComparison.Ordinal)))
                        {
                            if (RootKey.OpenSubKey($"{RegistrySubKeyPath}\\Jobs", writable: true) is { } jobsKey)
                            {
                                try { jobsKey.DeleteValue(jobId, throwOnMissingValue: false); }
                                finally { jobsKey.Dispose(); }
                            }
                        }
                    }
                }
            }

            // we should remove process keys used in older procgov versions
            procgovKey.DeleteSubKey(executablePath, throwOnMissingSubKey: false);
        }

        public static void GetAllSavedProcessAndJobSettings(out ImmutableDictionary<ConfigJobId, JobSettings> jobsSettings,
            out ImmutableDictionary<string, ConfigJobId> processesSettings)
        {
            var jobs = GetSavedJobsSettings();
            var processes = GetSavedProcessesSettings(jobs);
#pragma warning disable CS0612 // Type or member is obsolete
            AddSavedLegacyProcessSettings(processes, jobs);
#pragma warning restore CS0612 // Type or member is obsolete

            jobsSettings = [.. jobs];
            processesSettings = [.. processes];

            // Helpers

            static Dictionary<ConfigJobId, JobSettings> GetSavedJobsSettings()
            {
                if (RootKey.OpenSubKey($"{RegistrySubKeyPath}\\Jobs") is { } jobsKey)
                {
                    try
                    {
                        return jobsKey.GetValueNames().Select(jobId => new KeyValuePair<ConfigJobId, JobSettings>(
                            new(jobId), ParseJobSettings(jobsKey, jobId))).ToDictionary();
                    }
                    finally { jobsKey.Dispose(); }
                }
                else { return []; }


                static JobSettings ParseJobSettings(RegistryKey jobsKey, string jobId)
                {
                    if (jobsKey.GetValue(jobId, Array.Empty<byte>()) is byte[] bytes && bytes.Length > 0)
                    {
                        try
                        {
                            return bytes[0] switch
                            {
#pragma warning disable CS0612 // Type or member is obsolete
                               // 3 => Updater.ToCurrent(MsgPackSerializer.Deserialize<JobSettings_v4>(bytes.AsMemory()[1..]) ?? JobSettings_v4.Empty),
#pragma warning restore CS0612
                                4 => MsgPackSerializer.Deserialize<JobSettings>(bytes.AsMemory()[1..]) ?? JobSettings.Empty,
                                var v => throw new ArgumentException($"unrecognized settings version: {v}")
                            };
                        }
                        catch (Exception ex)
                        {
                            Logger.TraceWarning("[service] Error reading job {0} settings: {1}", jobId, ex);
                            return JobSettings.Empty;
                        }
                    }
                    else
                    {

                        Logger.TraceWarning($"[service] Invalid job settings {jobId}");
                        return JobSettings.Empty;
                    }
                }
            }

            static Dictionary<string, ConfigJobId> GetSavedProcessesSettings(Dictionary<ConfigJobId, JobSettings> jobs)
            {
                if (RootKey.OpenSubKey($"{RegistrySubKeyPath}\\Processes") is { } processesKey)
                {
                    try
                    {
                        return processesKey.GetValueNames().ToDictionary(name => name,
                            name =>
                            {
                                ConfigJobId jobId = new(processesKey.GetValue(name) is string id ? id : Guid.NewGuid().ToString());
                                if (jobs.TryAdd(jobId, JobSettings.Empty))
                                {
                                    Logger.TraceWarning("[service] Job {0} settings missing or invalid for the process '{1}'",
                                        jobId, name);
                                }
                                return jobId;
                            });
                    }
                    finally { processesKey.Dispose(); }
                }
                else { return []; }
            }

            [Obsolete]
            static void AddSavedLegacyProcessSettings(Dictionary<string, ConfigJobId> processes, Dictionary<ConfigJobId, JobSettings> jobs)
            {
                if (RootKey.OpenSubKey($"{RegistrySubKeyPath}") is { } procgovKey)
                {
                    foreach (var processKeyName in procgovKey.GetSubKeyNames())
                    {
                        // check if it's a special key or maybe was installed with new settings
                        if (processKeyName == "Processes" || processKeyName == "Jobs" || processes.ContainsKey(processKeyName))
                        {
                            continue;
                        }

                        using var processKey = procgovKey.OpenSubKey(processKeyName);
                        if (processKey is not null)
                        {
                            ConfigJobId generatedJobId = new(Guid.NewGuid().ToString());
                            jobs.Add(generatedJobId, ReadJobSettings(processKey) with
                            {
                                Privileges = ParsePrivileges(processKey),
                                Environment = ParseEnvironmentVars(processKey)
                            });
                            processes.Add(processKeyName, generatedJobId);
                        }
                    }

                    // Helpers

                    static JobSettings ReadJobSettings(RegistryKey processKey)
                    {
                        try
                        {
                            if (processKey?.GetValue("JobSettings") is byte[] bytes && bytes.Length > 0)
                            {
                                return MsgPackSerializer.Deserialize<JobSettings_v3>(bytes)?.ToCurrent() ?? JobSettings.Empty;
                            }
                            else { throw new InvalidDataException("JobSettings missing or empty"); }
                        }
                        catch (Exception ex)
                        {
                            Logger.TraceWarning("[service] Error reading the process '{0}' job settings: {1}", processKey?.Name, ex);
                            return JobSettings.Empty;
                        }
                    }

                    static ImmutableArray<string> ParsePrivileges(RegistryKey processKey)
                    {
                        if (processKey.GetValue("Privileges") is string[] privileges)
                        {
                            return [.. privileges];
                        }
                        return [];
                    }

                    static ImmutableDictionary<string, string> ParseEnvironmentVars(RegistryKey processKey)
                    {
                        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        if (processKey.GetValue("Environment") is string[] lines)
                        {
                            foreach (var line in lines)
                            {
                                var ind = line.IndexOf('=');
                                if (ind > 0)
                                {
                                    var key = line[..ind].Trim();
                                    if (!string.IsNullOrEmpty(key))
                                    {
                                        var val = line[(ind + 1)..].Trim();
                                        envVars.Add(key, val);
                                    }
                                }
                                else
                                {
                                    Logger.TraceEvent(TraceEventType.Warning, 0, $"The environment block for process {processKey.Name} contains invalid line: {line}");
                                }
                            }
                        }

                        return [.. envVars];
                    }
                }
            }
        }
    }
}

