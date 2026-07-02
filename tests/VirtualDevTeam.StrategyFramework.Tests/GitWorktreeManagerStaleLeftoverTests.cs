using System.Diagnostics;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Reproduces the Windows worktree-cleanup race that blocked val-e2e: on Windows,
/// copilot/MCP subprocesses can hold file handles inside a candidate worktree after
/// the strategy finished, so <c>DisposeAsync</c>'s <c>git worktree remove --force</c>
/// AND its <c>Directory.Delete</c> fallback both fail. The stale directory then
/// collided with the NEXT task's <c>git worktree add</c>, which aborted with
/// "already exists". The fix: give each invocation a unique path suffix so stale
/// leftovers can't collide.
/// </summary>
public class GitWorktreeManagerStaleLeftoverTests : IDisposable
{
    private readonly string _repoRoot;

    public GitWorktreeManagerStaleLeftoverTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "worktree-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_repoRoot))
            {
                foreach (var file in Directory.EnumerateFiles(_repoRoot, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_repoRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task CreateAsync_succeeds_when_stale_directory_at_same_strategy_path_cannot_be_removed()
    {
        if (!IsGitAvailable()) return;
        await InitRepoWithSeedCommitAsync(_repoRoot);
        var baseSha = (await RunGitAsync(_repoRoot, "rev-parse", "HEAD")).Trim();

        var mgr = new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance);

        // First create + dispose — normal happy path.
        var h1 = await mgr.CreateAsync(_repoRoot, ".candidates", "task-1", "baseline", baseSha, default);
        var h1Path = h1.Path;
        await h1.DisposeAsync();

        // Simulate the "stale leftover" race: an unrelated process recreated a
        // directory at the PREVIOUS-style conflicting path (sans unique suffix)
        // AND a sibling path that would collide if we reused a predictable name.
        // This mimics Windows holding a file handle that prevents cleanup from
        // fully removing the old worktree dir.
        var candidatesRoot = Path.Combine(_repoRoot, ".candidates", "task-1");
        Directory.CreateDirectory(Path.Combine(candidatesRoot, "baseline"));
        File.WriteAllText(Path.Combine(candidatesRoot, "baseline", "stuck-file.txt"), "holds-a-handle-shape");

        // Second create — must NOT throw "already exists", even though a stale
        // bare `baseline` dir exists at the legacy path. With the fix, the new
        // worktree path gets a unique suffix (e.g. `baseline-{8hex}`), so the
        // stale dir is irrelevant.
        var h2 = await mgr.CreateAsync(_repoRoot, ".candidates", "task-1", "baseline", baseSha, default);

        try
        {
            Assert.NotEqual(h1Path, h2.Path); // unique per invocation
            Assert.True(Directory.Exists(h2.Path), "new worktree must exist");
            Assert.Contains("baseline", Path.GetFileName(h2.Path)); // still identifiable as baseline
        }
        finally
        {
            await h2.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_same_taskid_and_strategyid_produces_distinct_paths()
    {
        if (!IsGitAvailable()) return;
        await InitRepoWithSeedCommitAsync(_repoRoot);
        var baseSha = (await RunGitAsync(_repoRoot, "rev-parse", "HEAD")).Trim();

        var mgr = new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance);

        var h1 = await mgr.CreateAsync(_repoRoot, ".candidates", "task-1", "baseline", baseSha, default);
        try
        {
            // Same (taskId, strategyId) — must get a fresh path so it can coexist
            // with the live h1 handle (sequential, but demonstrates uniqueness).
            await h1.DisposeAsync();

            var h2 = await mgr.CreateAsync(_repoRoot, ".candidates", "task-1", "baseline", baseSha, default);
            try
            {
                Assert.NotEqual(h1.Path, h2.Path);
            }
            finally
            {
                await h2.DisposeAsync();
            }
        }
        catch
        {
            await h1.DisposeAsync();
            throw;
        }
    }

    private static bool IsGitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task InitRepoWithSeedCommitAsync(string root)
    {
        await RunGitAsync(root, "init", "-q", "-b", "main");
        await RunGitAsync(root, "config", "user.email", "test@example.com");
        await RunGitAsync(root, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(root, "README.md"), "seed\n");
        await RunGitAsync(root, "add", "-A");
        await RunGitAsync(root, "commit", "-q", "-m", "seed");
    }

    private static async Task<string> RunGitAsync(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stdout = await outTask;
        var stderr = await errTask;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {cwd} ({p.ExitCode}): {stderr}");
        return stdout;
    }
}
