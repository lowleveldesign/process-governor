using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ProcessGovernor;

[Union(0, typeof(AssignJobToProcess))]
public interface IMonitorRequest { }

public interface IMonitorResponse { }


[StructLayout(LayoutKind.Sequential)]
record AssignJobToProcess(int ProcessId) : IMonitorRequest;



