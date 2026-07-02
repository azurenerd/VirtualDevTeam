using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.StrategyFramework.Tests;

public class GitWorktreeManagerPathValidationTests
{
    [Theory]
    [InlineData("diff --git a/src/foo.cs b/src/foo.cs", null)]
    [InlineData("diff --git a/../outside.cs b/../outside.cs", "path-escape")]
    [InlineData("diff --git a/.git/config b/.git/config", "dotgit-write")]
    [InlineData("diff --git a/src/.git/config b/src/.git/config", "dotgit-write")]
    [InlineData("diff --git a/foo/.git/hooks/pre-commit b/foo/.git/hooks/pre-commit", "dotgit-write")]
    [InlineData("diff --git a/tests/.evaluator-reserved/secret.cs b/tests/.evaluator-reserved/secret.cs", "reserved-path")]
    [InlineData("diff --git a//etc/passwd b//etc/passwd", "path-escape")]
    public void ValidatePatchPaths_classifies_unsafe_paths(string header, string? expectedPrefix)
    {
        var patch = header + "\nindex e69de29..abc 100644\n--- a/x\n+++ b/x\n";
        var result = GitWorktreeManager.ValidatePatchPaths(patch, "tests/.evaluator-reserved/");

        if (expectedPrefix is null)
            Assert.Null(result);
        else
            Assert.StartsWith(expectedPrefix, result);
    }

    [Fact]
    public void ValidatePatchPaths_returns_null_for_empty_patch()
    {
        Assert.Null(GitWorktreeManager.ValidatePatchPaths("", "tests/.evaluator-reserved/"));
    }

    [Fact]
    public void ValidatePatchPaths_rejects_absolute_windows_path()
    {
        var patch = "diff --git a/C:/Windows/System32/foo.cs b/C:/Windows/System32/foo.cs\n";
        var result = GitWorktreeManager.ValidatePatchPaths(patch, "tests/.evaluator-reserved/");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidatePatchPaths_rejects_symlink_creation()
    {
        // git represents symlinks with file mode 120000. A candidate that creates one
        // can point a regular-looking path outside the worktree and a follow-up write
        // would land outside the sandbox.
        var patch =
            "diff --git a/link b/link\n" +
            "new file mode 120000\n" +
            "index 0000000..abc\n" +
            "--- /dev/null\n" +
            "+++ b/link\n" +
            "@@ -0,0 +1 @@\n" +
            "+/etc/passwd\n";
        var result = GitWorktreeManager.ValidatePatchPaths(patch, "tests/.evaluator-reserved/");
        Assert.NotNull(result);
        Assert.StartsWith("symlink-or-gitlink", result);
    }

    [Fact]
    public void ValidatePatchPaths_rejects_mode_change_to_symlink()
    {
        // Pre-existing regular file flipped to a symlink in the patch — equivalent escape vector.
        var patch =
            "diff --git a/regular b/regular\n" +
            "old mode 100644\n" +
            "new mode 120000\n";
        var result = GitWorktreeManager.ValidatePatchPaths(patch, "tests/.evaluator-reserved/");
        Assert.NotNull(result);
        Assert.StartsWith("symlink-or-gitlink", result);
    }

    [Fact]
    public void ValidatePatchPaths_rejects_gitlink_submodule_creation()
    {
        // mode 160000 = gitlink (submodule pointer) — strategies should never introduce one.
        var patch =
            "diff --git a/sub b/sub\n" +
            "new file mode 160000\n" +
            "index 0000000..deadbee\n";
        var result = GitWorktreeManager.ValidatePatchPaths(patch, "tests/.evaluator-reserved/");
        Assert.NotNull(result);
        Assert.StartsWith("symlink-or-gitlink", result);
    }

    [Fact]
    public void ValidatePatchPaths_rejects_unix_absolute_path_via_leading_slash()
    {
        var patch = "diff --git a//tmp/escape.cs b//tmp/escape.cs\n";
        var result = GitWorktreeManager.ValidatePatchPaths(patch, "tests/.evaluator-reserved/");
        Assert.NotNull(result);
        Assert.StartsWith("path-escape", result);
    }

}
