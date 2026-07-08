using Microsoft.Win32.SafeHandles;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Wdk.System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Memory;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.System.SystemServices;
using Windows.Win32.System.Threading;
using static ProcessGovernor.Library.Win32.Helpers;
using PInvokeProcGov = ProcessGovernor.Library.Win32.PInvoke;
using PInvokeWdk = Windows.Wdk.PInvoke;

namespace ProcessGovernor.Library;

public record struct Win32ProcessSettings(
    ImmutableArray<string> Privileges,
    // the environment variables may require expansion (for example, a valid value could be "%PATH%;C:\\temp")
    ImmutableDictionary<string, string> Environment,
    PowerThrottling EfficiencyMode
)
{
    public Win32ProcessSettings() : this([], [], PowerThrottling.Auto) { }
}

public record struct Win32Process(SafeHandle Handle, uint Id) : IDisposable
{
    public readonly void Dispose() => Handle.Dispose();
}

internal sealed record Win32ProcessMetadata(string ProcessName, uint ParentId);

[Flags]
public enum ProcessCreationFlags { None = 0, NewConsole = 1, Suspended = 2 }

public static class Win32ProcessModule
{
    // PUBLIC APIs

    public static string GetProcessExecutablePath(uint processId)
    {
        using var processHandle = OpenProcessHandle(processId,
            (uint)PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        return GetProcessImageNameWin32(processHandle);
    }

    public static string GetProcessCommandLine(uint processId)
    {
        using var processHandle = OpenProcessHandle(processId,
            (uint)PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
        return GetProcessCommandLine(processHandle);
    }

    public static void TerminateProcess(uint processId)
    {
        using var processHandle = CheckWin32Result(OpenProcessHandle(processId, (uint)PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE));
        TerminateProcess(processHandle, uint.MaxValue);
    }

    public static void TerminateProcess(SafeHandle processHandle, uint exitCode) =>
        CheckWin32Result(PInvoke.TerminateProcess(processHandle.ToHANDLE(), exitCode));

    public static void WaitForTheProcessToExit(SafeHandle processHandle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            switch (PInvoke.WaitForSingleObject(processHandle.ToHANDLE(), 200 /* ms */))
            {
                case WAIT_EVENT.WAIT_OBJECT_0:
                    return;
                case WAIT_EVENT.WAIT_FAILED:
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                default:
                    break;
            }
        }
    }

    // INTERNAL APIs

    internal static Win32Process StartProcess(string arguments, ProcessCreationFlags flags, Win32ProcessSettings settings,
        out SafeHandle mainThreadHandle)
    {
        var leaveSuspended = flags.HasFlag(ProcessCreationFlags.Suspended);
        var processObject = CreateProcess(arguments, flags | ProcessCreationFlags.Suspended, settings, out mainThreadHandle);
        UpdateNewlyStartedProcessSettings(processObject.Handle, settings, null);

        if (!leaveSuspended)
        {
            CheckWin32Result(PInvoke.ResumeThread(mainThreadHandle));
        }

        return processObject;
    }

    internal static Win32Process StartProcessInJob(string arguments, ProcessCreationFlags flags, Win32ProcessSettings settings, SafeHandle jobHandle)
    {
        var leaveSuspended = flags.HasFlag(ProcessCreationFlags.Suspended);
        var processObject = CreateProcessWithJobAssigned(arguments, flags | ProcessCreationFlags.Suspended, settings, jobHandle, out var mainThreadHandle);
        try
        {
            UpdateNewlyStartedProcessSettings(processObject.Handle, settings, jobHandle);

            if (!leaveSuspended)
            {
                CheckWin32Result(PInvoke.ResumeThread(mainThreadHandle));
            }

            return processObject;
        }
        finally
        {
            mainThreadHandle.Dispose();
        }
    }

    internal static Win32Process StartProcessInJobWithToken(string arguments, ProcessCreationFlags flags, SafeHandle tokenHandle,
        Win32ProcessSettings settings, SafeHandle jobHandle)
    {
        var leaveSuspended = flags.HasFlag(ProcessCreationFlags.Suspended);

        var processObject = CreateProcessWithToken(arguments, flags | ProcessCreationFlags.Suspended,
            settings, tokenHandle, out var mainThreadHandle);
        try
        {
            UpdateNewlyStartedProcessSettings(processObject.Handle, settings, jobHandle);

            if (!leaveSuspended)
            {
                CheckWin32Result(PInvoke.ResumeThread(mainThreadHandle));
            }

            return processObject;
        }
        finally
        {
            mainThreadHandle.Dispose();
        }
    }

    internal static void AssignExistingProcessToJob(uint processId, Win32ProcessSettings settings, SafeHandle jobHandle)
    {
        const uint RequiredJobAssignmentAccessRights = (uint)(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION |
            PROCESS_ACCESS_RIGHTS.PROCESS_SET_QUOTA | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ);
        const uint RequiredInjectionAccessRights = (uint)(PROCESS_ACCESS_RIGHTS.PROCESS_CREATE_THREAD |
            PROCESS_ACCESS_RIGHTS.PROCESS_VM_OPERATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_WRITE);

        var environmentVariables = settings.Environment;

        var requiredAccessRights = RequiredJobAssignmentAccessRights;
        if (environmentVariables.Count > 0)
        {
            requiredAccessRights |= RequiredInjectionAccessRights;
        }
        if (settings.EfficiencyMode != PowerThrottling.Undefined)
        {
            requiredAccessRights |= (uint)PROCESS_ACCESS_RIGHTS.PROCESS_SET_INFORMATION;
        }

        using var processHandle = OpenProcessHandle(processId, requiredAccessRights);

        Win32JobModule.AssignProcess(jobHandle, processHandle);

        AccountPrivilegeModule.TryEnablingProcessPrivileges(processHandle, settings.Privileges, out _);

        SetProcessEnvironmentVariables(processHandle, environmentVariables);

        UpdateUninheritedProcessSettings(processHandle, settings);
    }

    internal static void UpdateUninheritedProcessSettings(uint processId, Win32ProcessSettings settings)
    {
        uint requiredAccessRights = 0;
        if (settings.EfficiencyMode != PowerThrottling.Undefined)
        {
            requiredAccessRights = (uint)PROCESS_ACCESS_RIGHTS.PROCESS_SET_INFORMATION;
        }

        if (requiredAccessRights != 0)
        {
            using var processHandle = OpenProcessHandle(processId, requiredAccessRights);
            UpdateUninheritedProcessSettings(processHandle, settings);
        }
    }

    // it does not throw exception but returns invalid handle on failure
    internal static SafeHandle OpenProcessHandle(uint processId, uint desiredAccess) =>
        PInvoke.OpenProcess_SafeHandle((PROCESS_ACCESS_RIGHTS)desiredAccess, false, processId);

    internal static string? GetEnvironmentString(IReadOnlyDictionary<string, string> additionalEnvironmentVars)
    {
        if (additionalEnvironmentVars.Count == 0)
        {
            return null;
        }

        StringBuilder envEntries = new();
        foreach (string env in Environment.GetEnvironmentVariables().Keys)
        {
            if (additionalEnvironmentVars.ContainsKey(env))
            {
                continue; // overwrite existing env
            }

            envEntries.Append(env).Append('=').Append(
                Environment.GetEnvironmentVariable(env)).Append('\0');
        }

        foreach (var kv in additionalEnvironmentVars)
        {
            // we need to expand the value as provided variables may reference existing variable values
            if (kv.Value != "")
            {
                envEntries.Append(kv.Key).Append('=').Append(
                    Environment.ExpandEnvironmentVariables(kv.Value)).Append('\0');
            }
        }

        if (envEntries.Length > 0)
        {
            envEntries.Append('\0');
            return envEntries.ToString();
        }
        else { return null; }
    }

    internal static string? GetProcessEnvironmentVariable(SafeHandle processHandle, string name, int maxAcceptableLength = 1024)
    {
        unsafe
        {
            bool isWow64 = IsProcessWow64(processHandle);

            var kernel32BaseAddr = GetModuleHandle(processHandle, isWow64, "kernel32.dll");
            var fnGetEnvironmentVariableW = (nint)GetModuleExportRva(processHandle, isWow64, kernel32BaseAddr, "GetEnvironmentVariableW");

            var ntdllHandle = GetModuleHandle(processHandle, isWow64, "ntdll.dll");
            var fnRtlExitUserThread = GetModuleExportRva(processHandle, isWow64, ntdllHandle, "RtlExitUserThread");
            var remoteThreadStart = ntdllHandle + (nint)fnRtlExitUserThread;

            QueueApcThread queueApcThreadFunc = isWow64 ? PInvokeProcGov.RtlQueueApcWow64Thread : PInvokeProcGov.NtQueueApcThread;

            nint remoteThreadHandle = 0;
            bool processHandleAddRef = false;
            try
            {
                processHandle.DangerousAddRef(ref processHandleAddRef);
                var processRawHandle = processHandle.DangerousGetHandle();
                if (PInvokeProcGov.RtlCreateUserThread(processRawHandle, nint.Zero, true, 0, 0, 0, remoteThreadStart,
                     nint.Zero, out remoteThreadHandle, out _) is var status && status != 0)
                {
                    throw new Win32Exception((int)PInvoke.RtlNtStatusToDosError(new NTSTATUS(status)));
                }

                nuint valueBufferSize = (uint)maxAcceptableLength * sizeof(char);

                nuint allocLength = (nuint)name.Length * sizeof(char) + sizeof(char) /* null */ + valueBufferSize;
                var allocAddr = PInvoke.VirtualAllocEx(processHandle.ToHANDLE(), null, allocLength,
                    VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE | VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE);
                if (allocAddr != null)
                {
                    try
                    {
                        // VirtualAllocEx initializes memory to 0 so we don't need to write the null terminator
                        var currAddr = (byte*)allocAddr;
                        nint nameAddr = (nint)currAddr;
                        fixed (void* namePtr = name)
                        {
                            var lengthInBytes = (nuint)name.Length * sizeof(char);
                            CheckWin32Result(PInvoke.WriteProcessMemory(processHandle.ToHANDLE(), currAddr, namePtr, lengthInBytes, null));
                            currAddr += lengthInBytes + sizeof(char); // + null terminator
                        }

                        string valuePlaceholder = "__procgov__";
                        nint valueAddr = (nint)currAddr;
                        fixed (void* valuePlaceholderPtr = valuePlaceholder)
                        {
                            CheckWin32Result(PInvoke.WriteProcessMemory(processHandle.ToHANDLE(), currAddr, valuePlaceholderPtr,
                                (nuint)valuePlaceholder.Length * sizeof(char), null));
                        }

                        status = queueApcThreadFunc(remoteThreadHandle, kernel32BaseAddr + fnGetEnvironmentVariableW,
                            nameAddr, valueAddr, maxAcceptableLength);
                        if (status != 0)
                        {
                            throw new Win32Exception((int)PInvoke.RtlNtStatusToDosError(new NTSTATUS(status)));
                        }

                        // APC is the first thing the new thread executes when resumed
                        if (PInvoke.ResumeThread((HANDLE)remoteThreadHandle) < 0)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }

                        if (PInvoke.WaitForSingleObject((HANDLE)remoteThreadHandle, 5000) is var err && err == WAIT_EVENT.WAIT_TIMEOUT)
                        {
                            throw new Win32Exception((int)WIN32_ERROR.ERROR_TIMEOUT);
                        }
                        else if (err == WAIT_EVENT.WAIT_FAILED)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }

                        var valueBufferAddr = NativeMemory.AllocZeroed(valueBufferSize + sizeof(char) /* null */);
                        try
                        {
                            CheckWin32Result(PInvoke.ReadProcessMemory(processHandle.ToHANDLE(), (void*)valueAddr,
                                valueBufferAddr, valueBufferSize, null));
                            // I can't verify the last error of the call to GetEnvironmentVariableW 
                            // so I assume that if my placeholder was not overriden, the variable
                            // is probably not set in the remote process
                            return Marshal.PtrToStringUni((nint)valueBufferAddr) is var v && v == valuePlaceholder ? null : v;
                        }
                        finally
                        {
                            NativeMemory.Free(valueBufferAddr);
                        }
                    }
                    finally
                    {
                        PInvoke.VirtualFreeEx(processHandle.ToHANDLE(), allocAddr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
                    }
                }
                else
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                if (processHandleAddRef)
                {
                    processHandle.DangerousRelease();
                }

                if (remoteThreadHandle != 0)
                {
                    PInvoke.CloseHandle((HANDLE)remoteThreadHandle);
                }
            }
        }
    }

    internal static void SetProcessEnvironmentVariables(SafeHandle processHandle, IReadOnlyDictionary<string, string> variables)
    {
        if (variables.Count == 0)
        {
            return;
        }

        unsafe
        {
            bool isWow64 = IsProcessWow64(processHandle);

            var kernel32BaseAddr = GetModuleHandle(processHandle, isWow64, "kernel32.dll");
            var fnSetEnvironmentVariableW = (nint)GetModuleExportRva(processHandle, isWow64, kernel32BaseAddr, "SetEnvironmentVariableW");

            var ntdllHandle = GetModuleHandle(processHandle, isWow64, "ntdll.dll");
            var fnRtlExitUserThread = GetModuleExportRva(processHandle, isWow64, ntdllHandle, "RtlExitUserThread");
            var remoteThreadStart = ntdllHandle + (nint)fnRtlExitUserThread;

            QueueApcThread queueApcThreadFunc = isWow64 ? PInvokeProcGov.RtlQueueApcWow64Thread : PInvokeProcGov.NtQueueApcThread;

            var (variablesBlock, startOffsets) = PrepareEnvironmentVariablesBlock(variables);

            nint remoteThreadHandle = 0;
            bool processHandleAddRef = false;
            try
            {
                processHandle.DangerousAddRef(ref processHandleAddRef);
                var processRawHandle = processHandle.DangerousGetHandle();
                if (PInvokeProcGov.RtlCreateUserThread(processRawHandle, nint.Zero, true, 0, 0, 0, remoteThreadStart,
                     nint.Zero, out remoteThreadHandle, out _) is var status && status != 0)
                {
                    throw new Win32Exception((int)PInvoke.RtlNtStatusToDosError(new NTSTATUS(status)));
                }

                var allocSize = (nuint)variablesBlock.Length * sizeof(char);
                var allocAddr = PInvoke.VirtualAllocEx(processHandle.ToHANDLE(), null, allocSize, VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE
                    | VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE);
                if (allocAddr != null)
                {
                    fixed (char* variablesBlockPtr = variablesBlock)
                    {
                        CheckWin32Result(PInvoke.WriteProcessMemory(processHandle.ToHANDLE(), allocAddr, variablesBlockPtr, allocSize, null));
                    }

                    try
                    {
                        var fnAddr = kernel32BaseAddr + fnSetEnvironmentVariableW;

                        foreach (var (nameOffset, valueOffset) in startOffsets)
                        {
                            var nameAddr = (nint)allocAddr + nameOffset * sizeof(char);
                            var valueAddr = valueOffset == 0 ? 0 : (nint)allocAddr + valueOffset * sizeof(char);
                            status = queueApcThreadFunc(remoteThreadHandle, fnAddr, nameAddr, valueAddr, 0);
                            if (status != 0)
                            {
                                throw new Win32Exception((int)PInvoke.RtlNtStatusToDosError(new NTSTATUS(status)));
                            }
                        }

                        // APC is the first thing the new thread executes when resumed
                        if (PInvoke.ResumeThread((HANDLE)remoteThreadHandle) < 0)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }

                        if (PInvoke.WaitForSingleObject((HANDLE)remoteThreadHandle, 5000) is var err && err == WAIT_EVENT.WAIT_TIMEOUT)
                        {
                            throw new Win32Exception((int)WIN32_ERROR.ERROR_TIMEOUT);
                        }
                        else if (err == WAIT_EVENT.WAIT_FAILED)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                    }
                    finally
                    {
                        PInvoke.VirtualFreeEx(processHandle.ToHANDLE(), allocAddr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
                    }
                }
                else
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                if (processHandleAddRef)
                {
                    processHandle.DangerousRelease();
                }

                if (remoteThreadHandle != 0)
                {
                    PInvoke.CloseHandle((HANDLE)remoteThreadHandle);
                }
            }
        }


        static (string Content, (int, int)[] StartOffsets) PrepareEnvironmentVariablesBlock(IReadOnlyDictionary<string, string> variables)
        {
            // prepares the final string to copy to the target process memory (we need to expand the variable values
            // since they may reference existing variables)
            var (buffer, startOffsets) = variables.Aggregate(
                (Buffer: new StringBuilder(), StartOffsets: new List<(int, int)>(variables.Count)), (state, kv) =>
                {
                    var keyOffset = state.Buffer.Length;
                    state.Buffer.Append(kv.Key).Append('\0');
                    if (kv.Value != "")
                    {
                        var valueOffset = state.Buffer.Length;
                        state.Buffer.Append(Environment.ExpandEnvironmentVariables(kv.Value)).Append('\0');
                        state.StartOffsets.Add((keyOffset, valueOffset));
                    }
                    else
                    {
                        state.StartOffsets.Add((keyOffset, 0));
                    }
                    return state;
                }
            );
            return (buffer.ToString(), [.. startOffsets]);
        }
    }

    internal unsafe static string GetProcessImageNameWin32(SafeHandle processHandle)
    {
        uint bufferLength = (uint)sizeof(UNICODE_STRING) + PInvoke.MAX_PATH * sizeof(char);
        var buffer = NativeMemory.Alloc(bufferLength);
        uint stringLength = bufferLength / sizeof(char);
        bool processHandleAddRef = false;
        try
        {
            processHandle.DangerousAddRef(ref processHandleAddRef);
            var nativeProcessHandle = (HANDLE)processHandle.DangerousGetHandle();
            while (!PInvoke.QueryFullProcessImageName(nativeProcessHandle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR((nint)buffer), &stringLength))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER && bufferLength < 1024 * 1024 /* safe guard */)
                {
                    bufferLength *= 2;
                    buffer = NativeMemory.Realloc(buffer, bufferLength);
                    stringLength = bufferLength / sizeof(char);
                }
                else
                {
                    throw new Win32Exception(err);
                }
            }

            Debug.Assert(stringLength > 0);
            return new string((char*)buffer, 0, (int)stringLength);
        }
        finally
        {
            if (processHandleAddRef)
            {
                processHandle.DangerousRelease();
            }

            NativeMemory.Free(buffer);
        }
    }

    internal unsafe static Dictionary<uint, Win32ProcessMetadata> GetRunningProcesses()
    {
        var snapshotHandle = PInvoke.CreateToolhelp32Snapshot(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        try
        {
            Dictionary<uint, Win32ProcessMetadata> processes = [];

            PROCESSENTRY32W processEntry = new() { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            if (!PInvoke.Process32FirstW(snapshotHandle, &processEntry))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            do
            {
                var processName = Marshal.PtrToStringUni((nint)processEntry.szExeFile.Value) ?? "N/A";

                processes[processEntry.th32ProcessID] = new(processName, processEntry.th32ParentProcessID);
            }
            while (PInvoke.Process32NextW(snapshotHandle, &processEntry));

            return processes;
        }
        finally
        {
            PInvoke.CloseHandle(snapshotHandle);
        }
    }

    // PRIVATE

    private static void UpdateUninheritedProcessSettings(SafeHandle processHandle, Win32ProcessSettings settings)
    {
        if (settings.EfficiencyMode != PowerThrottling.Undefined)
        {
            SetEfficiencyMode(processHandle.ToHANDLE(), settings.EfficiencyMode);
        }

        // requires handle with PROCESS_SET_INFORMATION access right
        unsafe static void SetEfficiencyMode(HANDLE processHandle, PowerThrottling effMode)
        {
            PROCESS_POWER_THROTTLING_STATE state = new()
            {
                Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = effMode.HasFlag(PowerThrottling.Auto) ? 0 : PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = effMode.HasFlag(PowerThrottling.Auto) || effMode.HasFlag(PowerThrottling.Off) ? 0 :
                    PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED
            };
            if (!PInvoke.SetProcessInformation(processHandle, PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    private static void UpdateNewlyStartedProcessSettings(SafeHandle processHandle, Win32ProcessSettings settings, SafeHandle? jobHandle)
    {
        if (jobHandle is not null)
        {
            Win32JobModule.AssignProcess(jobHandle, processHandle);
        }

        AccountPrivilegeModule.TryEnablingProcessPrivileges(processHandle, settings.Privileges, out _);

        UpdateUninheritedProcessSettings(processHandle, settings);
    }

    private static PROCESS_CREATION_FLAGS GetWin32ProcessCreationFlags(ProcessCreationFlags flags, Win32ProcessSettings settings) =>
        PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT
            | (flags.HasFlag(ProcessCreationFlags.NewConsole) ? PROCESS_CREATION_FLAGS.CREATE_NEW_CONSOLE : 0)
            | (flags.HasFlag(ProcessCreationFlags.Suspended) ? PROCESS_CREATION_FLAGS.CREATE_SUSPENDED : 0);

    private static unsafe Win32Process CreateProcess(string arguments, ProcessCreationFlags flags,
        Win32ProcessSettings settings, out SafeHandle mainThreadHandle)
    {
        var startupInfo = new STARTUPINFOW() { cb = (uint)sizeof(STARTUPINFOW) };
        var processInfo = new PROCESS_INFORMATION();

        char[] cmdline = [.. arguments, '\0'];

        fixed (char* penv = GetEnvironmentString(settings.Environment))
        fixed (char* cmdlinePtr = cmdline)
        {
            CheckWin32Result(PInvoke.CreateProcess(null, new PWSTR(cmdlinePtr), null, null, false,
                GetWin32ProcessCreationFlags(flags, settings), penv, null, &startupInfo, &processInfo));
        }

        mainThreadHandle = new SafeFileHandle(processInfo.hThread, true);
        return new Win32Process(new SafeFileHandle(processInfo.hProcess, true), processInfo.dwProcessId);
    }

    private unsafe static Win32Process CreateProcessWithToken(string arguments, ProcessCreationFlags flags,
        Win32ProcessSettings settings, SafeHandle tokenHandle, out SafeHandle mainThreadHandle)
    {
        var startupInfo = new STARTUPINFOW() { cb = (uint)sizeof(STARTUPINFOW) };
        var processInfo = new PROCESS_INFORMATION();

        char[] cmdline = [.. arguments, '\0'];

        fixed (char* penv = GetEnvironmentString(settings.Environment))
        fixed (char* cmdlinePtr = cmdline)
        {
            if (!PInvoke.CreateProcessWithToken((HANDLE)tokenHandle.DangerousGetHandle(), CREATE_PROCESS_LOGON_FLAGS.LOGON_WITH_PROFILE,
                null, new PWSTR(cmdlinePtr), GetWin32ProcessCreationFlags(flags, settings), penv, null, &startupInfo, &processInfo))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        var processHandle = new SafeFileHandle(processInfo.hProcess, true);

        mainThreadHandle = new SafeFileHandle(processInfo.hThread, true);
        return new Win32Process(processHandle, processInfo.dwProcessId);
    }

    private unsafe static Win32Process CreateProcessWithJobAssigned(string arguments, ProcessCreationFlags flags,
        Win32ProcessSettings settings, SafeHandle jobHandle, out SafeHandle mainThreadHandle)
    {
        var procThreadAttrList = new LPPROC_THREAD_ATTRIBUTE_LIST();

        nuint procThreadAttrListSize = 0;
        if (!PInvoke.InitializeProcThreadAttributeList(procThreadAttrList, 1, 0, &procThreadAttrListSize) &&
            Marshal.GetLastWin32Error() is var err && err != (int)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
        {
            throw new Win32Exception(err);
        }

        var procThreadAttrListPtr = (void*)Marshal.AllocHGlobal((nint)procThreadAttrListSize);
        try
        {
            procThreadAttrList = new LPPROC_THREAD_ATTRIBUTE_LIST(procThreadAttrListPtr);
            if (!PInvoke.InitializeProcThreadAttributeList(procThreadAttrList, 1, 0, &procThreadAttrListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            bool jobHandleAddRef = false;
            try
            {
                jobHandle.DangerousAddRef(ref jobHandleAddRef);
                var rawJobHandle = jobHandle.DangerousGetHandle();
                if (!PInvoke.UpdateProcThreadAttribute(procThreadAttrList, 0, PInvoke.PROC_THREAD_ATTRIBUTE_JOB_LIST, &rawJobHandle,
                        (nuint)sizeof(nuint), null, (nuint*)null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var startupInfo = new STARTUPINFOEXW()
                {
                    StartupInfo = new STARTUPINFOW() { cb = (uint)sizeof(STARTUPINFOEXW) },
                    lpAttributeList = procThreadAttrList
                };
                var processInfo = new PROCESS_INFORMATION();
                var processCreationFlags = PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT | GetWin32ProcessCreationFlags(flags, settings);

                char[] cmdline = [.. arguments, '\0'];

                fixed (char* penv = GetEnvironmentString(settings.Environment))
                fixed (char* cmdlinePtr = cmdline)
                {
                    if (!PInvoke.CreateProcess(null, new PWSTR(cmdlinePtr), null, null, true, processCreationFlags, null, null,
                        (STARTUPINFOW*)&startupInfo, &processInfo))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{nameof(PInvoke.CreateProcess)} failed.");
                    }
                }

                mainThreadHandle = new SafeFileHandle(processInfo.hThread, true);
                return new Win32Process(new SafeFileHandle(processInfo.hProcess, true), processInfo.dwProcessId);
            }
            finally
            {
                PInvoke.DeleteProcThreadAttributeList(procThreadAttrList);

                if (jobHandleAddRef)
                {
                    jobHandle.DangerousRelease();
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal((nint)procThreadAttrListPtr);
        }
    }

    private static unsafe HMODULE GetModuleHandle(SafeHandle processHandle, bool isWow64, string moduleName)
    {
        const uint MaxModulesNumber = 256;

        var moduleHandles = stackalloc HMODULE[(int)MaxModulesNumber];
        uint cb = MaxModulesNumber * (uint)Marshal.SizeOf<HMODULE>();
        uint cbNeeded = 0;

        bool processHandleAddRef = false;
        try
        {
            processHandle.DangerousAddRef(ref processHandleAddRef);
            var processRawHandle = (HANDLE)processHandle.DangerousGetHandle();
            PInvoke.EnumProcessModulesEx(processRawHandle, moduleHandles, cb, &cbNeeded,
                isWow64 ? ENUM_PROCESS_MODULES_EX_FLAGS.LIST_MODULES_32BIT : ENUM_PROCESS_MODULES_EX_FLAGS.LIST_MODULES_64BIT);

            if (cb >= cbNeeded)
            {
                moduleName = Path.DirectorySeparatorChar + moduleName.ToUpper();
                var nameBuffer = stackalloc char[(int)PInvoke.MAX_PATH];
                foreach (var iterModuleHandle in new Span<HMODULE>(moduleHandles, (int)(cbNeeded / Marshal.SizeOf<HMODULE>())))
                {
                    if (PInvoke.GetModuleFileNameEx(processRawHandle, iterModuleHandle, nameBuffer,
                            PInvoke.MAX_PATH) is var iterModuleNameLength && iterModuleNameLength > moduleName.Length)
                    {
                        var iterModuleNameSpan = new Span<char>(nameBuffer, (int)iterModuleNameLength);
                        if (IsTheRightModule(iterModuleNameSpan))
                        {
                            return iterModuleHandle;
                        }
                    }
                }

                bool IsTheRightModule(ReadOnlySpan<char> m)
                {
                    var moduleNameSpan = moduleName.AsSpan();
                    for (int i = 0; i < moduleNameSpan.Length; i++)
                    {
                        if (char.ToUpper(m[i + m.Length - moduleNameSpan.Length]) != moduleNameSpan[i])
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }

            return (HMODULE)nint.Zero;
        }
        finally
        {
            if (processHandleAddRef)
            {
                processHandle.DangerousRelease();
            }
        }
    }

    private static unsafe uint GetModuleExportRva(SafeHandle processHandle, bool isWow64, nint moduleBase, string functionName)
    {
        var exportDirectory = ReadExportDirectory(processHandle, isWow64, moduleBase);

        if (TryFindingOrdinal(out var ordinal))
        {
            var ordinalBase = exportDirectory.Base;
            var functionAddresses = new uint[exportDirectory.NumberOfFunctions];
            fixed (uint* functionAddressesPtr = functionAddresses)
            {
                if (!PInvoke.ReadProcessMemory(processHandle.ToHANDLE(), (void*)(moduleBase + exportDirectory.AddressOfFunctions),
                    functionAddressesPtr, (nuint)(functionAddresses.Length * sizeof(uint)), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            return functionAddresses[ordinal - ordinalBase];
        }

        return 0;


        bool TryFindingOrdinal(out uint ordinal)
        {
            var nameAddresses = new uint[exportDirectory.NumberOfNames];
            fixed (uint* namedAddressesPtr = nameAddresses)
            {
                if (!PInvoke.ReadProcessMemory(processHandle.ToHANDLE(), (void*)(moduleBase + exportDirectory.AddressOfNames),
                    namedAddressesPtr, (nuint)(nameAddresses.Length * sizeof(uint)), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            var namedOrdinals = new ushort[exportDirectory.NumberOfNames];
            fixed (ushort* namedOrdinalsPtr = namedOrdinals)
            {
                if (!PInvoke.ReadProcessMemory(processHandle.ToHANDLE(), (void*)(moduleBase + exportDirectory.AddressOfNameOrdinals),
                    namedOrdinalsPtr, (nuint)(namedOrdinals.Length * sizeof(ushort)), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            var ordinalBase = exportDirectory.Base;
            var buffer = stackalloc byte[(int)PInvoke.MAX_PATH];
            for (int i = 0; i < nameAddresses.Length; i++)
            {
                if (!PInvoke.ReadProcessMemory(processHandle.ToHANDLE(), (void*)(moduleBase + nameAddresses[i]), buffer, PInvoke.MAX_PATH, null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                // make sure that we always have a null-terminated string
                buffer[PInvoke.MAX_PATH - 1] = 0;
                var name = Marshal.PtrToStringAnsi((nint)buffer) ?? "";
                if (string.Equals(name, functionName, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = ordinalBase + namedOrdinals[i];
                    return true;
                }
            }

            ordinal = 0;
            return false;
        }

        static IMAGE_EXPORT_DIRECTORY ReadExportDirectory(SafeHandle processHandle, bool isWow64, nint moduleBase)
        {
            IMAGE_DATA_DIRECTORY ReadExportDirectoryEntry32(int ntHeaderOffset)
            {
                IMAGE_NT_HEADERS32 inh;
                if (!PInvoke.ReadProcessMemory(processHandle.ToHANDLE(), (void*)(moduleBase + ntHeaderOffset), &inh,
                    (nuint)Marshal.SizeOf<IMAGE_NT_HEADERS32>(), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return inh.OptionalHeader.DataDirectory.AsReadOnlySpan()[(int)IMAGE_DIRECTORY_ENTRY.IMAGE_DIRECTORY_ENTRY_EXPORT];
            }

            IMAGE_DATA_DIRECTORY ReadExportDirectoryEntry64(int ntHeaderOffset)
            {
                IMAGE_NT_HEADERS64 inh;
                if (!PInvoke.ReadProcessMemory(processHandle.ToHANDLE(), (void*)(moduleBase + ntHeaderOffset), &inh,
                    (nuint)Marshal.SizeOf<IMAGE_NT_HEADERS64>(), null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return inh.OptionalHeader.DataDirectory.AsReadOnlySpan()[(int)IMAGE_DIRECTORY_ENTRY.IMAGE_DIRECTORY_ENTRY_EXPORT];
            }

            IMAGE_DOS_HEADER idh;
            if (!PInvoke.ReadProcessMemory(processHandle.ToHANDLE(), (void*)moduleBase, &idh, (nuint)Marshal.SizeOf<IMAGE_DOS_HEADER>(), null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var exportDirectoryEntry = isWow64 ? ReadExportDirectoryEntry32(idh.e_lfanew) : ReadExportDirectoryEntry64(idh.e_lfanew);

            IMAGE_EXPORT_DIRECTORY exportDirectory;
            if (!PInvoke.ReadProcessMemory(processHandle.ToHANDLE(), (void*)(moduleBase + exportDirectoryEntry.VirtualAddress), &exportDirectory,
                (nuint)Marshal.SizeOf<IMAGE_EXPORT_DIRECTORY>(), null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return exportDirectory;
        }
    }

    private static unsafe bool IsProcessWow64(SafeHandle processHandle)
    {
        nint isWow64 = 0;

        uint returnLength = 0;

        bool processHandleAddRef = false;
        try
        {
            processHandle.DangerousAddRef(ref processHandleAddRef);
            CheckWin32Result(PInvokeWdk.NtQueryInformationProcess(
                (HANDLE)processHandle.DangerousGetHandle(), PROCESSINFOCLASS.ProcessWow64Information,
                &isWow64, (uint)nint.Size, ref returnLength));
            Debug.Assert(nint.Size == returnLength);

            return isWow64 != 0;
        }
        finally
        {
            if (processHandleAddRef)
            {
                processHandle.DangerousRelease();
            }
        }
    }

    private unsafe static string GetProcessCommandLine(SafeHandle processHandle)
    {
        uint bufferLength = (uint)sizeof(UNICODE_STRING) + PInvoke.MAX_PATH * sizeof(char);
        var buffer = NativeMemory.Alloc(bufferLength);
        bool processHandleAddRef = false;
        try
        {
            processHandle.DangerousAddRef(ref processHandleAddRef);
            var nativeProcessHandle = (HANDLE)processHandle.DangerousGetHandle();
            uint returnLength = 0;
            if (PInvokeWdk.NtQueryInformationProcess(nativeProcessHandle, PROCESSINFOCLASS.ProcessCommandLineInformation, buffer,
                bufferLength, &returnLength) is var ntstatus && ntstatus == NTSTATUS.STATUS_INFO_LENGTH_MISMATCH)
            {
                bufferLength = returnLength;
                buffer = NativeMemory.Realloc(buffer, bufferLength);
                ntstatus = PInvokeWdk.NtQueryInformationProcess(nativeProcessHandle,
                    PROCESSINFOCLASS.ProcessCommandLineInformation, buffer, bufferLength, &returnLength);
            }

            if (ntstatus != NTSTATUS.STATUS_SUCCESS)
            {
                throw new Win32Exception((int)PInvoke.RtlNtStatusToDosError(ntstatus));
            }

            var unicodeStringPtr = (UNICODE_STRING*)buffer;
            return new string(unicodeStringPtr->Buffer, 0, unicodeStringPtr->Length / sizeof(char));
        }
        finally
        {
            if (processHandleAddRef)
            {
                processHandle.DangerousRelease();
            }

            NativeMemory.Free(buffer);
        }
    }
}