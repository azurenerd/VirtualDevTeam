using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.MissingWork;
using VirtualDevTeam.Orchestrator;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Behavioral tests for <see cref="StubFunctionBodyDetector"/>.
/// Each test exercises the internal static helpers (FindTsJsStubs / FindCSharpStubs /
/// FindPythonStubs / FindGoStubs / ClassifyBraceBody) directly rather than writing
/// real files, keeping tests fast and deterministic.
///
/// The canonical positive case throughout is the GridGuardians PR #1518 pathfinding stub:
/// <code>
/// export function register(_scene: any): void {
///   /* to be completed when EventBus exists */
/// }
/// </code>
/// </summary>
public sealed class StubFunctionBodyDetectorTests
{
    private static readonly StubFunctionBodyDetector Detector =
        new(NullLogger<StubFunctionBodyDetector>.Instance);

    private static MissingWorkContext EmptyContext(string root) => new()
    {
        WorkspaceRoot = root,
        OpenIssues = Array.Empty<IssueRef>(),
        RecentlyClosedIssues = Array.Empty<IssueRef>(),
    };

    // =========================================================================
    // TypeScript / JavaScript
    // =========================================================================

    [Fact]
    public void TsJs_CatA_StubCommentBody_IsDetected()
    {
        // Cat-A: body has only a stub-pattern comment, no executable code
        var lines = new[]
        {
            "export function processPayment(amount: number): void {",
            "  /* TODO: implement payment processing */",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindTsJsStubs(lines).ToList();

        Assert.Single(stubs);
        Assert.Equal("processPayment", stubs[0].FunctionName);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatA, stubs[0].Category);
        Assert.False(stubs[0].HasStubOk);
    }

    [Fact]
    public void TsJs_CatD_EmptyBody_IsDetected()
    {
        // Cat-D: completely empty braces
        var lines = new[]
        {
            "export function initialize(): void { }",
        };

        var stubs = StubFunctionBodyDetector.FindTsJsStubs(lines).ToList();

        var match = stubs.FirstOrDefault(s => s.FunctionName == "initialize");
        Assert.NotNull(match);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatD, match.Category);
    }

    [Fact]
    public void TsJs_CatE_UnderscoreParam_PlusStubBody_IsDetected()
    {
        // Cat-E (canonical GridGuardians PR #1518 pathfinding stub):
        // underscore-prefixed param + stub comment body
        var lines = new[]
        {
            "export function register(_scene: any): void {",
            "  /* to be completed when EventBus exists */",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindTsJsStubs(lines).ToList();

        Assert.Single(stubs);
        Assert.Equal("register", stubs[0].FunctionName);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatE, stubs[0].Category);
        Assert.False(stubs[0].HasStubOk);
    }

    [Fact]
    public void TsJs_CatE_MultilineSignature_UnderscoreParam_EmptyBody_IsDetected()
    {
        // Cat-E: underscore param + empty body across multiple lines
        var lines = new[]
        {
            "function handleEvent(_event: MouseEvent): void {",
            "",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindTsJsStubs(lines).ToList();

        var match = stubs.FirstOrDefault(s => s.FunctionName == "handleEvent");
        Assert.NotNull(match);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatE, match.Category);
    }

    [Fact]
    public void TsJs_Negative_RealImplementation_NotDetected()
    {
        // Negative: function with actual executable code
        var lines = new[]
        {
            "export function add(a: number, b: number): number {",
            "  return a + b;",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindTsJsStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    [Fact]
    public void TsJs_Negative_StubOkAnnotation_Suppresses()
    {
        // Negative: STUB_OK annotation in preceding line suppresses detection
        var lines = new[]
        {
            "// STUB_OK: pathfinding placeholder — software-engineer-1 2026-05-13",
            "export function register(_scene: any): void {",
            "  /* to be completed when EventBus exists */",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindTsJsStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    [Fact]
    public void TsJs_Negative_StubOkInBody_Suppresses()
    {
        // Negative: STUB_OK annotation inside the function body suppresses detection
        var lines = new[]
        {
            "export function noop(_data: any): void {",
            "  // STUB_OK: intentional no-op during init — se-2 2026-05-14",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindTsJsStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    [Fact]
    public void TsJs_PlaceholderKeyword_IsRecognizedAsStub()
    {
        var lines = new[]
        {
            "function save(data: any): Promise<void> {",
            "  // placeholder — will be implemented once DB schema is ready",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindTsJsStubs(lines).ToList();

        Assert.Single(stubs);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatA, stubs[0].Category);
    }

    [Fact]
    public void TsJs_WipKeyword_IsRecognizedAsStub()
    {
        var lines = new[]
        {
            "export async function fetchOrders(): Promise<Order[]> {",
            "  /* WIP */",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindTsJsStubs(lines).ToList();

        Assert.Single(stubs);
    }

    // =========================================================================
    // C#
    // =========================================================================

    [Fact]
    public void CSharp_CatD_EmptyBody_IsDetected()
    {
        var lines = new[]
        {
            "public class Foo {",
            "    public void Initialize() { }",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindCSharpStubs(lines).ToList();

        var match = stubs.FirstOrDefault(s => s.FunctionName == "Initialize");
        Assert.NotNull(match);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatD, match.Category);
    }

    [Fact]
    public void CSharp_CatA_StubComment_IsDetected()
    {
        var lines = new[]
        {
            "    public void Register(Scene scene)",
            "    {",
            "        // TODO: wire to event bus",
            "    }",
        };

        var stubs = StubFunctionBodyDetector.FindCSharpStubs(lines).ToList();

        Assert.Single(stubs);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatA, stubs[0].Category);
    }

    [Fact]
    public void CSharp_Negative_AbstractMethodDeclaration_NotDetected()
    {
        // Negative: abstract method has no body — must not be flagged
        var lines = new[]
        {
            "    public abstract void Register(Scene scene);",
        };

        var stubs = StubFunctionBodyDetector.FindCSharpStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    [Fact]
    public void CSharp_Negative_RealImplementation_NotDetected()
    {
        var lines = new[]
        {
            "    public int Add(int a, int b)",
            "    {",
            "        return a + b;",
            "    }",
        };

        var stubs = StubFunctionBodyDetector.FindCSharpStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    [Fact]
    public void CSharp_Negative_AbstractKeywordInLine_NotDetected()
    {
        var lines = new[]
        {
            "    public abstract Task<int> ComputeAsync(CancellationToken ct = default);",
        };

        var stubs = StubFunctionBodyDetector.FindCSharpStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    [Fact]
    public void CSharp_Negative_StubOkAnnotation_Suppresses()
    {
        var lines = new[]
        {
            "    // STUB_OK: empty Dispose is intentional — se-1 2026-05-13",
            "    public void Dispose() { }",
        };

        var stubs = StubFunctionBodyDetector.FindCSharpStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    // =========================================================================
    // Python
    // =========================================================================

    [Fact]
    public void Python_CatD_PassBody_IsDetected()
    {
        // Cat-D: def f(): pass
        var lines = new[]
        {
            "def register(scene):",
            "    pass",
        };

        var stubs = StubFunctionBodyDetector.FindPythonStubs(lines).ToList();

        Assert.Single(stubs);
        Assert.Equal("register", stubs[0].FunctionName);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatD, stubs[0].Category);
    }

    [Fact]
    public void Python_CatD_InlinePass_IsDetected()
    {
        // Cat-D: inline `def f(): pass`
        var lines = new[]
        {
            "def noop(_x): pass",
        };

        var stubs = StubFunctionBodyDetector.FindPythonStubs(lines).ToList();

        Assert.Single(stubs);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatE, stubs[0].Category); // underscore param
    }

    [Fact]
    public void Python_CatA_StubComment_IsDetected()
    {
        var lines = new[]
        {
            "def connect(self, host: str) -> None:",
            "    # placeholder — will use socket once network layer is ready",
            "    pass",
        };

        var stubs = StubFunctionBodyDetector.FindPythonStubs(lines).ToList();

        Assert.Single(stubs);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatA, stubs[0].Category);
    }

    [Fact]
    public void Python_Negative_RealImplementation_NotDetected()
    {
        var lines = new[]
        {
            "def add(a: int, b: int) -> int:",
            "    return a + b",
        };

        var stubs = StubFunctionBodyDetector.FindPythonStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    [Fact]
    public void Python_Negative_StubOkAnnotation_Suppresses()
    {
        var lines = new[]
        {
            "# STUB_OK: intentional no-op during init phase — se-2 2026-05-14",
            "def register(scene): pass",
        };

        var stubs = StubFunctionBodyDetector.FindPythonStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    // =========================================================================
    // Go
    // =========================================================================

    [Fact]
    public void Go_CatD_EmptyBody_IsDetected()
    {
        var lines = new[]
        {
            "func Register(scene Scene) {",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindGoStubs(lines).ToList();

        Assert.Single(stubs);
        Assert.Equal("Register", stubs[0].FunctionName);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatD, stubs[0].Category);
    }

    [Fact]
    public void Go_CatA_StubComment_IsDetected()
    {
        var lines = new[]
        {
            "func (r *Router) Register(scene any) {",
            "    // stub — integration point for EventBus",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindGoStubs(lines).ToList();

        Assert.Single(stubs);
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatA, stubs[0].Category);
    }

    [Fact]
    public void Go_Negative_RealImplementation_NotDetected()
    {
        var lines = new[]
        {
            "func Add(a, b int) int {",
            "    return a + b",
            "}",
        };

        var stubs = StubFunctionBodyDetector.FindGoStubs(lines).ToList();

        Assert.Empty(stubs);
    }

    // =========================================================================
    // ClassifyBraceBody helper unit tests
    // =========================================================================

    [Fact]
    public void ClassifyBraceBody_EmptyLines_ReturnsCatD()
    {
        var body = new List<string> { "  ", "\t" };
        var result = StubFunctionBodyDetector.ClassifyBraceBody(body, paramsStr: "");
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatD, result);
    }

    [Fact]
    public void ClassifyBraceBody_EmptyLines_WithUnderscoreParam_ReturnsCatE()
    {
        var body = new List<string>();
        var result = StubFunctionBodyDetector.ClassifyBraceBody(body, paramsStr: "_scene: any");
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatE, result);
    }

    [Fact]
    public void ClassifyBraceBody_StubCommentOnly_ReturnsCatA()
    {
        var body = new List<string>
        {
            "  /* to be wired when EventBus exists */",
        };
        var result = StubFunctionBodyDetector.ClassifyBraceBody(body, paramsStr: "count: number");
        Assert.Equal(StubFunctionBodyDetector.StubCategory.CatA, result);
    }

    [Fact]
    public void ClassifyBraceBody_ExecutableStatement_ReturnsNone()
    {
        var body = new List<string>
        {
            "  return 42;",
        };
        var result = StubFunctionBodyDetector.ClassifyBraceBody(body, paramsStr: "");
        Assert.Equal(StubFunctionBodyDetector.StubCategory.None, result);
    }

    [Fact]
    public void ClassifyBraceBody_NonStubComment_ReturnsNone()
    {
        // A comment-only body that does NOT match stub keywords is NOT a stub.
        var body = new List<string>
        {
            "  // required by IDisposable interface",
        };
        var result = StubFunctionBodyDetector.ClassifyBraceBody(body, paramsStr: "");
        Assert.Equal(StubFunctionBodyDetector.StubCategory.None, result);
    }

    // =========================================================================
    // ExtractBraceBody helper unit tests
    // =========================================================================

    [Fact]
    public void ExtractBraceBody_SingleLine_EmptyBraces()
    {
        var lines = new[] { "function foo() { }" };
        var (body, end) = StubFunctionBodyDetector.ExtractBraceBody(lines, 0);

        Assert.Equal(0, end);
        Assert.Empty(body);
    }

    [Fact]
    public void ExtractBraceBody_MultiLine_ExtractsInnerLines()
    {
        var lines = new[]
        {
            "function foo() {",
            "  /* stub */",
            "}",
        };
        var (body, end) = StubFunctionBodyDetector.ExtractBraceBody(lines, 0);

        Assert.Equal(2, end);
        Assert.Single(body);
        Assert.Contains("stub", body[0]);
    }

    [Fact]
    public void ExtractBraceBody_ReturnsMinusOne_WhenNoClosingBrace()
    {
        var lines = new[]
        {
            "function incomplete() {",
            "  /* no closing brace in next 120 lines */",
        };
        var (_, end) = StubFunctionBodyDetector.ExtractBraceBody(lines, 0);

        Assert.Equal(-1, end);
    }

    // =========================================================================
    // DetectAsync integration smoke test (uses real temp files)
    // =========================================================================

    [Fact]
    public async Task DetectAsync_CanonicalGridGuardians_RegisterStub_IsFound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vdt-stub-detect-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var tsFile = Path.Combine(tempDir, "pathfinding.ts");

            await File.WriteAllTextAsync(tsFile,
                """
                export function register(_scene: any): void {
                  /* to be completed when EventBus exists */
                }
                """);

            var ctx = EmptyContext(tempDir);
            var findings = await Detector.DetectAsync(ctx, default);

            Assert.NotEmpty(findings);
            var f = findings.First(x => x.DetectorId == "stub-function-body");
            Assert.Equal("register", f.Pattern);
            Assert.True(f.Confidence >= 0.80);
            Assert.Contains("Cat-E", f.Summary);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_StubOkAnnotated_RegisterStub_IsNotFound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vdt-stub-detect-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var tsFile = Path.Combine(tempDir, "pathfinding-ok.ts");

            await File.WriteAllTextAsync(tsFile,
                """
                // STUB_OK: pathfinding placeholder — software-engineer-1 2026-05-13
                export function register(_scene: any): void {
                  /* to be completed when EventBus exists */
                }
                """);

            var ctx = EmptyContext(tempDir);
            var findings = await Detector.DetectAsync(ctx, default);

            Assert.Empty(findings.Where(f => f.Pattern == "register"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_NonexistentRoot_ReturnsEmptyFindings()
    {
        var ctx = EmptyContext(@"C:\nonexistent-path-12345");
        var findings = await Detector.DetectAsync(ctx, default);
        Assert.Empty(findings);
    }
}
