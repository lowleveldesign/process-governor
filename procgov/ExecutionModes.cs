﻿namespace ProcessGovernor;

enum StartBehavior { Freeze, Thaw, None };

enum ExitBehavior { WaitForJobCompletion, DontWaitForJobCompletion, TerminateJobOnExit };

[Flags]
enum LaunchConfig { Default = 0, NoGui = 1, Quiet = 2, NoMonitor = 4 }

internal interface IJobTarget;

record LaunchProcess(List<string> Procargs, bool NewConsole) : IJobTarget;

record AttachToProcess(uint[] Pids) : IJobTarget;

internal interface IExecutionMode;

record ShowHelpAndExit(string ErrorMessage) : IExecutionMode;

record ShowSystemInfoAndExit() : IExecutionMode;

record RunAsCmdApp(
    string? JobName,
    JobSettings JobSettings,
    IJobTarget JobTarget,
    Dictionary<string, string> Environment,
    List<string> Privileges,
    LaunchConfig LaunchConfig,
    StartBehavior StartBehavior,
    ExitBehavior ExitBehavior) : IExecutionMode;

record RunAsMonitor(TimeSpan MaxMonitorIdleTime, bool NoGui) : IExecutionMode;

record RunAsService : IExecutionMode;

record SetupProcessGovernance(
    string? JobName,
    JobSettings JobSettings,
    Dictionary<string, string> Environment,
    List<string> Privileges,
    string ExecutablePath,
    string ServiceInstallPath,
    string ServiceUserName,
    string? ServiceUserPassword) : IExecutionMode;

record RemoveProcessGovernance(string ExecutablePath, string ServiceInstallPath) : IExecutionMode;

record RemoveAllProcessGovernance(string ServiceInstallPath) : IExecutionMode;
