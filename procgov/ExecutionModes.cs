using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessGovernor;
enum ExitBehavior { WaitForJobCompletion, DontWaitForJobCompletion, TerminateJobOnExit };

interface IExecutionMode { }

record ExitImmediately(string errorMessage) : IExecutionMode;

record LaunchProcess(
    JobSettings JobSettings,
    List<string> Procargs,
    bool NewConsole,
    Dictionary<string, string> Environment,
    List<string> Privileges,
    bool NoGui,
    ExitBehavior ExitBehavior) : IExecutionMode;

record AttachToProcesses(
    JobSettings JobSettings,
    int[] Pids,
    Dictionary<string, string> Environment,
    List<string> Privileges,
    bool NoGui,
    ExitBehavior ExitBehavior) : IExecutionMode;

record RunAsMonitor : IExecutionMode;

record RunAsService : IExecutionMode;

record InstallService(JobSettings JobSettings, string ExecutablePath) : IExecutionMode;

record UninstallService(string ExecutablePath) : IExecutionMode;
