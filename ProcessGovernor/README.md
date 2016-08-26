Process Governor
================

Provides a means of starting a Process with limited Resources (at the moment memory limits are supported)

With contribution from [weidingerhp](https://github.com/weidingerhp) - thank you.

Usage:
<pre>
procgov.exe [OPTIONS] args
Options:
 -m, --maxmem=VALUE         Max committed memory usage in bytes (accepted
                              suffixes: K, M or G).
     --profilerguid=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX
                            Profiler GUID
 -p, --pid=VALUE            Attach to an already running process
 -h, --help                 Show this message and exit
 -?                         Show this message and exit
</pre>
