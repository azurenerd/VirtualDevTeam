using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.MissingWork;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Behavioral tests for <see cref="EventCatalogValidator"/>.
///
/// Tests are structured in two layers:
/// <list type="bullet">
///   <item>Unit tests that call internal static helpers directly (no filesystem I/O),
///         covering catalog parsing, code scanning, and cross-reference rules.</item>
///   <item>Integration tests that write temp files and exercise the full
///         <see cref="EventCatalogValidator.DetectAsync"/> path.</item>
/// </list>
/// </summary>
public sealed class EventCatalogValidatorTests : IDisposable
{
    private static readonly EventCatalogValidator Validator =
        new(NullLogger<EventCatalogValidator>.Instance);

    private static MissingWorkContext EmptyContext(string root) => new()
    {
        WorkspaceRoot = root,
        OpenIssues = Array.Empty<IssueRef>(),
        RecentlyClosedIssues = Array.Empty<IssueRef>(),
    };

    // Temp directory created fresh per test class (xUnit instantiates one class per test).
    private readonly string _tempDir;

    public EventCatalogValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vdt-ecv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // =========================================================================
    // ParseEventCatalog — unit tests (no filesystem)
    // =========================================================================

    [Fact]
    public void ParseEventCatalog_EmptyString_ReturnsEmptyList()
    {
        var result = EventCatalogValidator.ParseEventCatalog("");
        Assert.Null(result);
    }

    [Fact]
    public void ParseEventCatalog_NoSection_ReturnsNull()
    {
        var md = """
            # Architecture

            ## Components
            Some text here.

            ## Data Flow
            More text.
            """;

        var result = EventCatalogValidator.ParseEventCatalog(md);
        Assert.Null(result);
    }

    [Fact]
    public void ParseEventCatalog_WithCatalogSection_ReturnsCatalogEntries()
    {
        var md = """
            # Architecture

            ## Event Catalog

            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            | user:login | auth-service | notification-service, analytics |
            | game:started | game-engine | ui |
            """;

        var result = EventCatalogValidator.ParseEventCatalog(md);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("user:login", result[0].EventName);
        Assert.Contains("auth-service", result[0].Emitters);
        Assert.Contains("notification-service", result[0].Subscribers);
        Assert.Contains("analytics", result[0].Subscribers);
        Assert.Equal("game:started", result[1].EventName);
    }

    [Fact]
    public void ParseEventCatalog_EmptyCells_HandledGracefully()
    {
        var md = """
            ## Event Catalog
            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            | my:event | - | - |
            """;

        var result = EventCatalogValidator.ParseEventCatalog(md);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Empty(result[0].Emitters);
        Assert.Empty(result[0].Subscribers);
    }

    [Fact]
    public void ParseEventCatalog_StopsAtNextHeading()
    {
        var md = """
            ## Event Catalog
            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            | foo | a | b |

            ## Another Section
            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            | bar | c | d |
            """;

        // Should only parse the first section
        var result = EventCatalogValidator.ParseEventCatalog(md);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("foo", result[0].EventName);
    }

    // =========================================================================
    // ScanFileForEventRefs — unit tests (no filesystem)
    // =========================================================================

    [Fact]
    public void ScanFile_EmitPattern_CapturesEventName()
    {
        var lines = new[]
        {
            "eventBus.emit('user:login');",
            "this.emit(\"game:started\");",
            "bus.publish('player:died');",
        };

        var emits = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
        var subs = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
        var ae = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
        var as_ = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);

        EventCatalogValidator.ScanFileForEventRefs(lines, "feature-a/service.ts", emits, subs, ae, as_);

        Assert.True(emits.ContainsKey("user:login"));
        Assert.True(emits.ContainsKey("game:started"));
        Assert.True(emits.ContainsKey("player:died"));
        Assert.Empty(subs);
    }

    [Fact]
    public void ScanFile_SubscribePattern_CapturesEventName()
    {
        var lines = new[]
        {
            "eventBus.on('user:login', handleLogin);",
            "eventBus.subscribe('game:started', () => {});",
        };

        var emits = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
        var subs = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
        var ae = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
        var as_ = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);

        EventCatalogValidator.ScanFileForEventRefs(lines, "feature-b/handler.ts", emits, subs, ae, as_);

        Assert.Empty(emits);
        Assert.True(subs.ContainsKey("user:login"));
        Assert.True(subs.ContainsKey("game:started"));
    }

    [Fact]
    public void ScanFile_ArchContractAnnotation_EmitIsCaptured()
    {
        var lines = new[]
        {
            "// ARCH-CONTRACT: emits=user:login",
            "function doLogin() { }",
        };

        var emits = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
        var subs = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
        var ae = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);
        var as_ = new Dictionary<string, List<CodeEventRef>>(StringComparer.OrdinalIgnoreCase);

        EventCatalogValidator.ScanFileForEventRefs(lines, "auth.ts", emits, subs, ae, as_);

        // Should land in annotation emits, not code emits
        Assert.Empty(emits);
        Assert.True(ae.ContainsKey("user:login"));
        Assert.True(ae["user:login"][0].IsAnnotation);
    }

    // =========================================================================
    // DetectAsync integration tests — use real filesystem temp directories
    // =========================================================================

    [Fact]
    public async Task DetectAsync_MissingArchitectureFile_ProducesNoFindings()
    {
        // Workspace exists but has no Architecture.md
        var ctx = EmptyContext(_tempDir);

        var findings = await Validator.DetectAsync(ctx, default);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_ArchFileWithNoCatalogSection_ProducesNoFindings()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Architecture.md"), """
            # Architecture
            ## Components
            Just some components.
            """);

        var ctx = EmptyContext(_tempDir);
        var findings = await Validator.DetectAsync(ctx, default);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_CodeMatchesCatalog_ProducesNoFindings()
    {
        // Catalog says 'foo' is emitted by feature-a; code shows eventBus.emit('foo') in feature-a/*.ts
        File.WriteAllText(Path.Combine(_tempDir, "Architecture.md"), """
            # Architecture

            ## Event Catalog

            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            | foo | feature-a | feature-b |
            """);

        var featureADir = Directory.CreateDirectory(Path.Combine(_tempDir, "feature-a"));
        File.WriteAllText(Path.Combine(featureADir.FullName, "service.ts"),
            "eventBus.emit('foo');");

        var featureBDir = Directory.CreateDirectory(Path.Combine(_tempDir, "feature-b"));
        File.WriteAllText(Path.Combine(featureBDir.FullName, "handler.ts"),
            "eventBus.on('foo', handler);");

        var ctx = EmptyContext(_tempDir);
        var findings = await Validator.DetectAsync(ctx, default);

        // All events accounted for — no findings expected
        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_CodeEmitsUncatalogedEvent_ProducesCriticalFinding()
    {
        // No Event Catalog entry for 'foo', but code emits it → Critical finding
        File.WriteAllText(Path.Combine(_tempDir, "Architecture.md"), """
            # Architecture

            ## Event Catalog

            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            | known-event | service-a | service-b |
            """);

        File.WriteAllText(Path.Combine(_tempDir, "service.ts"),
            "eventBus.emit('foo');");

        var ctx = EmptyContext(_tempDir);
        var findings = await Validator.DetectAsync(ctx, default);

        var undeclared = findings.Where(f => f.DedupKey.Contains("undeclared-emit")).ToList();
        Assert.Single(undeclared);
        Assert.Equal("foo", undeclared[0].Pattern);
        Assert.Equal(0.85, undeclared[0].Confidence);
    }

    [Fact]
    public async Task DetectAsync_CatalogDeclaredSubscriberNoEmitterInCode_ProducesImportantFinding()
    {
        // Catalog declares 'bar' has subscriber, but no emit('bar') found in code
        File.WriteAllText(Path.Combine(_tempDir, "Architecture.md"), """
            # Architecture

            ## Event Catalog

            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            | bar | service-a | service-b |
            """);

        // Only subscriber, no emitter in code
        File.WriteAllText(Path.Combine(_tempDir, "handler.ts"),
            "eventBus.on('bar', handleBar);");

        var ctx = EmptyContext(_tempDir);
        var findings = await Validator.DetectAsync(ctx, default);

        var subNoEmit = findings.Where(f => f.DedupKey.Contains("subscriber-no-emitter")).ToList();
        Assert.Single(subNoEmit);
        Assert.Equal("bar", subNoEmit[0].Pattern);
        Assert.Equal(0.65, subNoEmit[0].Confidence);
    }

    [Fact]
    public async Task DetectAsync_ArchContractAnnotationPrecedesCatalog_ProducesWarning()
    {
        // ARCH-CONTRACT annotation declares an event, but no catalog row exists
        File.WriteAllText(Path.Combine(_tempDir, "Architecture.md"), """
            # Architecture

            ## Event Catalog

            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            """);

        // Annotation only — no catalog row for 'annotated-event'
        File.WriteAllText(Path.Combine(_tempDir, "service.ts"), """
            // ARCH-CONTRACT: emits=annotated-event
            function triggerEvent() { }
            """);

        var ctx = EmptyContext(_tempDir);
        var findings = await Validator.DetectAsync(ctx, default);

        var annotationFindings = findings.Where(f => f.DedupKey.Contains("undeclared-annotation-emit")).ToList();
        Assert.Single(annotationFindings);
        Assert.Equal(0.50, annotationFindings[0].Confidence);
    }

    [Fact]
    public async Task DetectAsync_NonExistentWorkspace_ProducesNoFindings()
    {
        var ctx = EmptyContext(Path.Combine(_tempDir, "does-not-exist"));
        var findings = await Validator.DetectAsync(ctx, default);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_CatalogEmitterButNoSubscriberInCode_ProducesWarning()
    {
        // Catalog has emitter declared for 'baz' but no subscribe call in code
        File.WriteAllText(Path.Combine(_tempDir, "Architecture.md"), """
            # Architecture

            ## Event Catalog

            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            | baz | emitter-service | consumer-service |
            """);

        // Only emitter in code, no subscriber
        File.WriteAllText(Path.Combine(_tempDir, "emitter.ts"),
            "eventBus.emit('baz');");

        var ctx = EmptyContext(_tempDir);
        var findings = await Validator.DetectAsync(ctx, default);

        var emitterNoSub = findings.Where(f => f.DedupKey.Contains("emitter-no-subscriber")).ToList();
        Assert.Single(emitterNoSub);
        Assert.Equal("baz", emitterNoSub[0].Pattern);
        Assert.Equal(0.45, emitterNoSub[0].Confidence);
    }

    [Fact]
    public async Task DetectAsync_CSharpPublishPattern_Detected()
    {
        // C# eventBus.Publish("event-name") should be matched
        File.WriteAllText(Path.Combine(_tempDir, "Architecture.md"), """
            ## Event Catalog
            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            """);

        File.WriteAllText(Path.Combine(_tempDir, "EventPublisher.cs"),
            """_eventBus.Publish("cs-event");""");

        var ctx = EmptyContext(_tempDir);
        var findings = await Validator.DetectAsync(ctx, default);

        var undeclared = findings.Where(f => f.Pattern == "cs-event").ToList();
        Assert.Single(undeclared);
    }

    [Fact]
    public async Task DetectAsync_ArchitectureMdUnderAgentDocs_IsFound()
    {
        // Architecture.md located at AgentDocs/42/Architecture.md
        var docsDir = Directory.CreateDirectory(Path.Combine(_tempDir, "AgentDocs", "42"));
        File.WriteAllText(Path.Combine(docsDir.FullName, "Architecture.md"), """
            ## Event Catalog
            | Event | Emitters | Subscribers |
            |-------|----------|-------------|
            | nested-event | svc-a | svc-b |
            """);

        File.WriteAllText(Path.Combine(_tempDir, "app.ts"),
            "eventBus.emit('undeclared-nested');");

        var ctx = EmptyContext(_tempDir);
        var findings = await Validator.DetectAsync(ctx, default);

        // Catalog was loaded (nested-event cataloged) but undeclared-nested is not
        Assert.Contains(findings, f => f.Pattern == "undeclared-nested");
    }
}
