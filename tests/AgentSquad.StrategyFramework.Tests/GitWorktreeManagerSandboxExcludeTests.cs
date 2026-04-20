using System.Diagnostics;
using AgentSquad.Core.Strategies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSquad.StrategyFramework.Tests;

/// <summary>
/// Tests for <see cref="GitWorktreeManager.ExtractPatchAsync"/> sandbox-exclusion
/// behavior. The agentic strategy creates <c>&lt;worktree&gt;/.sandbox/</c> with
/// deeply-nested CLI package files that can exceed Windows MAX_PATH or trip over
/// global .gitignore entries. <c>ExtractPatchAsync</c> must add <c>.sandbox/</c>
/// to the worktree-local info/exclude so subsequent <c>git add -A</c> skips it,
/// while still capturing real candidate file changes.
/// </summary>
public sealed class GitWorktreeManagerSandboxExcludeTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly GitWorktreeManager _mgr;
    private readonly string _baseSha;

    public GitWorktreeManagerSandboxExcludeTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "asq-sbxexcl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repoRoot, "seed.txt"), "seed\n");
        Git("add", "seed.txt");
        Git("commit", "-q", "-m", "init");
        _baseSha = CaptureGit("rev-parse", "HEAD").Trim();
        _mgr = new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ExtractPatchAsync_skips_sandbox_dir_and_captures_real_file_changes()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "new-file.cs"), "public class Foo {}\n");
        var sandbox = Path.Combine(_repoRoot, ".sandbox", "localappdata", "deep");
        Directory.CreateDirectory(sandbox);
        File.WriteAllText(Path.Combine(sandbox, "scaffolding.txt"), "do not stage me\n");

        var diff = await _mgr.ExtractPatchAsync(_repoRoot, _baseSha, CancellationToken.None);

        Assert.Contains("new-file.cs", diff);
        Assert.DoesNotContain(".sandbox", diff);
        Assert.DoesNotContain("scaffolding.txt", diff);
    }

    [Fact]
    public async Task ExtractPatchAsync_is_idempotent_on_sandbox_exclude_entry()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "a.cs"), "a\n");
        Directory.CreateDirectory(Path.Combine(_repoRoot, ".sandbox"));
        File.WriteAllText(Path.Combine(_repoRoot, ".sandbox", "ignore-me.txt"), "x\n");

        _ = await _mgr.ExtractPatchAsync(_repoRoot, _baseSha, CancellationToken.None);
        _ = await _mgr.ExtractPatchAsync(_repoRoot, _baseSha, CancellationToken.None);

        var excludeFile = Path.Combine(_repoRoot, ".git", "info", "exclude");
        var contents = File.Exists(excludeFile) ? File.ReadAllText(excludeFile) : string.Empty;
        var occurrences = contents
            .Split('\n')
            .Count(l => l.Trim().TrimEnd('\r') == ".sandbox/");
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task ExtractPatchAsync_returns_empty_when_only_sandbox_changed()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, ".sandbox", "home"));
        File.WriteAllText(Path.Combine(_repoRoot, ".sandbox", "home", "file"), "x\n");

        var diff = await _mgr.ExtractPatchAsync(_repoRoot, _baseSha, CancellationToken.None);

        Assert.Equal(string.Empty, diff);
    }

    /// <summary>
    /// Regression for the real-world empty-patch bug: the agentic CLI runs
    /// <c>git add -A &amp;&amp; git commit</c> itself during its tool use. After that,
    /// <c>git diff HEAD</c> is empty — but <c>git diff base</c> must still see
    /// everything the strategy produced. This is the core reason ExtractPatchAsync
    /// diffs against the worktree's base SHA rather than HEAD.
    /// </summary>
    [Fact]
    public async Task ExtractPatchAsync_captures_changes_that_strategy_already_committed()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "scaffold.cs"), "public class Bar {}\n");
        File.WriteAllText(Path.Combine(_repoRoot, "other.cs"), "public class Baz {}\n");
        // Simulate the agentic CLI committing its own work.
        Git("add", "-A");
        Git("commit", "-q", "-m", "agentic: scaffold T1");

        var diff = await _mgr.ExtractPatchAsync(_repoRoot, _baseSha, CancellationToken.None);

        Assert.NotEqual(string.Empty, diff);
        Assert.Contains("scaffold.cs", diff);
        Assert.Contains("other.cs", diff);
    }

    /// <summary>
    /// Mixed case: strategy committed some files AND left others uncommitted.
    /// Both must appear in the extracted patch.
    /// </summary>
    [Fact]
    public async Task ExtractPatchAsync_captures_both_committed_and_uncommitted_changes()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "committed.cs"), "public class C {}\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "partial commit");
        File.WriteAllText(Path.Combine(_repoRoot, "uncommitted.cs"), "public class U {}\n");

        var diff = await _mgr.ExtractPatchAsync(_repoRoot, _baseSha, CancellationToken.None);

        Assert.Contains("committed.cs", diff);
        Assert.Contains("uncommitted.cs", diff);
    }

    private string CaptureGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({p.ExitCode}): {p.StandardError.ReadToEnd()}");
        return output;
    }

    private void Git(params string[] args) => CaptureGit(args);
}
