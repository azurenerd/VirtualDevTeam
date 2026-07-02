using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VirtualDevTeam.Core.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Tests for <see cref="SandboxPostRunValidator"/> (<c>p3-sandbox-hardening</c>):
/// reparse-point detection, host-gitconfig drift detection, sandbox-gitconfig
/// integrity check, path canonicalization helper.
/// </summary>
public class SandboxPostRunValidatorTests : IDisposable
{
    private readonly string _worktree;
    private readonly string _sandboxGitconfig;

    public SandboxPostRunValidatorTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "as-val-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
        _sandboxGitconfig = Path.Combine(_worktree, ".sandbox", "gitconfig");
        Directory.CreateDirectory(Path.Combine(_worktree, ".sandbox"));
        File.WriteAllText(_sandboxGitconfig, "# empty");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_worktree)) Directory.Delete(_worktree, recursive: true); } catch { }
    }

    [Fact]
    public void Clean_worktree_produces_no_violations()
    {
        File.WriteAllText(Path.Combine(_worktree, "readme.md"), "hello");
        Directory.CreateDirectory(Path.Combine(_worktree, "src"));
        File.WriteAllText(Path.Combine(_worktree, "src", "app.cs"), "class C {}");

        var validator = new SandboxPostRunValidator(NullLogger.Instance);
        var snap = SandboxPostRunValidator.TakeSnapshot();
        var violations = validator.Validate(_worktree, snap, _sandboxGitconfig);

        Assert.Empty(violations);
    }

    [Fact]
    public void Sandbox_gitconfig_deleted_is_flagged()
    {
        File.Delete(_sandboxGitconfig);
        var validator = new SandboxPostRunValidator(NullLogger.Instance);
        var snap = SandboxPostRunValidator.TakeSnapshot();
        var violations = validator.Validate(_worktree, snap, _sandboxGitconfig);

        Assert.Contains(violations, v => v.Code == "sandbox-gitconfig-removed");
    }

    [Fact]
    public void Host_gitconfig_drift_is_flagged()
    {
        // Fake drift by constructing a snapshot whose hash doesn't match current.
        // Use HashHostGitconfigs() to get the real path set, then overwrite
        // every hash with zeros.
        var zeros = SandboxPostRunValidator.HashHostGitconfigs()
            .ToDictionary(
                kvp => kvp.Key,
                _ => "0000000000000000000000000000000000000000000000000000000000000000",
                StringComparer.OrdinalIgnoreCase);
        var fakeSnap = new SandboxSnapshot(
            HostGitconfigHashes: zeros,
            WorktreeGitFileHash: null,
            TakenAtUtc: DateTime.UtcNow);
        var validator = new SandboxPostRunValidator(NullLogger.Instance);
        var violations = validator.Validate(_worktree, fakeSnap, _sandboxGitconfig);

        // Unless the host's current hash is coincidentally all zeros, this fires.
        if (zeros.Count > 0)
            Assert.Contains(violations, v => v.Code == "host-gitconfig-drift");
    }

    [Fact]
    public void Reparse_point_inside_worktree_is_flagged_on_windows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // Create a junction inside the worktree pointing at %SystemRoot%. Use
        // `cmd /c mklink /J` because symlinks need dev-mode / admin.
        var link = Path.Combine(_worktree, "evil-junction");
        var target = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(5000);
        if (p.ExitCode != 0 || !Directory.Exists(link))
            return; // junction creation not permitted in this env — skip

        var validator = new SandboxPostRunValidator(NullLogger.Instance);
        var snap = SandboxPostRunValidator.TakeSnapshot();
        var violations = validator.Validate(_worktree, snap, _sandboxGitconfig);

        Assert.Contains(violations, v => v.Code == "reparse-point");
    }

    [Fact]
    public void Reparse_scan_skips_sandbox_directory()
    {
        // The .sandbox dir we create ourselves must not trip the scanner.
        Directory.CreateDirectory(Path.Combine(_worktree, ".sandbox", "home"));
        File.WriteAllText(Path.Combine(_worktree, ".sandbox", "home", "file.txt"), "x");

        var validator = new SandboxPostRunValidator(NullLogger.Instance);
        var snap = SandboxPostRunValidator.TakeSnapshot();
        var violations = validator.Validate(_worktree, snap, _sandboxGitconfig);

        // No reparse-point violations — only the sandbox files exist.
        Assert.DoesNotContain(violations, v => v.Code == "reparse-point");
    }
}
