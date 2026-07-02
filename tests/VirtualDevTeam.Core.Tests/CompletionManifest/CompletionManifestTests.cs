using System.Text.Json;
using VirtualDevTeam.Core.CompletionManifest;

namespace VirtualDevTeam.Core.Tests.CompletionManifest;

/// <summary>
/// Tests for <see cref="VirtualDevTeam.Core.CompletionManifest.CompletionManifest"/> records,
/// <see cref="CompletionManifestReader"/>, <see cref="CompletionManifestWriter"/>,
/// <see cref="CompletionManifestPathResolver"/>, and <see cref="CompletionManifestEnforcement"/>.
/// </summary>
public sealed class CompletionManifestTests : IDisposable
{
    private readonly string _tempDir;

    public CompletionManifestTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vdt-manifest-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static VirtualDevTeam.Core.CompletionManifest.CompletionManifest BuildSample(
        bool fullyImplemented = true, bool stubOk = false)
    {
        return new VirtualDevTeam.Core.CompletionManifest.CompletionManifest
        {
            Version = 1,
            AgentId = "software-engineer-1",
            PrNumber = 1234,
            TaskId = "T03",
            GeneratedAt = new DateTimeOffset(2026, 5, 13, 11, 30, 0, TimeSpan.Zero),
            Exports = new[]
            {
                new ManifestExport
                {
                    File = "client/src/features/pathfinding/index.ts",
                    Symbol = "register",
                    FullyImplemented = fullyImplemented,
                    StubOk = stubOk,
                    Reason = stubOk ? "EventBus not yet available" : null,
                },
            },
            ScenariosImplemented = new[] { "S03", "S04" },
            ScenariosStepsOwned = new[]
            {
                new ManifestScenarioSteps { Scenario = "S03", Steps = new[] { 1, 2, 3, 4 } },
            },
        };
    }

    // ── Serialization round-trip ──────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTrips_AllFields()
    {
        var manifest = BuildSample(fullyImplemented: true);
        var path = Path.Combine(_tempDir, "pr-1234.json");

        await CompletionManifestWriter.WriteAsync(path, manifest);
        var loaded = await CompletionManifestReader.ReadAsync(path);

        Assert.NotNull(loaded);
        Assert.Equal(manifest.Version, loaded.Version);
        Assert.Equal(manifest.AgentId, loaded.AgentId);
        Assert.Equal(manifest.PrNumber, loaded.PrNumber);
        Assert.Equal(manifest.TaskId, loaded.TaskId);
        Assert.Equal(manifest.GeneratedAt, loaded.GeneratedAt);
        Assert.Equal(manifest.ScenariosImplemented, loaded.ScenariosImplemented);
        Assert.Single(loaded.Exports);
        Assert.Equal("register", loaded.Exports[0].Symbol);
        Assert.Equal("client/src/features/pathfinding/index.ts", loaded.Exports[0].File);
        Assert.True(loaded.Exports[0].FullyImplemented);
        Assert.False(loaded.Exports[0].StubOk);
        Assert.Null(loaded.Exports[0].Reason);
    }

    [Fact]
    public async Task WriteAsync_ProducesSnakeCaseJson()
    {
        var manifest = BuildSample(fullyImplemented: false, stubOk: true);
        var path = Path.Combine(_tempDir, "pr-1234-snake.json");

        await CompletionManifestWriter.WriteAsync(path, manifest);
        var json = await File.ReadAllTextAsync(path);

        // Snake_case property names must appear in the output
        Assert.Contains("\"agent_id\"", json);
        Assert.Contains("\"pr_number\"", json);
        Assert.Contains("\"task_id\"", json);
        Assert.Contains("\"fully_implemented\"", json);
        Assert.Contains("\"stub_ok\"", json);
        Assert.Contains("\"scenarios_implemented\"", json);
        Assert.Contains("\"scenarios_steps_owned\"", json);
        Assert.Contains("\"generated_at\"", json);

        // PascalCase must NOT appear
        Assert.DoesNotContain("\"AgentId\"", json);
        Assert.DoesNotContain("\"PrNumber\"", json);
    }

    [Fact]
    public async Task ReadAsync_HandlesStubOkExport_CorrectlyDeserialized()
    {
        var manifest = BuildSample(fullyImplemented: false, stubOk: true);
        var path = Path.Combine(_tempDir, "pr-stub-ok.json");

        await CompletionManifestWriter.WriteAsync(path, manifest);
        var loaded = await CompletionManifestReader.ReadAsync(path);

        Assert.NotNull(loaded);
        Assert.False(loaded.Exports[0].FullyImplemented);
        Assert.True(loaded.Exports[0].StubOk);
        Assert.Equal("EventBus not yet available", loaded.Exports[0].Reason);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var result = await CompletionManifestReader.ReadAsync(Path.Combine(_tempDir, "nonexistent.json"));
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAsync_CreatesParentDirectory_WhenMissing()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "deeply", ".completion-manifests", "pr-99.json");
        var manifest = BuildSample();

        await CompletionManifestWriter.WriteAsync(nestedPath, manifest);

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_PreservesScenarioSteps()
    {
        var manifest = BuildSample();
        var path = Path.Combine(_tempDir, "pr-steps.json");

        await CompletionManifestWriter.WriteAsync(path, manifest);
        var loaded = await CompletionManifestReader.ReadAsync(path);

        Assert.NotNull(loaded);
        var step = Assert.Single(loaded.ScenariosStepsOwned);
        Assert.Equal("S03", step.Scenario);
        Assert.Equal(new[] { 1, 2, 3, 4 }, step.Steps);
    }

    [Fact]
    public async Task ReadAsync_MissingOptionalFields_DefaultsCorrectly()
    {
        // Write minimal JSON that omits optional array fields
        var minimalJson = """
            {
              "version": 1,
              "agent_id": "se-2",
              "pr_number": 42,
              "task_id": "T01",
              "exports": [],
              "generated_at": "2026-05-01T00:00:00+00:00"
            }
            """;
        var path = Path.Combine(_tempDir, "pr-minimal.json");
        await File.WriteAllTextAsync(path, minimalJson);

        var loaded = await CompletionManifestReader.ReadAsync(path);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Exports);
        Assert.Empty(loaded.ScenariosImplemented);
        Assert.Empty(loaded.ScenariosStepsOwned);
    }

    // ── PathResolver ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_WorkspaceMode_UsesWorktreePath()
    {
        var path = CompletionManifestPathResolver.Resolve(42, worktreePath: @"C:\agents\se-1\repo");
        Assert.Equal(Path.Combine(@"C:\agents\se-1\repo", ".completion-manifests", "pr-42.json"), path);
    }

    [Fact]
    public void Resolve_ApiOnlyMode_UsesStoragePath()
    {
        var path = CompletionManifestPathResolver.Resolve(99, worktreePath: null, storagePath: @"C:\storage");
        Assert.Equal(Path.Combine(@"C:\storage", ".completion-manifests", "pr-99.json"), path);
    }

    [Fact]
    public void Resolve_BothNull_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CompletionManifestPathResolver.Resolve(1, worktreePath: null, storagePath: null));
    }

    [Fact]
    public void Resolve_WorktreePrecedesStorage_WhenBothProvided()
    {
        var path = CompletionManifestPathResolver.Resolve(7,
            worktreePath: @"C:\worktree",
            storagePath: @"C:\storage");
        Assert.Contains("worktree", path);
    }

    // ── Enforcement ──────────────────────────────────────────────────────────

    [Fact]
    public void Check_ReturnsOk_WhenAllExportsFullyImplemented()
    {
        var manifest = BuildSample(fullyImplemented: true, stubOk: false);
        var result = CompletionManifestEnforcement.Check(manifest);
        Assert.IsType<EnforcementResult.Ok>(result);
    }

    [Fact]
    public void Check_ReturnsOk_WhenStubOkIsTrue()
    {
        var manifest = BuildSample(fullyImplemented: false, stubOk: true);
        var result = CompletionManifestEnforcement.Check(manifest);
        Assert.IsType<EnforcementResult.Ok>(result);
    }

    [Fact]
    public void Check_ReturnsBlockedByStub_WhenNotImplementedAndNotStubOk()
    {
        var manifest = BuildSample(fullyImplemented: false, stubOk: false);
        var result = CompletionManifestEnforcement.Check(manifest);

        var blocked = Assert.IsType<EnforcementResult.BlockedByStub>(result);
        Assert.Single(blocked.Offenders);
        Assert.Equal("register", blocked.Offenders[0].Symbol);
    }

    [Fact]
    public void Check_ReturnsBlockedByStub_OnlyForOffendingExports()
    {
        var manifest = new VirtualDevTeam.Core.CompletionManifest.CompletionManifest
        {
            Version = 1,
            AgentId = "se-1",
            PrNumber = 5,
            TaskId = "T01",
            GeneratedAt = DateTimeOffset.UtcNow,
            Exports = new[]
            {
                new ManifestExport { File = "a.ts", Symbol = "good", FullyImplemented = true, StubOk = false },
                new ManifestExport { File = "b.ts", Symbol = "bad",  FullyImplemented = false, StubOk = false },
                new ManifestExport { File = "c.ts", Symbol = "ok",   FullyImplemented = false, StubOk = true },
            },
        };

        var result = CompletionManifestEnforcement.Check(manifest);
        var blocked = Assert.IsType<EnforcementResult.BlockedByStub>(result);
        Assert.Single(blocked.Offenders);
        Assert.Equal("bad", blocked.Offenders[0].Symbol);
    }

    [Fact]
    public void ShouldBlockReady_ReturnsFalse_WhenAllOk()
    {
        var manifest = BuildSample(fullyImplemented: true);
        Assert.False(CompletionManifestEnforcement.ShouldBlockReady(manifest));
    }

    [Fact]
    public void ShouldBlockReady_ReturnsTrue_WhenBlockedByStub()
    {
        var manifest = BuildSample(fullyImplemented: false, stubOk: false);
        Assert.True(CompletionManifestEnforcement.ShouldBlockReady(manifest));
    }

    [Fact]
    public void Check_ReturnsOk_WhenExportsListIsEmpty()
    {
        var manifest = new VirtualDevTeam.Core.CompletionManifest.CompletionManifest
        {
            Version = 1,
            AgentId = "se-1",
            PrNumber = 1,
            TaskId = "T00",
            GeneratedAt = DateTimeOffset.UtcNow,
            Exports = Array.Empty<ManifestExport>(),
        };
        var result = CompletionManifestEnforcement.Check(manifest);
        Assert.IsType<EnforcementResult.Ok>(result);
    }
}
