
# Process Governor

![.NET](https://github.com/lowleveldesign/process-governor/workflows/build/badge.svg)

This application allows you to set constraints on a process. It uses [a job object](https://msdn.microsoft.com/en-us/library/windows/desktop/ms684161(v=vs.85).aspx) for this purpose. 

Procgov requires .NET 4.6.2 to run and you may download the latest version binaries from the [release page](https://github.com/lowleveldesign/process-governor/releases) or install it with [Chocolatey](https://chocolatey.org/):

```
choco install procgov
```

:warning: Always use procgov executable with the same bitness as your application.

The available options are:

```
Usage: procgov [OPTIONS] args

Options:
  -m, --maxmem=VALUE         Max committed memory usage in bytes (accepted
                               suffixes: K, M, or G).
      --maxjobmem=VALUE      Max committed memory usage for all the processes
                               in the job (accepted suffixes: K, M, or G).
      --maxws=VALUE          Max working set size in bytes (accepted
                               suffixes: K, M, or G). Must be set with minws.
      --minws=VALUE          Min working set size in bytes (accepted
                               suffixes: K, M, or G). Must be set with maxws.
      --env=VALUE            A text file with environment variables (each
                               line in form: VAR=VAL). Applies only to newly
                               created processes.
  -n, --node=VALUE           The preferred NUMA node for the process.
  -c, --cpu=VALUE            If in hex (starts with 0x) it is treated as an
                               affinity mask, otherwise it is a number of CPU
                               cores assigned to your app. If you also provide
                               the NUMA node, this setting will apply only to
                               this node.
  -e, --cpurate=VALUE        The maximum CPU rate in % for the process. If
                               you also set the affinity, the rate will apply
                               only to the selected CPU cores. (Windows 8.1+)
  -b, --bandwidth=VALUE      The maximum bandwidth (in bytes) for the process
                               outgoing network traffic (accepted suffixes: K,
                               M, or G). (Windows 10+)
  -r, --recursive            Apply limits to child processes too (will wait
                               for all processes to finish).
      --newconsole           Start the process in a new console window.
      --nogui                Hide Process Governor console window (set always
                               when installed as debugger).
  -p, --pid=VALUE            Attach to an already running process
      --install              Install procgov as a debugger for a specific
                               process using Image File Executions. DO NOT USE
                               this option if the process you want to control
                               starts child instances of itself (for example,
                               Chrome).
  -t, --timeout=VALUE        Kill the process (with -r, also all its
                               children) if it does not finish within the
                               specified time. Add suffix to define the time
                               unit. Valid suffixes are: ms, s, m, h.
      --process-utime=VALUE  Kill the process (with -r, also applies to its
                               children) if it exceeds the given user-mode
                               execution time. Add suffix to define the time
                               unit. Valid suffixes are: ms, s, m, h.
      --job-utime=VALUE      Kill the process (with -r, also all its
                               children) if the total user-mode execution time
                               exceed the specified value. Add suffix to define
                               the time unit. Valid suffixes are: ms, s, m, h.
      --uninstall            Uninstall procgov for a specific process.
      --enable-privileges=VALUE
                             Enables the specified privileges in the remote
                               process. You may specify multiple privileges by
                               splitting them with commas, for example,
                               'SeDebugPrivilege,SeLockMemoryPrivilege'
      --debugger             Internal - do not use.
  -q, --quiet                Do not show procgov messages.
      --nowait               Does not wait for the target process(es) to exit.
  -v, --verbose              Show verbose messages in the console.
  -h, --help                 Show this message and exit
  -?                         Show this message and exit
```

You may set limits on a newly created process or on an already running one. To **attach to a process** use the **-p|--pid** switch, eg. `procgov --maxmem 40M --pid 1234`. To **start a new process** with the limits applied, just pass the process image path and its arguments as procgov arguments, eg. `procgov --maxmem 40M c:\temp\test.exe arg1 arg2"`.

Starting from version 2.8, it is possible to **update once set limits**. Simply run procgov providing new limits and the target process ID. Procgov will update only the specified limits. Let's have a look at an example to understand this behavior better:

```powershell
PS> procgov64 -m 100M -c 2 notepad.exe
# notepad PID: 1234
#
# applied limits:
#  - CPU affinity: 2 cores
#  - max committed memory: 100MB
#
# <stop procgov with Ctrl+C>

PS> procgov64 -m 120M -p 1234
# On rerun, procgov will update the memory limit, but it won't modify the CPU affinity
#
# applied limits:
#  - CPU affinity: 2 cores
#  - max committed memory: 120MB
```

Finally, you may **run procgov always when a given process starts**. When you use the **--install** switch Process Governor will add a special key to the **Image File Execution Options** in the registry, so that it will always start before your chosen process. To install Process Governor for a test.exe process, use the following command: `procgov --install --maxmem 40M test.exe`. You may later remove this installation by using the **--uninstall** switch, eg. `procgov --uninstall test.exe`.

## Limit memory of a process

With the **--maxmem** switch Process Governor allows you to set a limit on a memory committed by a process. On Windows committed memory is actually all private memory that the process uses. This way you may use Process Governor to test your .NET applications (including web applications) for memory leaks. If the process is leaking memory you faster get the **OutOfMemoryException**.

With the **--maxws** and **--minws** switches you may control the maximum and minimum working set sizes of the process. If **--maxws** is > 0, **--minws** must also be > 0, and vice-versa.

## Limit CPU usage of a process (CPU affinity)

With the **--cpu** switch you may control on which cores your application will run. If you provide the CPU core number as **a decimal value**, your application will be allowed to use the specified number of cores. If you provide the CPU core number as **a hex value (with 0x prefix)**, this number will be treated as an affinity mask - where each bit represents a CPU core (starting from the least significant bit). Let's have a look at two example usages on a CPU intensive application.  In a first one we set the CPU core limit to two cores:

```
> procgov --cpu=2 TestLimit.exe
```

A CPU usage graph on my machine looks as follows:

![cpu-equals-2](https://raw.githubusercontent.com/lowleveldesign/process-governor/master/docs/cpuaffinity-equals-2.png)

In a second we set the CPU affinity mask (with the hex notation):

```
> procgov --cpu=0x2 TestLimit.exe
```

A CPU graph in this case looks as follows (notice only the second core is used):

![cpu-equals-0x2](https://raw.githubusercontent.com/lowleveldesign/process-governor/master/docs/cpuaffinity-equals-0x2.png)

## Limit the CPU rate

The **--cpu-rate** option allows you to set the maximum CPU rate for the process. If you also set the CPU affinity, the rate will apply only to the selected cores. For example, if you have eight logical CPU cores on your machine and you set the CPU rate to 100% and the CPU affinity to 0x7 (first four cores), the maximum CPU rate reported for this process by the monitoring tools will be 50% (we are running at full capacity but on a half of the CPU number).

## Limit the execution time of the process (clock time)

With the **--timeout** option you may define the maximum time (clock time) the process can run before procgov terminates it. If the **--recursive** option is set and the timeout passes, progov will terminate also all the process children started from the beginning of the monitoring session.

## Limit the user-mode execution time

The **--process-utime** and **--job-utime** options allow you to set a limit on the maximum user-mode execution time for a process (with the **--recursive** option also all its children) or a job. The latter case will make sense with the **--recursive** option as it will set a limit on the total user-mode execution time for the process and its children.

## Set additional environment variables for a process

With the **--env** switch you may provide a file with additional environment variables, which should be set for a process. An example usage (provided by @weidingerhp) is to set the COR PROFILING variables:

```
COR_ENABLE_PROFILING=0x01
COR_PROFILER={32E2F4DA-1BEA-47ea-88F9-C5DAF691C94A}
```

## Enable process privileges

Starting from version 2.10, you can enable privileges in the target process with the **--enable-privileges** switch. You may specify multiple privileges, separated by a comma, for example:

```
procgov --enable-privileges=SeDebugPrivilege,SeShutdownPrivilege notepad
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
