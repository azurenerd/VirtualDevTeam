using System.Diagnostics;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class WinnerApplyServiceTests : IDisposable
{
    private readonly string _repo;

    public WinnerApplyServiceTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "apply-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-q", "-b", "main");
        Git(_repo, "config", "user.email", "t@t");
        Git(_repo, "config", "user.name", "t");
        // Disable autocrlf so generated patch hashes match on Windows.
        Git(_repo, "config", "core.autocrlf", "false");
        Git(_repo, "config", "core.eol", "lf");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# test\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "init");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_repo)) ForceDelete(_repo); } catch { }
    }

    [Fact]
    public async Task ApplyAsync_succeeds_when_head_unchanged()
    {
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();
        var patch = "diff --git a/new.txt b/new.txt\nnew file mode 100644\nindex 0000000..c75bf48\n--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+hello\n";
        var svc = new WinnerApplyService(NullLogger<WinnerApplyService>.Instance);

        var outcome = await svc.ApplyAsync(_repo, "main", baseSha, patch, CancellationToken.None);

        Assert.True(outcome.Applied, $"Failed: {outcome.FailureReason}");
        Assert.True(File.Exists(Path.Combine(_repo, "new.txt")));
    }

    [Fact]
    public async Task ApplyAsync_succeeds_when_head_advanced_linearly()
    {
        // A linear advance (expectedBase is an ancestor of current HEAD) is safe —
        // the ancestor-aware check (commit 3603546) allows the apply to proceed.
        var staleSha = Git(_repo, "rev-parse", "HEAD").Trim();
        // Advance head linearly
        File.WriteAllText(Path.Combine(_repo, "bump.txt"), "bump\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "advance");

        var patch = "diff --git a/x.txt b/x.txt\nnew file mode 100644\n--- /dev/null\n+++ b/x.txt\n@@ -0,0 +1 @@\n+x\n";
        var svc = new WinnerApplyService(NullLogger<WinnerApplyService>.Instance);
        var outcome = await svc.ApplyAsync(_repo, "main", staleSha, patch, CancellationToken.None);

        Assert.True(outcome.Applied, $"Linear advance should succeed: {outcome.FailureReason}");
        Assert.True(File.Exists(Path.Combine(_repo, "x.txt")));
    }

    [Fact]
    public async Task ApplyAsync_rejects_when_head_diverged()
    {
        // Create a divergent branch: expectedBase is NOT an ancestor of current HEAD.
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();
        // Create a side branch and commit
        Git(_repo, "checkout", "-b", "side");
        File.WriteAllText(Path.Combine(_repo, "side.txt"), "side\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "side-commit");
        var sideSha = Git(_repo, "rev-parse", "HEAD").Trim();
        // Go back to main and amend (rewrite history to diverge from side)
        Git(_repo, "checkout", "main");
        File.WriteAllText(Path.Combine(_repo, "rewrite.txt"), "rewrite\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "rewrite");

        // sideSha is NOT an ancestor of current main HEAD — true divergence
        var patch = "diff --git a/x.txt b/x.txt\nnew file mode 100644\n--- /dev/null\n+++ b/x.txt\n@@ -0,0 +1 @@\n+x\n";
        var svc = new WinnerApplyService(NullLogger<WinnerApplyService>.Instance);
        var outcome = await svc.ApplyAsync(_repo, "main", sideSha, patch, CancellationToken.None);

        Assert.False(outcome.Applied);
        Assert.True(outcome.HeadChanged);
    }

    [Fact]
    public async Task ApplyAsync_rejects_empty_patch()
    {
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();
        var svc = new WinnerApplyService(NullLogger<WinnerApplyService>.Instance);
        var outcome = await svc.ApplyAsync(_repo, "main", baseSha, "", CancellationToken.None);
        Assert.False(outcome.Applied);
        Assert.Equal("empty-patch", outcome.FailureReason);
    }

    [Fact]
    public async Task ApplyAsync_handles_delete_patch()
    {
        File.WriteAllText(Path.Combine(_repo, "deleteme.txt"), "bye\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();

        // Capture a working-tree delete patch (no --cached) so applying it later just
        // re-performs the working-tree deletion.
        File.Delete(Path.Combine(_repo, "deleteme.txt"));
        var patch = Git(_repo, "diff", "HEAD", "--binary", "--full-index");
        // Restore working tree so the apply has something to delete.
        Git(_repo, "checkout", "--", "deleteme.txt");
        Assert.True(File.Exists(Path.Combine(_repo, "deleteme.txt")));

        var svc = new WinnerApplyService(NullLogger<WinnerApplyService>.Instance);
        var outcome = await svc.ApplyAsync(_repo, "main", baseSha, patch, CancellationToken.None);

        Assert.True(outcome.Applied, $"Failed: {outcome.FailureReason}");
        Assert.False(File.Exists(Path.Combine(_repo, "deleteme.txt")));
    }

    [Fact]
    public async Task ApplyAsync_handles_rename_patch()
    {
        File.WriteAllText(Path.Combine(_repo, "old.txt"), "content\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed-rename");
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();

        // Stage the rename via git so the diff includes a "rename from/to" header.
        Git(_repo, "mv", "old.txt", "new.txt");
        var patch = Git(_repo, "diff", "--cached", "HEAD", "-M", "--binary", "--full-index");
        // Restore both index and working tree.
        Git(_repo, "reset", "--hard", "HEAD");
        Assert.True(File.Exists(Path.Combine(_repo, "old.txt")));
        Assert.False(File.Exists(Path.Combine(_repo, "new.txt")));

        var svc = new WinnerApplyService(NullLogger<WinnerApplyService>.Instance);
        var outcome = await svc.ApplyAsync(_repo, "main", baseSha, patch, CancellationToken.None);

        Assert.True(outcome.Applied, $"Failed: {outcome.FailureReason}");
        Assert.True(File.Exists(Path.Combine(_repo, "new.txt")));
        Assert.False(File.Exists(Path.Combine(_repo, "old.txt")));
    }

    [Fact]
    public async Task ApplyAsync_handles_binary_patch()
    {
        var binPath = Path.Combine(_repo, "blob.bin");
        var bytes = new byte[] { 0x00, 0xFF, 0x10, 0x20, 0x7F, 0x80, 0xC3, 0xA9, 0x00, 0x01 };
        File.WriteAllBytes(binPath, bytes);
        // Use intent-to-add so `git diff HEAD` includes the new untracked file.
        Git(_repo, "add", "-N", "blob.bin");
        var patch = Git(_repo, "diff", "HEAD", "--binary", "--full-index");
        Assert.False(string.IsNullOrWhiteSpace(patch));
        // Reset working tree + index so the apply has to recreate the file.
        Git(_repo, "reset", "--hard", "HEAD");
        File.Delete(binPath);
        var baseSha = Git(_repo, "rev-parse", "HEAD").Trim();

        var svc = new WinnerApplyService(NullLogger<WinnerApplyService>.Instance);
        var outcome = await svc.ApplyAsync(_repo, "main", baseSha, patch, CancellationToken.None);

        Assert.True(outcome.Applied, $"Failed: {outcome.FailureReason}");
        Assert.True(File.Exists(binPath));
        Assert.Equal(bytes, File.ReadAllBytes(binPath));
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} => {p.ExitCode}: {e}");
        return o;
    }

    private static void ForceDelete(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        { try { File.SetAttributes(f, FileAttributes.Normal); } catch { } }
        Directory.Delete(dir, true);
    }
}
