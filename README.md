
# Process Governor

[![Build status](https://lowleveldesign.visualstudio.com/Github/_apis/build/status/Process%20Governer%20-%20make%20release)](https://lowleveldesign.visualstudio.com/Github/_build/latest?definitionId=3)

This application allows you to set constraints on a process. It uses [a job object](https://msdn.microsoft.com/en-us/library/windows/desktop/ms684161(v=vs.85).aspx) for this purpose. The available options are:

```
Usage: procgov [OPTIONS] args

Options:
  -m, --maxmem=VALUE         Max committed memory usage in bytes (accepted
                               suffixes: K, M or G).
      --env=VALUE            A text file with environment variables (each
                               line in form: VAR=VAL). Applies only to newly
                               created processes.
  -c, --cpu=VALUE            If in hex (starts with 0x) it is treated as an
                               affinity mask, otherwise it is a number of CPU
                               cores assigned to your app.
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
      --debugger             Internal - do not use.
  -v, --verbose              Show verbose messages in the console.
  -h, --help                 Show this message and exit
  -?                         Show this message and exit
```

**REMEMBER to always use procgov with the same bitness as your application.**

You may set limits on a newly created process or on an already running one. To **attach to a process** use the **-p|--pid** switch, eg. `procgov --maxmem 40M --pid 1234`. To **start a new process** with the limits applied, just pass the process image path and its arguments as procgov arguments, eg. `procgov --maxmem 40M c:\temp\test.exe arg1 arg2"`.

Finally, it is possible to **run procgov always when a given process starts**. When you use the **--install** switch Process Governor will add a special key to the **Image File Execution Options** in the registry, so that it will always start before your chosen process. To install Process Governor for a test.exe process, use the following command: `procgov --install --maxmem 40M test.exe`. You may later remove this installation by using the **--uninstall** switch, eg. `procgov --uninstall test.exe`.

## Limit memory of a process

With the **--maxmem** switch Process Governor allows you to set a limit on a memory committed by a process. On Windows committed memory is actually all private memory that the process uses. This way you may use Process Governor to test your .NET applications (including web applications) for memory leaks. If the process is leaking memory you faster get the **OutOfMemoryException**.

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

## Contributions

Below you may find a list of people who contributed to this project. Thank you!

- @beevvy - an issue report and a fix for a bug with the environment variables
- @weidingerhp - an idea of environment variables for a process and CLR profiler setup

## Links

- **2013.11.21** - [Set process memory limit with Process Governor](http://lowleveldesign.wordpress.com/2013/11/21/set-process-memory-limit-with-process-governor)
- **2016.10.21** - [Releasing wtrace 1.0 and procgov 2.0](https://lowleveldesign.wordpress.com/2016/10/21/releasing-wtrace-1-0-and-procgov-2-0/)
- **2019.01.31** - [Limit the execution time of a process tree on Windows](https://lowleveldesign.org/2019/01/31/limit-the-execution-time-of-a-process-tree-on-windows/)
