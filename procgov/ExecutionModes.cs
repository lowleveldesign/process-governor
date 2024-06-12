

namespace ProcessGovernor;

enum ExitBehavior { WaitForJobCompletion, DontWaitForJobCompletion, TerminateJobOnExit };

internal interface IExecutionMode;

record ShowHelpAndExit(string ErrorMessage) : IExecutionMode;

record LaunchProcess(
    JobSettings JobSettings,
    List<string> Procargs,
    bool NewConsole,
    Dictionary<string, string> Environment,
    List<string> Privileges,
    bool NoGui,
    bool Quiet,
    ExitBehavior ExitBehavior) : IExecutionMode;

record AttachToProcesses(
    JobSettings JobSettings,
    int[] Pids,
    Dictionary<string, string> Environment,
    List<string> Privileges,
    bool NoGui,
    bool Quiet,
    ExitBehavior ExitBehavior) : IExecutionMode;

record RunAsMonitor : IExecutionMode;

record RunAsService : IExecutionMode;

record InstallService(JobSettings JobSettings, string ExecutablePath) : IExecutionMode;

record UninstallService(string ExecutablePath) : IExecutionMode;
