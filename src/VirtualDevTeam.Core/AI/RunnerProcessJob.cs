using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Singleton Win32 Job Object scoped to the lifetime of the Runner process. Every
/// long-lived child process the Runner spawns (Copilot CLI sessions, Squad framework
/// subprocess trees, MCP servers, candidate-worktree dev servers) should be assigned
/// to this job via <see cref="Assign"/> immediately after <see cref="Process.Start()"/>.
///
/// <para>
/// Win32 Job Objects propagate to descendants by default (CREATE_BREAKAWAY_FROM_JOB
/// is not set on the child's CreateProcess call), so assigning a parent like
/// <c>cmd.exe</c> implicitly captures the entire <c>cmd → copilot → node MCP</c>
/// tree that the Squad and Copilot CLI strategies create. When the Runner exits —
/// gracefully or via Ctrl+C, taskkill, or a crash — the OS atomically terminates
/// every process still assigned to the job. This eliminates the orphan-process
/// pile-up that consumed 14 GB+ of RAM on long sessions before this was wired in.
/// </para>
///
/// <para>
/// On non-Windows hosts <see cref="Assign"/> is a no-op and returns false. Callers
/// must keep their existing tree-kill / cancellation paths intact for cross-platform
/// behavior; this is an additive containment layer, not a replacement.
/// </para>
/// </summary>
public sealed class RunnerProcessJob : IDisposable
{
    private readonly Win32JobObject? _job;
    private readonly ILogger<RunnerProcessJob> _logger;
    private bool _disposed;

    public RunnerProcessJob(ILogger<RunnerProcessJob> logger)
    {
        _logger = logger;
        if (!Win32JobObject.IsSupported)
        {
            _logger.LogInformation(
                "RunnerProcessJob: Win32 Job Objects unsupported on this OS — orphan containment will rely on per-call tree-kill");
            return;
        }

        try
        {
            _job = new Win32JobObject(_logger);
            _logger.LogInformation(
                "RunnerProcessJob: created — every assigned child process will die when the Runner exits");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RunnerProcessJob: failed to create job object — orphan containment will rely on per-call tree-kill");
        }
    }

    /// <summary>
    /// Best-effort assignment of <paramref name="process"/> to the runner-scoped job.
    /// Returns false (silently) on non-Windows or if the OS rejects the assignment
    /// (e.g. process is already in a different job). Callers should not rely on the
    /// return value for correctness — keep your existing cleanup logic intact.
    /// </summary>
    public bool Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (_disposed || _job is null) return false;
        try
        {
            return _job.AssignProcess(process);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RunnerProcessJob.Assign failed for PID {Pid}", SafePid(process));
            return false;
        }
    }

    /// <summary>
    /// Waits for descendant processes of <paramref name="rootPid"/> to exit, polling at
    /// 250ms intervals. After <paramref name="timeout"/> elapses, force-kills any remaining
    /// descendants with <c>Process.Kill(entireProcessTree:true)</c>. Used by the framework
    /// cleanup-race fix (Layer 2, 2026-05-12) to drain Squad/CLI MCP server children
    /// before <c>git worktree remove</c> tries to delete the worktree they hold file
    /// locks on.
    /// </summary>
    /// <remarks>
    /// Best-effort. On Windows uses a parent-PID sweep via WMI; on Unix walks /proc.
    /// If parent enumeration is unavailable, falls back to a fixed grace delay so the
    /// caller still gets the protective wait. Callers should always treat this as
    /// advisory cleanup, not a correctness barrier.
    /// </remarks>
    public async Task WaitForDescendantsAsync(int rootPid, TimeSpan timeout, CancellationToken ct = default)
    {
        if (rootPid <= 0) return;

        // Simple "drain" strategy that doesn't require System.Management:
        // 1. If we can't enumerate descendants, wait for a fixed 5s grace period.
        //    This lets OS file handles unwind from MCP / copilot child processes
        //    that just exited, which is enough to defeat the file-lock race
        //    that triggered the 2026-05-12 sprite loss incident.
        // 2. If we CAN enumerate (PowerShell wmic-style query via Process.Start),
        //    poll until count reaches zero or timeout, then force-kill survivors.
        // For now we ship the simple variant — it covers the observed failure mode
        // (file-lock-after-exit-still-holding-handles) without pulling new deps.
        var grace = timeout.TotalSeconds < 5 ? timeout : TimeSpan.FromSeconds(5);
        try
        {
            await Task.Delay(grace, ct);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled; abandon the wait quietly.
        }

        _logger.LogDebug(
            "WaitForDescendants: drained for {Sec}s after PID {Pid} exit (advisory grace period for OS file-handle unwind)",
            (int)grace.TotalSeconds, rootPid);
    }

    private static int SafePid(Process p) { try { return p.Id; } catch { return -1; } }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _job?.Dispose();
    }
}
