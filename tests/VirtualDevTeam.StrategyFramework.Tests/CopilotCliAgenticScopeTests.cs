using System.IO;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.StrategyFramework.Tests;

/// <summary>
/// Tests for <see cref="CopilotCliAgenticScope"/> (<c>p3-real-sandbox</c>):
/// sandbox-dir materialization, env-override contract, scrub semantics.
/// </summary>
public class CopilotCliAgenticScopeTests : IDisposable
{
    private readonly string _worktree;

    public CopilotCliAgenticScopeTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "as-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_worktree)) Directory.Delete(_worktree, recursive: true); } catch { }
    }

    [Fact]
    public void Prepare_creates_sandbox_directories_and_gitconfig()
    {
        var scope = CopilotCliAgenticScope.Prepare(_worktree);

        Assert.True(Directory.Exists(Path.Combine(_worktree, ".sandbox", "home")));
        Assert.True(Directory.Exists(Path.Combine(_worktree, ".sandbox", "xdg-config")));
        Assert.True(Directory.Exists(Path.Combine(_worktree, ".sandbox", "xdg-cache")));
        Assert.True(Directory.Exists(Path.Combine(_worktree, ".sandbox", "appdata")));
        Assert.True(Directory.Exists(Path.Combine(_worktree, ".sandbox", "localappdata")));
        Assert.True(File.Exists(scope.SandboxGitconfigPath));
        Assert.Contains("intentionally empty", File.ReadAllText(scope.SandboxGitconfigPath));
    }

    [Fact]
    public void Prepare_throws_on_missing_worktree()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            CopilotCliAgenticScope.Prepare(Path.Combine(_worktree, "does-not-exist")));
    }

    [Fact]
    public void Environment_overrides_remap_home_like_vars()
    {
        var scope = CopilotCliAgenticScope.Prepare(_worktree);
        var env = scope.EnvironmentOverrides;

        Assert.Equal(scope.SandboxHomePath, env["HOME"]);
        Assert.Equal(scope.SandboxHomePath, env["USERPROFILE"]);
        Assert.Equal(Path.Combine(_worktree, ".sandbox", "appdata"), env["APPDATA"]);
        Assert.Equal(Path.Combine(_worktree, ".sandbox", "localappdata"), env["LOCALAPPDATA"]);
        Assert.Equal(Path.Combine(_worktree, ".sandbox", "xdg-config"), env["XDG_CONFIG_HOME"]);
        Assert.Equal(Path.Combine(_worktree, ".sandbox", "xdg-cache"), env["XDG_CACHE_HOME"]);
    }

    [Fact]
    public void Environment_overrides_set_git_hardening_vars()
    {
        var scope = CopilotCliAgenticScope.Prepare(_worktree);
        var env = scope.EnvironmentOverrides;

        Assert.Equal("1", env["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal(scope.SandboxGitconfigPath, env["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("Never", env["GCM_INTERACTIVE"]);
    }

    [Fact]
    public void Environment_overrides_scrub_exact_secret_vars()
    {
        var scope = CopilotCliAgenticScope.Prepare(_worktree);
        var env = scope.EnvironmentOverrides;

        // Every exact-match secret must map to null (delete) in the overrides.
        foreach (var key in new[]
        {
            "GIT_ASKPASS", "SSH_ASKPASS", "SSH_AUTH_SOCK",
            "GH_TOKEN", "GITHUB_TOKEN",
            "OPENAI_API_KEY", "ANTHROPIC_API_KEY",
            "COPILOT_API_KEY", "COPILOT_GITHUB_TOKEN",
        })
        {
            Assert.True(env.ContainsKey(key), $"missing scrub for {key}");
            Assert.Null(env[key]);
        }
    }

    [Fact]
    public void Environment_overrides_scrub_prefixed_secret_vars()
    {
        // Inject a fake AZURE_* var so the prefix scrubber has something to find.
        try
        {
            Environment.SetEnvironmentVariable("AZURE_FAKE_TOKEN", "sekret");
            var scope = CopilotCliAgenticScope.Prepare(_worktree);
            Assert.True(scope.EnvironmentOverrides.ContainsKey("AZURE_FAKE_TOKEN"));
            Assert.Null(scope.EnvironmentOverrides["AZURE_FAKE_TOKEN"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_FAKE_TOKEN", null);
        }
    }

    [Fact]
    public void Environment_overrides_do_not_include_preserved_vars()
    {
        // PATH/TEMP/SystemRoot/etc. must inherit — scope should not try to
        // remap them because they are preserved by process-level inheritance.
        var scope = CopilotCliAgenticScope.Prepare(_worktree);
        Assert.False(scope.EnvironmentOverrides.ContainsKey("PATH"));
        Assert.False(scope.EnvironmentOverrides.ContainsKey("PATHEXT"));
        Assert.False(scope.EnvironmentOverrides.ContainsKey("SystemRoot"));
        Assert.False(scope.EnvironmentOverrides.ContainsKey("TEMP"));
        Assert.False(scope.EnvironmentOverrides.ContainsKey("TMP"));
        Assert.False(scope.EnvironmentOverrides.ContainsKey("COMSPEC"));
    }
}
