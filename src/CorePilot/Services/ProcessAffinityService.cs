using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CorePilot.Interop;

namespace CorePilot.Services;

public sealed record ProcessAffinityInfo(
    int Pid,
    string Name,
    ushort Group,
    ulong AffinityMask,
    ulong SystemMask,
    bool CanQuery,
    bool CanSet,
    bool SpansMultipleGroups,
    string? AccessError);

public sealed class ProcessAffinityException : Exception
{
    public ProcessAffinityException(string message) : base(message) { }
}

/// <summary>
/// Reads and sets process affinity with Get/SetProcessAffinityMask. Masks are
/// relative to the process's processor group, exactly like Task Manager's.
/// </summary>
public sealed class ProcessAffinityService
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotFound = 1168;

    public Task<IReadOnlyList<ProcessAffinityInfo>> EnumerateAsync() => Task.Run(Enumerate);

    public IReadOnlyList<ProcessAffinityInfo> Enumerate()
    {
        var result = new List<ProcessAffinityInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var pid = process.Id;
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch
                {
                    name = $"PID {pid}";
                }

                var info = Query(pid, name);
                if (info is not null) result.Add(info);
            }
        }

        result.Sort((a, b) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return byName != 0 ? byName : a.Pid.CompareTo(b.Pid);
        });
        return result;
    }

    /// <summary>Returns null when the process vanished during enumeration.</summary>
    private static ProcessAffinityInfo? Query(int pid, string name)
    {
        // Ask for write access up front: a denied open means the process is
        // protected, but a read-only handle may still let us show its mask.
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
            false, pid);
        var canSet = handle != IntPtr.Zero;

        if (handle == IntPtr.Zero)
        {
            var writeError = Marshal.GetLastWin32Error();
            if (writeError is ErrorInvalidParameter or ErrorNotFound) return null;

            handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is ErrorInvalidParameter or ErrorNotFound) return null;
                return new ProcessAffinityInfo(pid, name, 0, 0, 0, false, false, false, Describe(error));
            }
        }

        try
        {
            var group = (ushort)0;
            var spansMultipleGroups = false;
            var groupArray = new ushort[64];
            ushort groupCount = (ushort)groupArray.Length;
            if (NativeMethods.GetProcessGroupAffinity(handle, ref groupCount, groupArray) && groupCount > 0)
            {
                group = groupArray[0];
                spansMultipleGroups = groupCount > 1;
            }

            if (!NativeMethods.GetProcessAffinityMask(handle, out var processMask, out var systemMask)
                || processMask == 0 || systemMask == 0)
            {
                var error = Marshal.GetLastWin32Error();
                return new ProcessAffinityInfo(pid, name, group, 0, 0, false, false, spansMultipleGroups,
                    error == 0
                        ? "Affinity is not readable (the process may span several processor groups)."
                        : Describe(error));
            }

            return new ProcessAffinityInfo(
                pid, name, group, (ulong)processMask, (ulong)systemMask,
                true, canSet, spansMultipleGroups, null);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public void SetAffinity(int pid, ulong affinityMask)
    {
        if (affinityMask == 0)
            throw new ProcessAffinityException("Select at least one CPU core.");

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_INFORMATION | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false, pid);
        if (handle == IntPtr.Zero)
            throw new ProcessAffinityException(DescribeApplyError(Marshal.GetLastWin32Error()));

        try
        {
            if (!NativeMethods.GetProcessAffinityMask(handle, out var processMask, out var systemMask)
                || processMask == 0 || systemMask == 0)
            {
                throw new ProcessAffinityException(
                    "Windows could not read the process affinity. It may span several processor groups, " +
                    "which this tool cannot pin safely.");
            }

            var available = (ulong)systemMask;
            if ((affinityMask & ~available) != 0)
                throw new ProcessAffinityException(
                    "The selected cores are not available in this process's processor group.");

            if (!NativeMethods.SetProcessAffinityMask(handle, (nuint)affinityMask))
                throw new ProcessAffinityException(DescribeApplyError(Marshal.GetLastWin32Error()));
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string Describe(int error) => error switch
    {
        ErrorAccessDenied => "Access denied — protected system process.",
        ErrorInvalidParameter => "Not accessible.",
        ErrorNotFound => "Process has exited.",
        _ => new Win32Exception(error).Message,
    };

    private static string DescribeApplyError(int error) => error switch
    {
        ErrorAccessDenied =>
            "Access denied. This process is protected (System, antimalware, or another user's session).",
        ErrorInvalidParameter =>
            "Windows rejected the affinity mask. The process may have exited or spans several processor groups.",
        ErrorNotFound => "The process has exited.",
        _ => $"Windows error {error}: {new Win32Exception(error).Message}",
    };
}
