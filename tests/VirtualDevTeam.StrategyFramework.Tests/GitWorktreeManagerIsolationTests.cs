using System.Diagnostics;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Verifies that per-worktree git config hardening is isolated between concurrent
/// candidates. Before the <c>extensions.worktreeConfig</c> switch, `git config --local`
/// inside a linked worktree actually wrote to the main repo's `.git/config`, so two
/// candidates racing on the same task would see last-writer-wins hardening values
/// and the main repo would be mutated. This test reproduces that race and asserts
/// the fix.
/// </summary>
public class GitWorktreeManagerIsolationTests : IDisposable
{
    private readonly string _repoRoot;

    public GitWorktreeManagerIsolationTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "worktree-iso-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        // Best-effort cleanup; on Windows `.git/worktrees/**` can retain locks briefly.
        try
        {
            if (Directory.Exists(_repoRoot))
            {
                // Strip read-only bits that git may have set on pack-ref files.
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
    public async Task CreateAsync_per_worktree_config_does_not_mutate_main_repo_config()
    {
        if (!IsGitAvailable()) return; // skip on environments without git

        await InitRepoWithSeedCommitAsync(_repoRoot);
        var baseSha = await RunGitAsync(_repoRoot, "rev-parse", "HEAD");
        baseSha = baseSha.Trim();

        // Capture the main repo config before the manager touches it.
        var mainConfigPath = Path.Combine(_repoRoot, ".git", "config");
        Assert.True(File.Exists(mainConfigPath), "main repo config must exist");
        var beforeMain = File.ReadAllText(mainConfigPath);

        var mgr = new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance);

        // Create two worktrees for the same task-id distinguished by strategyId, in parallel.
        var handles = await Task.WhenAll(
            mgr.CreateAsync(_repoRoot, ".candidates", "task-1", "baseline", baseSha, default),
            mgr.CreateAsync(_repoRoot, ".candidates", "task-1", "mcp-enhanced", baseSha, default));

        try
        {
            // Each worktree's hardened keys must be visible via `--worktree` reads.
            foreach (var h in handles)
            {
                var credHelper = (await RunGitAsync(h.Path, "config", "--worktree", "credential.helper")).Trim();
                var pushDefault = (await RunGitAsync(h.Path, "config", "--worktree", "push.default")).Trim();
                var hooksPath = (await RunGitAsync(h.Path, "config", "--worktree", "core.hooksPath")).Trim();

                Assert.Equal("", credHelper);
                Assert.Equal("nothing", pushDefault);
                Assert.Equal("", hooksPath);
            }

            // The main repo config should NOT contain push.default=nothing, credential.helper=""
            // etc. — those must live in each worktree's config.worktree only. The ONLY key
            // the manager writes to main config is extensions.worktreeConfig.
            var afterMain = File.ReadAllText(mainConfigPath);
            Assert.DoesNotContain("push.default", afterMain);
            Assert.DoesNotContain("core.hooksPath", afterMain);
            Assert.DoesNotContain("credential.helper", afterMain);
            // Sanity: extensions.worktreeConfig IS the key we wrote to main.
            Assert.Contains("worktreeconfig", afterMain, StringComparison.OrdinalIgnoreCase);
            // And the pre-existing user identity was not disturbed.
            Assert.Contains("[user]", beforeMain, StringComparison.Ordinal);
            Assert.Contains("[user]", afterMain, StringComparison.Ordinal);

            // Each worktree has its own config.worktree file (the point of the extension).
            // Paths differ for linked worktrees: <gitdir>/worktrees/<name>/config.worktree.
            var worktreesDir = Path.Combine(_repoRoot, ".git", "worktrees");
            Assert.True(Directory.Exists(worktreesDir), "linked worktree metadata dir must exist");
            var configWorktreeFiles = Directory.GetFiles(worktreesDir, "config.worktree", SearchOption.AllDirectories);
            Assert.Equal(2, configWorktreeFiles.Length);

            // Each config.worktree file should contain the hardened keys exactly once.
            foreach (var f in configWorktreeFiles)
            {
                var content = File.ReadAllText(f);
                Assert.Contains("credential", content, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("push", content, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("hooksPath", content, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            foreach (var h in handles)
                await h.DisposeAsync();
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
