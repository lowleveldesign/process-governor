using MessagePack;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

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
            if (Path.GetFileName(Environment.ProcessPath) is { } executablePath)
            {
                var executableName = Path.GetFileName(executablePath);
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

        SaveProcessSettings(installData.ExecutablePath,
            new(installData.JobSettings, installData.Environment, installData.Privileges));

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

        RemoveSavedProcessSettings(uninstallData.ExecutablePath);

        return AnyProcessSavedSettingsLeft() ? 0 : Execute(new RemoveAllProcessGovernance(uninstallData.ServiceInstallPath));

        static bool AnyProcessSavedSettingsLeft()
        {
            using var rootKey = Environment.IsPrivilegedProcess ? Registry.LocalMachine : Registry.CurrentUser;
            if (rootKey.OpenSubKey(RegistrySubKeyPath) is { } procgovKey)
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

            using var rootKey = Environment.IsPrivilegedProcess ? Registry.LocalMachine : Registry.CurrentUser;
            rootKey.DeleteSubKeyTree(RegistrySubKeyPath, throwOnMissingSubKey: false);

            if (Directory.Exists(removeRequest.ServiceInstallPath) && string.Compare(
                Path.TrimEndingDirectorySeparator(removeRequest.ServiceInstallPath),
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) != 0)
            {
                Directory.Delete(removeRequest.ServiceInstallPath, recursive: true);
            }
        }

        return 0;
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
                                                $"Discovered a new process to be governed: {executablePath} ({p.Id}), job settings: {settings.JobSettings}");

                                            // when clock time limit is set we have to wait for the monitored job
                                            ExitBehavior exitBehavior = settings.JobSettings.ClockTimeLimitInMilliseconds > 0 ? 
                                                ExitBehavior.WaitForJobCompletion : ExitBehavior.DontWaitForJobCompletion;
                                            _ = Execute(new RunAsCmdApp(settings.JobSettings, new AttachToProcess([(uint)p.Id]),
                                                    settings.Environment, settings.Privileges, LaunchConfig.Quiet | LaunchConfig.NoGui,
                                                    exitBehavior), ct);
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

            // wait for the monitor task to complete or report an error
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

        JobSettings ParseJobSettings(RegistryKey processKey)
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

        List<string> ParsePrivileges(RegistryKey processKey)
        {
            if (processKey.GetValue("Privileges") is string[] privileges)
            {
                return [.. privileges];
            }
            return [];
        }

        Dictionary<string, string> ParseEnvironmentVars(RegistryKey processKey)
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

    internal static void SaveProcessSettings(string executablePath, ProcessSavedSettings settings)
    {
        using var rootKey = Environment.IsPrivilegedProcess ? Registry.LocalMachine : Registry.CurrentUser;
        using var procgovKey = rootKey.CreateSubKey(RegistrySubKeyPath);
        using var processKey = procgovKey.CreateSubKey(executablePath);

        processKey.SetValue("JobSettings", MessagePackSerializer.Serialize(settings.JobSettings), RegistryValueKind.Binary);
        if (settings.Environment.Count > 0)
        {
            processKey.SetValue("Environment", GetEnvironmentLines(), RegistryValueKind.MultiString);
        }
        if (settings.Privileges.Count > 0)
        {
            processKey.SetValue("Privileges", settings.Privileges.ToArray(), RegistryValueKind.MultiString);
        }

        string[] GetEnvironmentLines()
        {
            return settings.Environment.Select(kv => $"{kv.Key}={kv.Value}").ToArray();
        }
    }

    internal static void RemoveSavedProcessSettings(string executablePath)
    {
        using var rootKey = Environment.IsPrivilegedProcess ? Registry.LocalMachine : Registry.CurrentUser;
        if (rootKey.OpenSubKey(RegistrySubKeyPath, writable: true) is { } procgovKey)
        {
            procgovKey.DeleteSubKey(executablePath, throwOnMissingSubKey: false);
            procgovKey.Dispose();
        }
    }

    internal sealed record ProcessSavedSettings(
        JobSettings JobSettings,
        Dictionary<string, string> Environment,
        List<string> Privileges
    );
}
