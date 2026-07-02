using System.Diagnostics;
using System.Runtime.InteropServices;
using VirtualDevTeam.Core.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Tests for <see cref="Win32JobObject"/> (<c>p3-cleanup-impl</c>). Windows-only
/// assertions are gated on <see cref="Win32JobObject.IsSupported"/>; on other
/// platforms the tests exercise the no-op fallback.
/// </summary>
public class Win32JobObjectTests
{
    [Fact]
    public void IsSupported_matches_runtime_platform()
    {
        Assert.Equal(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), Win32JobObject.IsSupported);
    }

    [Fact]
    public void Construct_and_dispose_does_not_throw()
    {
        using var job = new Win32JobObject(NullLogger.Instance);
    }

    [Fact]
    public void Construct_with_limits_does_not_throw()
    {
        using var job = new Win32JobObject(
            NullLogger.Instance,
            memoryLimitBytes: 2L * 1024 * 1024 * 1024,
            activeProcessLimit: 32);
    }

    [Fact]
    public void AssignProcess_null_throws()
    {
        using var job = new Win32JobObject(NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => job.AssignProcess(null!));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var job = new Win32JobObject(NullLogger.Instance);
        job.Dispose();
        job.Dispose();
    }

    [Fact]
    public void AssignProcess_on_running_process_returns_true_on_windows()
    {
        if (!Win32JobObject.IsSupported)
        {
            // Non-Windows: method must return false cleanly without throwing.
            using var job = new Win32JobObject(NullLogger.Instance);
            using var p = StartSleepingProcess();
            Assert.False(job.AssignProcess(p));
            p.Kill(entireProcessTree: true);
            return;
        }

        using var process = StartSleepingProcess();
        try
        {
            using var job = new Win32JobObject(NullLogger.Instance);
            var assigned = job.AssignProcess(process);
            Assert.True(assigned, "AssignProcessToJobObject should succeed on Windows");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public void Dispose_kills_assigned_process_on_windows()
    {
        if (!Win32JobObject.IsSupported) return; // Not relevant on non-Windows

        var process = StartSleepingProcess();
        try
        {
            var job = new Win32JobObject(NullLogger.Instance);
            Assert.True(job.AssignProcess(process));

            // Disposing the job must cause KILL_ON_JOB_CLOSE to terminate the
            // assigned process. Allow a brief window for the kernel to propagate.
            job.Dispose();

            Assert.True(process.WaitForExit(5000),
                "Process should exit within 5s of Job Object disposal");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
    }

    /// <summary>Starts a long-sleeping, cross-platform child process for test fixtures.</summary>
    private static Process StartSleepingProcess()
    {
        var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe", "/c timeout /t 60 /nobreak")
            : new ProcessStartInfo("sh", "-c \"sleep 60\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardInput = true;
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start helper process");
    }
}
