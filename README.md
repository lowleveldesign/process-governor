
# Process Governor

![.NET](https://github.com/lowleveldesign/process-governor/workflows/build/badge.svg)

This application allows you to set constraints on Windows processes. It uses [a job object](https://msdn.microsoft.com/en-us/library/windows/desktop/ms684161(v=vs.85).aspx) for this purpose. 

**Table of contents**

<!-- MarkdownTOC -->

- [Installation](#installation)
- [Understanding procgov run modes](#understanding-procgov-run-modes)
    - [The command-line application mode \(default\)](#the-command-line-application-mode-default)
    - [The monitor mode](#the-monitor-mode)
    - [The service mode \(beta\)](#the-service-mode-beta)
- [Applying limits on processes](#applying-limits-on-processes)
    - [Setting limits on a single process](#setting-limits-on-a-single-process)
    - [Setting limits on multiple processes](#setting-limits-on-multiple-processes)
    - [Updating already applied limits](#updating-already-applied-limits)
- [Available process constraints](#available-process-constraints)
    - [Limit memory of a process](#limit-memory-of-a-process)
    - [Limit CPU usage of a process \(CPU affinity\)](#limit-cpu-usage-of-a-process-cpu-affinity)
    - [Limit the CPU rate](#limit-the-cpu-rate)
    - [Limit the execution time of the process \(clock time\)](#limit-the-execution-time-of-the-process-clock-time)
    - [Limit the user-mode execution time](#limit-the-user-mode-execution-time)
- [Other options](#other-options)
    - [Set the priority class](#set-the-priority-class)
    - [Set additional environment variables for a process](#set-additional-environment-variables-for-a-process)
    - [Enable process privileges](#enable-process-privileges)
- [Contributions](#contributions)
- [Links](#links)

<!-- /MarkdownTOC -->

## Installation

You may download the latest version binaries from the [release page](https://github.com/lowleveldesign/process-governor/releases) or install it with [Chocolatey](https://chocolatey.org/) or [winget](https://learn.microsoft.com/en-us/windows/package-manager/winget/):

```shell
choco install procgov
# or
winget install procgov
```

## Understanding procgov run modes

### The command-line application mode (default)

Not much to say here :) It's the default mode that is activated when you launch procgov from the command prompt to launch a new process or attach to a running one.

### The monitor mode

When using procgov you may observe that it sometimes launches a second instance of itself (unless you use the --nomonitor switch). This second instance is a job monitor and you may recognize it by the --monitor switch in the command line args. It will stay alive until the last process in the monitored jobs exits. There should be at maximum one instance of a job monitor per Windows session. Its role is to monitor jobs created with procgov. The monitor should exit right after the termination of the last process in the monitored jobs.

### The service mode (beta)

*This feature is in a beta phase. Please use it with caution and report any experienced errors.*

If you use the **--install** switch to persist application settings, procgov will save the settings in the registry and will create a Windows service named ProcessGovernor. By default it will use the SYSTEM account and the `%ProgramFiles%\ProcessGovernor` folder as the service base path. You may configure this settings by using the **--service-path**, **--service-username**, and **--service-password** command-line switches. If you run the install command for another application, procgov will add new data to the registry but will reuse the existing service. The service should pick up the updated configuration after short time.

The ProcessGovernor service monitors starting processes and applies limits predefined during installation.

To uninstall the service, use the **--uninstall** switch. The service will be removed when you remove the last saved configuration. If you want to remove all saved procgov data, along with the service, use the **--uninstall-all** switch.

## Applying limits on processes

### Setting limits on a single process

You may set limits on a newly created process or on an already running one. To **constraint a running process** use the **-p|--pid** switch, eg.

```shell
procgov.exe --maxmem 40M --pid 1234
```

To **start a new process** with the limits applied, just pass the process image path as a procgov argument, eg. `procgov64 --maxmem 40M c:\temp\test.exe`. If you need to **pass any parameters to the target process**, it's best to use `--` to separate procgov parameters from the target process ones, for example:

```shell
procgov.exe -m 100M -- test.exe -arg1 -arg2=val2 arg3
````

### Setting limits on multiple processes

You may assign multiple processes to the same job object. When you use the **-p** parameter multiple times with different process IDs, procgov will apply the same limits for all the processes, for example:

```shell
procgov.exe --maxmem 100M -p 1234 -p 1235 -p 1236
```

If any of the processes was already assigned to a procgov job object, others will be assigned to it as well.

### Updating already applied limits

It is also possible to **update once set limits**. However, there is one requirement: the processes can't be assigned to different procgov jobs (so they must be either in the same job or unassigned). To update the limits, simply run procgov providing new limits and the target process ID(s). Procgov will update only the specified limits. Let's have a look at an example to understand this behavior better:

```shell
We set a CPU limit on a process 1234
procgov.exe --nowait -c 2 -p 1234

Then we run procgov again with the new CPU limit - procgov will update the existing job object
procgov.exe --nowait -c 4 -p 1234
```

## Available process constraints

### Limit memory of a process

With the **--maxmem** (**-m**) switch Process Governor allows you to set a limit on a memory committed by a process. On Windows committed memory is actually all private memory that the process uses. This way you may use Process Governor to test your .NET applications (including web applications) for memory leaks. If the process is leaking memory you faster get the **OutOfMemoryException**.

```shell
procgov.exe -m 100M -c 2 notepad.exe

procgov.exe -m 120M -p 1234
```

With the **--maxws** and **--minws** switches you may control the maximum and minimum working set sizes (physical memory usage) of the process. This option requires **SeIncreaseBasePriorityPrivilege**, so make sure your account has it (more info in the [issue 69](https://github.com/lowleveldesign/process-governor/issues/69)). If you want to limit the working set size, remember to always provide values greater than zero for both these parameters, for example:

```shell
procgov.exe --minws 1M --maxws 120M -p 1234
```

The **--maxjobmem** option allows you to specify the maximum committed memory for all the processes that belong to a given job object. This might be handy when you enable job propagation to child processes or you use the same job object to control multiple processes, for example:

```shell
procgov.exe -r --maxjobmem 200M -- cmd.exe

procgov.exe -r --maxjobmem 1G -p 1234,1235,1236
```

### Limit CPU usage of a process (CPU affinity)

With the **--cpu** switch you may control on which cores your application will run. If you provide the CPU core number as **a decimal value**, your application will be allowed to use the specified number of cores. 

If you provide the CPU core number as **a hex value (with 0x prefix)**, this number will be treated as an affinity mask in the first processor group - where each bit represents a CPU core (starting from the least significant bit). Additionally, you may prepend the affinity mask with a processor group number prefixed with letter 'g' and/or a NUMA node number prefixed with a letter 'n'. You may also skip the affinity and use the NUMA node or processor group affinity. Valid example values: `n1:g0:0xF`, `n1:g0`, `n1`, `g0`.

The --cpu parameter may be defined **multiple times** and the final affinity mask will be a combination of the provided masks.

Let's have a look at two example usages on a CPU intensive application. In a first one we set the CPU core limit to two cores:

```shell
procgov.exe --cpu=2 TestLimit.exe
```

A CPU usage graph on my machine looks as follows:

![cpu-equals-2](https://raw.githubusercontent.com/lowleveldesign/process-governor/master/docs/cpuaffinity-equals-2.png)

In a second we set the CPU affinity mask (with the hex notation):

```shell
procgov.exe --cpu=0x2 TestLimit.exe
```

A CPU graph in this case looks as follows (notice only the second core is used):

![cpu-equals-0x2](https://raw.githubusercontent.com/lowleveldesign/process-governor/master/docs/cpuaffinity-equals-0x2.png)

Examples of more complex affinity settings:

```shell
# Use processor group 0 affinity from NUMA node 0 and 1 core from the group 1 in NUMA node 1
procgov.exe --cpu=n0:g0 --cpu=n1:g1:0x1 TestLimit.exe

# Use processor group 0 affinity and 1 core from the group 1
procgov.exe --cpu=g0 --cpu=g1:0x1 TestLimit.exe
```

If you are unsure what CPU configuration is present in the system, you may run procgov with no params and it will print it:

```shell
procgov.exe
# 
# Use --help to print the available options.
# 
# === SYSTEM INFORMATION ===
# 
# NUMA Node 0:
#   Processor Group 0: 000000000000000F (CPUs: 0,1,2,3)
#   Processor Group 1: 0000000000000007 (CPUs: 4,5,6)
# 
# NUMA Node 1:
#   Processor Group 2: 000000000000000F (CPUs: 7,8,9,10)
#   Processor Group 3: 0000000000000007 (CPUs: 11,12,13)
# 
# Total Physical Memory (MB):          20 460
# Available Physical Memory (MB):      16 086
# Total Committed Memory (MB):         3 701
# Current Committed Memory Limit (MB): 21 740
```

### Limit the CPU rate

The **--cpu-rate** option allows you to set the maximum CPU rate for the process. If you also set the CPU affinity, the rate will apply only to the selected cores. For example, if you have eight logical CPU cores on your machine and you set the CPU rate to 100% and the CPU affinity to 0x7 (first four cores), the maximum CPU rate reported for this process by the monitoring tools will be 50% (we are running at full capacity but on a half of the CPU number).

### Limit the execution time of the process (clock time)

With the **--timeout** option you may define the maximum time (clock time) the process can run before procgov terminates it. If the **--recursive** option is set and the timeout passes, progov will terminate also all the process children started from the beginning of the monitoring session.

### Limit the user-mode execution time

The **--process-utime** and **--job-utime** options allow you to set a limit on the maximum user-mode execution time for a process (with the **--recursive** option also all its children) or a job. The latter case will make sense with the **--recursive** option as it will set a limit on the total user-mode execution time for the process and its children.

## Other options

### Set the priority class

The **--priority** parameter sets the process priority class of monitored processes. Possible values include: `Idle`, `BelowNormal`, `Normal`, `AboveNormal`, `High`, `RealTime`. The highest three priorities require **SeIncreaseBasePriorityPrivilege**, so make sure your account has it (more info in the [issue 69](https://github.com/lowleveldesign/process-governor/issues/69)).

### Set additional environment variables for a process

With the **--env** switch you may set process environment variables. This switch accepts a path to a text file with the variable values, for example:

```shell
COR_ENABLE_PROFILING=0x01
COR_PROFILER={32E2F4DA-1BEA-47ea-88F9-C5DAF691C94A}
```

The procgov command might look as follows:

```shell
procgov.exe --env c:\temp\env.txt -c 2 dotnet_app.exe
```

You may set the environment variables when starting a new process or accessing an existing one.

### Enable process privileges

You can enable privileges in the target process with the **--enable-privilege** switch. You may specify multiple privileges by using this parameter multiple times, for example:

```shell
procgov.exe --enable-privilege=SeDebugPrivilege --enable-privilege=SeShutdownPrivilege notepad
```

Keep in mind that in Windows, you can't add new privileges to the process token. You may only enable existing ones. You may check the available process privileges in Process Hacker or Process Explorer. Check the documentation for a given privilege to learn how to make it available for a given user (for example, you may need to update group policies).

## Contributions

Below you may find a list of people who contributed to this project. Thank you!

- @rowandh - an issue with the WS limit not being set
- @beevvy - an issue report and a fix for a bug with the environment variables
- @weidingerhp - an idea of environment variables for a process and CLR profiler setup

## Links

- **2013.11.21** - [Set process memory limit with Process Governor](http://lowleveldesign.wordpress.com/2013/11/21/set-process-memory-limit-with-process-governor)
- **2016.10.21** - [Releasing wtrace 1.0 and procgov 2.0](https://lowleveldesign.wordpress.com/2016/10/21/releasing-wtrace-1-0-and-procgov-2-0/)
- **2019.01.31** - [Limit the execution time of a process tree on Windows](https://lowleveldesign.org/2019/01/31/limit-the-execution-time-of-a-process-tree-on-windows/)
