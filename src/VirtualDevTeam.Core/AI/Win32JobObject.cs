using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Windows Job Object wrapper used for agentic-session process containment
/// (<c>p3-cleanup-impl</c>). On Windows, an instance creates a Job Object with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> + <c>DIE_ON_UNHANDLED_EXCEPTION</c>
/// and optional memory / active-process caps. Assigning a process attaches it
/// (and all descendants that haven't opted out) to the job; disposing the wrapper
/// closes the job handle and the OS kernel atomically terminates every process
/// still in the job.
///
/// On non-Windows, all methods are no-ops that return <c>false</c> from
/// <see cref="IsSupported"/>. Callers must keep the existing
/// <c>Process.Kill(entireProcessTree: true)</c> fallback for cross-platform
/// behavior.
/// </summary>
public sealed class Win32JobObject : IDisposable
{
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private IntPtr _handle = IntPtr.Zero;
    private readonly ILogger? _logger;
    private bool _disposed;

    public Win32JobObject(ILogger? logger = null, long memoryLimitBytes = 0, int activeProcessLimit = 0)
    {
        _logger = logger;
        if (!IsSupported) return;

        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        // Build limits. KILL_ON_JOB_CLOSE is the critical flag: closing the last
        // handle to the job kills every process still assigned to it.
        //
        // We deliberately do NOT set BREAKAWAY_OK or SILENT_BREAKAWAY_OK. For the
        // agentic threat model, any child that calls CreateProcess with
        // CREATE_BREAKAWAY_FROM_JOB would otherwise escape containment — and the
        // sandbox assumption is that every descendant must stay reaped.
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
            JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        if (memoryLimitBytes > 0)
        {
            info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            info.ProcessMemoryLimit = (UIntPtr)memoryLimitBytes;
        }
        if (activeProcessLimit > 0)
        {
            info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
            info.BasicLimitInformation.ActiveProcessLimit = (uint)activeProcessLimit;
        }

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Attaches the given process (and, implicitly, its future descendants) to
    /// the job. Must be called after <c>Process.Start()</c> but before the
    /// process spawns children. Returns <c>false</c> on non-Windows.
    /// </summary>
    public bool AssignProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!IsSupported || _handle == IntPtr.Zero) return false;
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            var err = Marshal.GetLastWin32Error();
            _logger?.LogWarning("AssignProcessToJobObject failed (Win32 {Err}); falling back to tree-kill", err);
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            // Closing the handle triggers KILL_ON_JOB_CLOSE for every remaining
            // process in the job — the kernel kills descendants atomically.
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    #region P/Invoke

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}
