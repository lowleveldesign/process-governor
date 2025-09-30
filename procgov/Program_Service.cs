using MessagePack;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace ProcessGovernor;

static partial class Program
{
    public const string ServiceName = "ProcessGovernor";
    public const int ServiceProcessObserverIntervalInMilliseconds = 1500;
    public const string RegistrySubKeyPath = @"SOFTWARE\ProcessGovernor";

    public static int Execute(RunAsService _)
    {
        if (Environment.UserInteractive)
        {
            throw new InvalidOperationException("the provided parameters require a service session");
        }

        Logger.Listeners.Add(new EventLogTraceListener(ServiceName));

        ServiceBase.Run(new ProcessGovernorService());
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

        if (!WindowsServiceModule.IsServiceInstalled(ServiceName))
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
                WindowsServiceModule.InstallService(ServiceName, ServiceName, serviceBinaryPath,
                    installData.ServiceUserName, installData.ServiceUserPassword);
            }
            else
            {
                throw new InvalidOperationException("could not determine the executable name");
            }
        }

        SaveProcessSettings(installData.ExecutablePath, new(installData.JobName,
            installData.JobSettings, installData.Environment, installData.Privileges));

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

        RemoveSavedProcessSettings(uninstallData.ExecutablePath);

        if (!AnyProcessSavedSettingsLeft())
        {
            FullUninstall(new RemoveAllProcessGovernance(uninstallData.ServiceInstallPath));
        }

        return 0;


        static bool AnyProcessSavedSettingsLeft()
        {
            if (Registry.LocalMachine.OpenSubKey(RegistrySubKeyPath) is { } procgovKey)
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
        if (WindowsServiceModule.IsServiceInstalled(ServiceName))
        {
            using (var serviceControl = new ServiceController(ServiceName))
            {
                if (serviceControl.Status == ServiceControllerStatus.Running)
                {
                    serviceControl.Stop();
                    serviceControl.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
            }
            WindowsServiceModule.UninstallService(ServiceName);

            if (EventLog.SourceExists(ServiceName))
            {
                EventLog.DeleteEventSource(ServiceName);
            }

            Registry.LocalMachine.DeleteSubKeyTree(RegistrySubKeyPath, throwOnMissingSubKey: false);

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
                    Logger.TraceEvent(TraceEventType.Error, 0,
                        $"I could not delete the {removeRequest.ServiceInstallPath} folder. Please remove it manually.");
                }
            }
        }


        static bool TerminateMonitor()
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(10);

                // we need to terminate the monitor process
                if (PInvoke.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var monitorProcessId))
                {
                    Logger.TraceEvent(TraceEventType.Verbose, 0, $"Monitor process ID is: {monitorProcessId}");
                    var monitorProcessHandle = PInvoke.OpenProcess(
                        PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE, false, monitorProcessId);
                    if (PInvoke.TerminateProcess(monitorProcessHandle, 0xff))
                    {
                        return PInvoke.WaitForSingleObject(monitorProcessHandle, 1000) == WAIT_EVENT.WAIT_OBJECT_0;
                    }
                    else
                    {
                        Logger.TraceEvent(TraceEventType.Error, 0, $"Error when terminating the monitor process: {Marshal.GetLastPInvokeError():x}");
                        return false;
                    }
                }
                else
                {
                    Logger.TraceEvent(TraceEventType.Error, 0, $"Error when reading the monitor process ID: {Marshal.GetLastPInvokeError():x}");
                    return false;
                }
            }
            catch
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0, "No monitor instance running.");
                return true;
            }
        }
    }

    internal sealed class ProcessGovernorService : ServiceBase
    {
        readonly CancellationTokenSource cts = new();

        Task processObserverTask = Task.CompletedTask;

        public ProcessGovernorService()
        {
            ServiceName = Program.ServiceName;
        }

        public void Start()
        {
            processObserverTask = Task.Run(() => RunProcessObserver(cts.Token));

            static void RunProcessObserver(CancellationToken ct)
            {
                const double CacheInvalidationTimeoutInSeconds = 120;

                DateTime lastSettingsReloadTimeUtc = DateTime.MinValue;
                Dictionary<string, ProcessSavedSettings> settingsCache = [];

                var runningProcesses = Process.GetProcesses().Select(p => p.Id).ToHashSet();

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        Task.Delay(ServiceProcessObserverIntervalInMilliseconds, ct).Wait(ct);

                        if ((DateTime.UtcNow - lastSettingsReloadTimeUtc).TotalSeconds > CacheInvalidationTimeoutInSeconds)
                        {
                            settingsCache = GetProcessesSavedSettings();
                            lastSettingsReloadTimeUtc = DateTime.UtcNow;
                        }

                        var currentRunningProcesses = new HashSet<int>(runningProcesses.Count);
                        foreach (var p in Process.GetProcesses())
                        {
                            try
                            {
                                if (!runningProcesses.Remove(p.Id) && p.MainModule?.FileName is { } executablePath)
                                {
                                    // we detected a new process, time to handle it
                                    string[] possibleProcessSubKeyNames = [executablePath, Path.GetFileName(executablePath)];

                                    foreach (var sk in possibleProcessSubKeyNames)
                                    {
                                        if (settingsCache.TryGetValue(sk, out var settings))
                                        {
                                            Logger.TraceEvent(TraceEventType.Verbose, 0,
                                                $"Discovered a new process to be governed: {executablePath} ({p.Id}), job name: '{settings.JobName}', job settings: {settings.JobSettings}");

                                            // when clock time limit is set we have to wait for the monitored job
                                            ExitBehavior exitBehavior = settings.JobSettings.ClockTimeLimitInMilliseconds > 0 ?
                                                ExitBehavior.WaitForJobCompletion : ExitBehavior.DontWaitForJobCompletion;
                                            _ = Execute(new RunAsCmdApp(settings.JobName, settings.JobSettings, new AttachToProcess([(uint)p.Id]),
                                                    settings.Environment, settings.Privileges, LaunchConfig.Quiet | LaunchConfig.NoGui,
                                                    StartBehavior.None, exitBehavior), ct);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
                            {
                                // might occur when we try to access the process' main module
                                Logger.TraceEvent(TraceEventType.Warning, 0, $"Failed when getting information about process {p.Id}: {ex}");
                            }
                            currentRunningProcesses.Add(p.Id);
                        }
                        runningProcesses = currentRunningProcesses;
                    }
                    catch (OperationCanceledException) { /* could be thrown by Task.Delay.Wait */ }
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
            try
            {
                if (!processObserverTask.Wait(TimeSpan.FromSeconds(10)))
                {
                    Logger.TraceEvent(TraceEventType.Warning, 0, "Process observer task did not complete in time.");
                }
            }
            catch (Exception ex)
            {
                Logger.TraceEvent(TraceEventType.Error, 0, $"Process observer task was in erronous state: {ex}");
            }

            processObserverTask = Task.CompletedTask;
        }
    }

    internal static Dictionary<string, ProcessSavedSettings> GetProcessesSavedSettings()
    {
        Dictionary<string, ProcessSavedSettings> settings = [];

        using var rootKey = Environment.IsPrivilegedProcess ? Registry.LocalMachine : Registry.CurrentUser;

        if (rootKey.OpenSubKey(RegistrySubKeyPath) is { } procgovKey)
        {
            try
            {
                foreach (string sk in procgovKey.GetSubKeyNames())
                {
                    if (procgovKey.OpenSubKey(sk) is { } processKey)
                    {
                        try
                        {
                            settings[sk] = new(
                                JobName: processKey.GetValue("JobName") as string,
                                JobSettings: ParseJobSettings(processKey),
                                Environment: ParseEnvironmentVars(processKey),
                                Privileges: ParsePrivileges(processKey)
                            );
                        }
                        finally
                        {
                            processKey.Dispose();
                        }
                    }
                }
            }
            finally
            {
                procgovKey.Dispose();
            }
        }

        return settings;


        static List<string> ParsePrivileges(RegistryKey processKey)
        {
            if (processKey.GetValue("Privileges") is string[] privileges)
            {
                return [.. privileges];
            }
            return [];
        }

        static Dictionary<string, string> ParseEnvironmentVars(RegistryKey processKey)
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

            return envVars;
        }
    }

    static JobSettings ParseJobSettings(RegistryKey processKey)
    {
        if (processKey.GetValue("JobSettings", Array.Empty<byte>()) is byte[] serializedBytes && serializedBytes.Length > 0)
        {
            try
            {
                return MessagePackSerializer.Deserialize<JobSettings>(serializedBytes);
            }
            catch (MessagePackSerializationException) { }
        }

        Logger.TraceEvent(TraceEventType.Warning, 0, $"Invalid job settings for process {processKey.Name}");
        return new();
    }

    internal static void SaveProcessSettings(string executablePath, ProcessSavedSettings settings)
    {
        using var procgovKey = Registry.LocalMachine.CreateSubKey(RegistrySubKeyPath);
        using var processKey = procgovKey.CreateSubKey(executablePath);

        if (settings.Environment.Count > 0)
        {
            processKey.SetValue("Environment", (string[])[.. settings.Environment.Select(kv => $"{kv.Key}={kv.Value}")], RegistryValueKind.MultiString);
        }
        if (settings.Privileges.Count > 0)
        {
            processKey.SetValue("Privileges", settings.Privileges.ToArray(), RegistryValueKind.MultiString);
        }

        if (settings.JobName is { } jobName && jobName != "")
        {
            string[] otherProcessKeyNames = [.. procgovKey.GetSubKeyNames().Where(sk => sk != executablePath)];

            processKey.SetValue("JobName", jobName, RegistryValueKind.String);

            if (settings.JobSettings.IsEmpty())
            {
                // if there are no job settings, we will try to find them by name and use for our process
                var jobSettings = otherProcessKeyNames.Aggregate(settings.JobSettings, (jobSettings, keyName) =>
                {
                    using var processKey = procgovKey.OpenSubKey(keyName);
                    if (processKey?.GetValue("JobName") as string == jobName)
                    {
                        Logger.TraceEvent(TraceEventType.Information, 0,
                            $"Found installed process '{keyName}' with the same job name. Its settings will be copied.");
                        return ParseJobSettings(processKey);
                    }
                    else
                    {
                        return jobSettings;
                    }
                });

                processKey.SetValue("JobSettings", MessagePackSerializer.Serialize(jobSettings), RegistryValueKind.Binary);

                ShowLimits(jobSettings);
            }
            else
            {
                var serializedJobSettings = MessagePackSerializer.Serialize(settings.JobSettings);
                // processes with the same job name will have their job settings overwritten
                Array.ForEach(otherProcessKeyNames, (keyName) =>
                {
                    using var processKey = procgovKey.OpenSubKey(keyName, true);
                    if (processKey?.GetValue("JobName") as string == jobName)
                    {
                        processKey.SetValue("JobSettings", serializedJobSettings, RegistryValueKind.Binary);
                    }
                });
                processKey.SetValue("JobSettings", serializedJobSettings, RegistryValueKind.Binary);

                ShowLimits(settings.JobSettings);
            }
        }
        else
        {
            processKey.SetValue("JobSettings", MessagePackSerializer.Serialize(settings.JobSettings), RegistryValueKind.Binary);

            ShowLimits(settings.JobSettings);
        }
    }

    internal static void RemoveSavedProcessSettings(string executablePath)
    {
        if (Registry.LocalMachine.OpenSubKey(RegistrySubKeyPath, writable: true) is { } procgovKey)
        {
            procgovKey.DeleteSubKey(executablePath, throwOnMissingSubKey: false);
            procgovKey.Dispose();
        }
    }

    internal sealed record ProcessSavedSettings(
        string? JobName,
        JobSettings JobSettings,
        Dictionary<string, string> Environment,
        List<string> Privileges
    );
}
