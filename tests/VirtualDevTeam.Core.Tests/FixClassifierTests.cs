using VirtualDevTeam.Core.HealthMonitor;
using Xunit;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// T1.6: classifier tests. The classifier decides whether an approved fix can be applied
/// while the runner is up (Live), needs a runner restart (DeferredRestart), or has to be
/// staged for the next boot (Blocked). The rules drive operator-visible behaviour, so each
/// edge case is verified.
/// </summary>
public class FixClassifierTests
{
    // ───────── Live tier ─────────

    [Fact]
    public void Classify_PromptOnlyEdit_IsLive()
    {
        // Prompt template edit — PromptFileWatcher reloads automatically, no restart needed.
        var rec = MakeRec(
            "prompts/researcher/multi-turn-pass-1.md",
            plan: "Update the system prompt in `prompts/researcher/multi-turn-pass-1.md` to include a verification step.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.Live, result.Tier);
        Assert.Contains("config/prompt-only", result.Rationale);
    }

    [Fact]
    public void Classify_AppsettingsKeyChange_IsLive()
    {
        // appsettings.json — IOptionsMonitor reload-on-change picks this up live.
        var rec = MakeRec(
            "src/VirtualDevTeam.Runner/appsettings.json",
            plan: "Change `VirtualDevTeam:FlowMonitor:PollIntervalSeconds` from 60 to 30 in `src/VirtualDevTeam.Runner/appsettings.json`.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.Live, result.Tier);
    }

    [Fact]
    public void Classify_SmeTemplateJson_IsLive()
    {
        // SME definitions — SMEAgentDefinitionService re-reads JSON on demand.
        var rec = MakeRec(
            "prompts/sme-templates/game-engine.json",
            plan: "Update spawn rules in `prompts/sme-templates/game-engine.json`.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.Live, result.Tier);
    }

    // ───────── DeferredRestart tier ─────────

    [Fact]
    public void Classify_CsFileEdit_IsDeferredRestart()
    {
        // .cs source change — runner has to be restarted to load the recompiled assembly.
        var rec = MakeRec(
            "src/VirtualDevTeam.Core/Agents/AgentBase.cs",
            plan: "Add a guard in `AgentBase.cs` to prevent double-stop calls.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.DeferredRestart, result.Tier);
        Assert.Contains("source file", result.Rationale);
    }

    [Fact]
    public void Classify_RazorFile_IsDeferredRestart()
    {
        // .razor view — Razor runtime compilation is not enabled, so restart needed.
        var rec = MakeRec(
            "src/VirtualDevTeam.Dashboard/Components/Pages/Approvals.razor",
            plan: "Add a tier badge to `Approvals.razor`.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.DeferredRestart, result.Tier);
    }

    [Fact]
    public void Classify_MixedCsAndPromptEdits_PicksDeferredRestart()
    {
        // Mixed paths: .cs requires a restart even if the prompt edit alone would be Live.
        // The DeferredRestart rule wins so the operator never sees stale code paired with new prompts.
        var rec = MakeRec(
            "src/VirtualDevTeam.Core/Agents/AgentBase.cs, prompts/researcher/multi-turn-pass-1.md",
            plan: "Update both files for the new flow.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.DeferredRestart, result.Tier);
        Assert.Equal(2, result.AffectedFiles.Count);
    }

    [Fact]
    public void Classify_RazorCsCodeBehind_IsDeferredRestartNotLive()
    {
        // Razor code-behind is a .cs file — must NOT slip through as Live just because the
        // path contains "razor" in the segment name.
        var rec = MakeRec(
            "src/VirtualDevTeam.Dashboard/Components/Pages/HealthMonitor.razor.cs",
            plan: "Add a method to the code-behind for `HealthMonitor.razor.cs`.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.DeferredRestart, result.Tier);
    }

    [Fact]
    public void Classify_UpperCaseCsExtension_IsCaseInsensitive()
    {
        // Some authors emit paths with uppercase extensions (`AgentBase.CS`). Don't let
        // that slip through into the Live tier just because we matched on lowercase.
        var rec = MakeRec(
            "src/VirtualDevTeam.Core/Agents/AgentBase.CS",
            plan: "Edit `src/VirtualDevTeam.Core/Agents/AgentBase.CS`.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.DeferredRestart, result.Tier);
    }

    // ───────── Blocked tier ─────────

    [Fact]
    public void Classify_CsprojChange_IsBlocked()
    {
        // .csproj edits add NuGet packages or change build config — runner can't reload these.
        var rec = MakeRec(
            "src/VirtualDevTeam.Core/VirtualDevTeam.Core.csproj",
            plan: "Add a NuGet reference to `Microsoft.Playwright` in the .csproj.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.Blocked, result.Tier);
        Assert.Contains("dependency", result.Rationale);
    }

    [Fact]
    public void Classify_PackageLockJson_IsBlocked()
    {
        // package-lock.json reflects npm dependency state — runner can't apply this live.
        var rec = MakeRec(
            "package-lock.json",
            plan: "Bump `@playwright/test` in `package-lock.json` to v1.42.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.Blocked, result.Tier);
    }

    [Fact]
    public void Classify_DbMigrationFile_IsBlocked()
    {
        // SQL migration / schema files — apply at next runner boot, not while running.
        var rec = MakeRec(
            "src/VirtualDevTeam.Core/Persistence/Migrations/004-add-fix-tier.sql",
            plan: "Add a migration script.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.Blocked, result.Tier);
    }

    [Fact]
    public void Classify_EmptyFileList_IsBlocked()
    {
        // No files identified → cannot scope a live CLI run safely. Default to Blocked so
        // the operator gets a chance to rework the plan with a more specific scope.
        var rec = MakeRec(
            filesToChange: null,
            plan: "Fix the bug somewhere in the codebase — it'll work itself out.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.Blocked, result.Tier);
        Assert.Empty(result.AffectedFiles);
        Assert.Contains("No files identified", result.Rationale);
    }

    [Fact]
    public void Classify_BlockedBeatsDeferredOnMixedPaths()
    {
        // Adding a NuGet AND editing a .cs file: Blocked wins — NuGet add must be applied
        // before runner boot or the .cs edit won't compile.
        var rec = MakeRec(
            "src/VirtualDevTeam.Core/VirtualDevTeam.Core.csproj, src/VirtualDevTeam.Core/Agents/AgentBase.cs",
            plan: "Add a NuGet package and use it in `AgentBase.cs`.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.Blocked, result.Tier);
    }

    // ───────── Edge cases ─────────

    [Fact]
    public void Classify_UnclassifiedFile_DefaultsToDeferredRestart()
    {
        // Random text file with no matching rule — fall back to DeferredRestart (the safe
        // side of the fence: never auto-apply something we can't classify).
        var rec = MakeRec(
            "docs/architecture-notes.txt",
            plan: "Update `docs/architecture-notes.txt`.");
        var result = FixClassifier.Classify(rec);
        Assert.Equal(FixTier.DeferredRestart, result.Tier);
        Assert.Contains("Unclassified", result.Rationale);
    }

    [Fact]
    public void ExtractFiles_ParsesBulletListUnderHeading()
    {
        // Files-to-modify heading style (markdown bullet list) should populate the file list
        // even if the planner didn't emit a structured `Files to change:` line.
        const string plan = """
            ## Problem
            Foo is broken.

            ## Files to modify
            - src/VirtualDevTeam.Core/Agents/AgentBase.cs
            - prompts/researcher/multi-turn-pass-1.md

            ## Verification
            Run tests.
            """;
        var files = FixClassifier.ExtractFiles(plan, filesToChange: null);
        Assert.Contains(files, f => f.Contains("AgentBase.cs"));
        Assert.Contains(files, f => f.Contains("multi-turn-pass-1.md"));
    }

    [Fact]
    public void ExtractFiles_DedupesAcrossSources()
    {
        // Same file appearing in both the structured FilesToChange line and the body's bullet
        // list should appear once in the resulting list — duplicates skew the classifier.
        const string plan = """
            ## Files to modify
            - `src/VirtualDevTeam.Core/Agents/AgentBase.cs`
            """;
        var files = FixClassifier.ExtractFiles(plan, filesToChange: "src/VirtualDevTeam.Core/Agents/AgentBase.cs");
        Assert.Single(files);
    }

    [Fact]
    public void ExtractFiles_IgnoresUrlsAndAnchors()
    {
        // Inline mentions of URLs and anchor links must not be treated as file paths.
        const string plan = """
            See also https://example.com/docs.html and the `#fix-rec-001` anchor.
            ## Files to modify
            - prompts/x.md
            """;
        var files = FixClassifier.ExtractFiles(plan, filesToChange: null);
        Assert.Single(files);
        Assert.Equal("prompts/x.md", files[0]);
    }

    [Fact]
    public void ClassifyFiles_RootAppsettingsJson_IsLive()
    {
        // appsettings.json at project root (no `src/...` prefix) should still be Live.
        var result = FixClassifier.ClassifyFiles(new[] { "appsettings.json" });
        Assert.Equal(FixTier.Live, result.Tier);
    }

    /// <summary>
    /// Build a minimal FixRecommendation suitable for classifier testing — the classifier
    /// only looks at FilesToChange + PlanMarkdown, so other fields are placeholder values.
    /// </summary>
    private static FixRecommendation MakeRec(string? filesToChange, string plan) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            FindingId = "test-finding",
            DetectorId = "test-detector",
            Severity = FlowFindingSeverity.Critical,
            PlanMarkdown = plan,
            FilesToChange = filesToChange,
        };
}
