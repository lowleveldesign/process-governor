using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ProcessGovernor;

enum IpcMessage
{
    AssignJobToObject = 1,
}

[StructLayout(LayoutKind.Sequential)]
record struct AssignJobToObject();

