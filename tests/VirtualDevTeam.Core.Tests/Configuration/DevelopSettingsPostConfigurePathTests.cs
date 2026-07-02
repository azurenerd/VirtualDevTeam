using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Tests.Configuration;

/// <summary>
/// Locks the develop-settings.json candidate-path ordering for
/// <see cref="DevelopSettingsPostConfigure"/>. The regression these guard against (2026-06):
/// a stale copy in the bin output dir (AppContext.BaseDirectory) overrode the operator's source
/// copy on every options-snapshot rebuild, drifting TestEngineerReviews back to true and stalling
/// the workflow at the Testing gate.
/// </summary>
public class DevelopSettingsPostConfigurePathTests
{
    private const string FileName = "develop-settings.json";

    [Fact]
    public void BuildCandidatePaths_CwdPathIsFirst()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "vdt-cwd");
        var baseDir = Path.Combine(Path.GetTempPath(), "vdt-app", "bin", "Debug", "net8.0");

        var candidates = DevelopSettingsPostConfigure.BuildCandidatePaths(cwd, baseDir);

        Assert.Equal(Path.Combine(cwd, FileName), candidates[0]);
    }

    [Fact]
    public void BuildCandidatePaths_NeverIncludesBinBaseDirCopy()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "vdt-cwd");
        var baseDir = Path.Combine(Path.GetTempPath(), "vdt-app", "bin", "Debug", "net8.0");

        var candidates = DevelopSettingsPostConfigure.BuildCandidatePaths(cwd, baseDir);

        // The bin output dir itself must NEVER be a candidate — a stale copy there must not win.
        var binCopy = Path.GetFullPath(Path.Combine(baseDir, FileName));
        Assert.DoesNotContain(candidates, c => string.Equals(
            Path.GetFullPath(c), binCopy, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildCandidatePaths_IncludesProjectDirWalkUpFallback()
    {
        // A dev-time layout: bin/Debug/net8.0 → walk up 3 levels reaches the project dir.
        var projectDir = Path.Combine(Path.GetTempPath(), "vdt-proj");
        var baseDir = Path.Combine(projectDir, "bin", "Debug", "net8.0");
        var cwd = Path.Combine(Path.GetTempPath(), "vdt-elsewhere");

        var candidates = DevelopSettingsPostConfigure.BuildCandidatePaths(cwd, baseDir);

        var expectedProjectCopy = Path.GetFullPath(Path.Combine(projectDir, FileName));
        Assert.Contains(candidates, c => string.Equals(
            Path.GetFullPath(c), expectedProjectCopy, StringComparison.OrdinalIgnoreCase));
    }
}
