using ProcessGovernor.Library;
using System.Collections.Immutable;

namespace ProcessGovernor;

public enum StartBehavior { Freeze, Thaw, None };

public enum ExitBehavior { WaitForJobCompletion, DontWaitForJobCompletion, TerminateJobOnExit };

[Flags]
public enum LaunchConfig { Default = 0, NoGui = 1, Quiet = 2 }

public interface IJobTarget;

public record LaunchProcess(List<string> Procargs, bool NewConsole) : IJobTarget;

public record AttachToProcess(uint[] Pids) : IJobTarget;

public interface IExecutionMode;

public record ShowHelpAndExit(string ErrorMessage) : IExecutionMode;

public record ShowSystemInfoAndExit() : IExecutionMode;

public record RunAsCmdApp(
    JobSettings? JobSettings, // when not null we always update the job settings (even if empty)
    IJobTarget JobTarget,
    LaunchConfig LaunchConfig,
    StartBehavior StartBehavior,
    ExitBehavior ExitBehavior) : IExecutionMode;

public record RunAsMonitor(TimeSpan MaxMonitorIdleTime, bool NoGui) : IExecutionMode;

public record RunAsService : IExecutionMode;

public record SetupProcessGovernance(
    JobSettings JobSettings,
    string ExecutablePath,
    string ServiceInstallPath,
    string ServiceUserName,
    string? ServiceUserPassword) : IExecutionMode;

public record RemoveProcessGovernance(string ExecutablePath, string ServiceInstallPath) : IExecutionMode;

public record RemoveAllProcessGovernance(string ServiceInstallPath) : IExecutionMode;
