using Microsoft.Extensions.Logging.Abstractions;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Tests.Scenarios;

/// <summary>
/// Tests for <see cref="ProductionReadinessChecker"/>.
/// Uses a temp directory per test so checks are fully isolated and deterministic.
/// </summary>
public sealed class ProductionReadinessCheckerTests : IDisposable
{
    private readonly string _root;
    private readonly ProductionReadinessChecker _checker;

    public ProductionReadinessCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vdt-prc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _checker = new ProductionReadinessChecker(NullLogger<ProductionReadinessChecker>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static ChecklistItem GetItem(ProductionReadinessReport report, string id) =>
        report.Items.Single(i => i.Id == id);

    // ── Test 1: All skip when no workspace ────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NoWorkspace_AllNonDbChecksSkip()
    {
        // Workspace path that does not exist.
        var missing = Path.Combine(_root, "nonexistent");

        var report = await _checker.CheckAsync(missing, scenarios: null);

        Assert.Equal(15, report.Items.Count);
        // Every item that requires the workspace should be Skip (not Fail).
        // Item 14 (flow-findings) also skips because no stateStore was provided.
        Assert.All(report.Items, item =>
            Assert.True(item.Status == CheckStatus.Skip,
                $"Expected Skip for '{item.Id}' but got {item.Status}: {item.Reason}"));
        Assert.True(report.AllPassed, "AllPassed should be true when every item is Skip");
    }

    // ── Test 2: All-pass workspace ────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_WellFormedWorkspace_AllPassOrSkip()
    {
        // Build success sentinel.
        WriteFile(".build-result.json", """{"success": true}""");

        // Playwright smoke spec.
        WriteFile("e2e/playwright-smoke.spec.ts", """
            import { test, expect } from '@playwright/test';
            test('scenario smoke', async ({ page }) => {
                await page.goto('/');
                await expect(page).toHaveTitle(/App/);
            });
            """);

        // A simple source file — no stubs, no debug leaks, no secrets.
        WriteFile("src/app.ts", """
            export function greet(name: string): string {
                return `Hello, ${name}!`;
            }
            """);

        // A test file.
        WriteFile("src/app.test.ts", """
            import { greet } from './app';
            test('greet', () => expect(greet('World')).toBe('Hello, World!'));
            """);

        // Config file without TODO/FIXME.
        WriteFile("config/settings.json", """{"port": 3000, "env": "production"}""");

        var report = await _checker.CheckAsync(_root, scenarios: null);

        Assert.Equal(15, report.Items.Count);
        Assert.Equal(0, report.FailCount);
        Assert.True(report.AllPassed,
            string.Join(", ", report.Items.Where(i => i.Status == CheckStatus.Fail).Select(i => $"{i.Id}: {i.Reason}")));
    }

    // ── Test 3: Cat-A stub → Fail ─────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_StubCommentInSource_NoCatAStubsFails()
    {
        WriteFile("src/pathfinding.ts", """
            export function findPath(start: Node, end: Node): Node[] {
                // TODO: stub — to be completed when EventBus exists
                return [];
            }
            """);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "no-cat-a-stubs");
        Assert.Equal(CheckStatus.Fail, item.Status);
        Assert.Contains("Cat-A", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_StubOkAnnotation_ExemptsFromCatAFail()
    {
        WriteFile("src/pathfinding.ts", """
            // STUB_OK: intentional placeholder during init phase — software-engineer-1 2026-01-01
            export function findPath(start: any, end: any): any[] {
                // TODO: to be wired when EventBus exists
                return [];
            }
            """);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "no-cat-a-stubs");
        // Should Pass (no unexempted Cat-A stubs) or Skip.
        Assert.True(item.Status == CheckStatus.Pass || item.Status == CheckStatus.Skip,
            $"Expected Pass/Skip, got {item.Status}: {item.Reason}");
    }

    // ── Test 4: Missing PMSpec asset → Fail ───────────────────────────────────

    [Fact]
    public async Task CheckAsync_MissingDeclaredAsset_NoMissingAssetsFails()
    {
        WriteFile("PMSpec.md", """
            # PM Specification

            # image-deliverables
            - assets/hero.png
            - assets/icon.svg

            ## Notes
            """);
        // hero.png exists; icon.svg does NOT.
        WriteFile("assets/hero.png", new string('X', 100));
        // icon.svg intentionally absent.

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "no-missing-assets");
        Assert.Equal(CheckStatus.Fail, item.Status);
        Assert.Contains("icon.svg", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_AllAssetsPresent_NoMissingAssetsPasses()
    {
        WriteFile("PMSpec.md", """
            # PM Specification

            # image-deliverables
            - assets/hero.png

            ## Notes
            """);
        WriteFile("assets/hero.png", new string('X', 100));

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "no-missing-assets");
        Assert.Equal(CheckStatus.Pass, item.Status);
    }

    // ── Test 5: Merge-conflict markers → Fail ─────────────────────────────────

    [Fact]
    public async Task CheckAsync_MergeConflictMarkers_IntegrationPrCleanFails()
    {
        WriteFile("src/component.ts", """
            export function render() {
            <<<<<<< HEAD
                return '<div>new</div>';
            =======
                return '<div>old</div>';
            >>>>>>> feature/branch
            }
            """);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "integration-pr-clean");
        Assert.Equal(CheckStatus.Fail, item.Status);
        Assert.Contains("component.ts", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_NoMergeConflicts_IntegrationPrCleanPasses()
    {
        WriteFile("src/clean.ts", "export const x = 1;");

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "integration-pr-clean");
        Assert.Equal(CheckStatus.Pass, item.Status);
    }

    // ── Test 6: Debug leaks → Fail ────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_ConsoleLogInSource_NoDebugLeaksFails()
    {
        WriteFile("src/feature.ts", """
            export function doWork() {
                console.log('debug: starting work');
                return 42;
            }
            """);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "no-debug-leaks");
        Assert.Equal(CheckStatus.Fail, item.Status);
    }

    [Fact]
    public async Task CheckAsync_ConsoleLogOnlyInTestFile_NoDebugLeaksPasses()
    {
        // console.log in test file should be exempt.
        WriteFile("src/feature.test.ts", """
            test('logs', () => {
                console.log('test output');
                expect(true).toBe(true);
            });
            """);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "no-debug-leaks");
        // Should Pass (test files are exempt) or Skip (no source files).
        Assert.True(item.Status == CheckStatus.Pass || item.Status == CheckStatus.Skip,
            $"Expected Pass/Skip, got {item.Status}: {item.Reason}");
    }

    // ── Test 7: No test files → Fail ──────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NoTestFiles_TestCoverageExistsFails()
    {
        WriteFile("src/app.ts", "export const x = 1;");

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "test-coverage-exists");
        Assert.Equal(CheckStatus.Fail, item.Status);
    }

    [Fact]
    public async Task CheckAsync_TestFilePresent_TestCoverageExistsPasses()
    {
        WriteFile("src/app.test.ts", "test('x', () => {});");

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "test-coverage-exists");
        Assert.Equal(CheckStatus.Pass, item.Status);
    }

    // ── Test 8: Config TODO/FIXME → Fail ─────────────────────────────────────

    [Fact]
    public async Task CheckAsync_TodoInConfigFile_ConfigCompletenessFails()
    {
        WriteFile("config/app.json", """
            {
              "apiUrl": "TODO: set production URL",
              "timeout": 5000
            }
            """);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "config-completeness");
        Assert.Equal(CheckStatus.Fail, item.Status);
    }

    // ── Test 9: Hardcoded secret → Fail ──────────────────────────────────────

    [Fact]
    public async Task CheckAsync_HardcodedPassword_NoSecurityShipBlockersFails()
    {
        WriteFile("src/db.ts", """
            const connection = createConnection({
                host: 'localhost',
                password: 'super_secret_password_123',
            });
            """);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "no-security-ship-blockers");
        Assert.Equal(CheckStatus.Fail, item.Status);
    }

    [Fact]
    public async Task CheckAsync_PasswordEnvVar_NoSecurityShipBlockersPasses()
    {
        // Using environment variable reference — should not flag.
        WriteFile("src/db.ts", """
            const connection = createConnection({
                host: 'localhost',
                password: process.env.DB_PASSWORD,
            });
            """);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "no-security-ship-blockers");
        Assert.True(item.Status == CheckStatus.Pass || item.Status == CheckStatus.Skip,
            $"Expected Pass/Skip for env-var reference, got {item.Status}: {item.Reason}");
    }

    // ── Test 10: Build result markers ─────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_BuildSuccessJson_BuildSuccessPasses()
    {
        WriteFile(".build-result.json", """{"success": true, "warnings": 0}""");

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "build-success");
        Assert.Equal(CheckStatus.Pass, item.Status);
    }

    [Fact]
    public async Task CheckAsync_BuildFailedJson_BuildSuccessFails()
    {
        WriteFile(".build-result.json", """{"success": false, "errors": ["Type error on line 42"]}""");

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "build-success");
        Assert.Equal(CheckStatus.Fail, item.Status);
    }

    // ── Test 11: Screenshots non-empty ────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_LargeScreenshot_ScreenshotsNonEmptyPasses()
    {
        var screenshotDir = Path.Combine(_root, ".screenshots");
        Directory.CreateDirectory(screenshotDir);
        // Write a >5KB PNG placeholder.
        File.WriteAllBytes(Path.Combine(screenshotDir, "home.png"), new byte[6 * 1024]);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "screenshots-non-empty");
        Assert.Equal(CheckStatus.Pass, item.Status);
    }

    [Fact]
    public async Task CheckAsync_TinyScreenshot_ScreenshotsNonEmptyFails()
    {
        var screenshotDir = Path.Combine(_root, ".screenshots");
        Directory.CreateDirectory(screenshotDir);
        File.WriteAllBytes(Path.Combine(screenshotDir, "blank.png"), new byte[100]);

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "screenshots-non-empty");
        Assert.Equal(CheckStatus.Fail, item.Status);
    }

    // ── Test 12: AllPassed / FailCount / PassCount properties ─────────────────

    [Fact]
    public async Task Report_FailCount_ReflectsNumberOfFailedItems()
    {
        // Create two conditions that will Fail: debug leak + no test files.
        WriteFile("src/leaky.ts", "console.log('debug');");
        // No test files.

        var report = await _checker.CheckAsync(_root, scenarios: null);

        Assert.True(report.FailCount >= 2,
            $"Expected at least 2 failures; FailCount={report.FailCount}. " +
            string.Join(", ", report.Items.Where(i => i.Status == CheckStatus.Fail).Select(i => i.Id)));
        Assert.False(report.AllPassed);
    }

    // ── Test 13: Smoke spec present ───────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_SmokeSpecPresent_Passes()
    {
        WriteFile("e2e/playwright-smoke.spec.ts", "test('smoke', () => {});");

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "smoke-spec-present");
        Assert.Equal(CheckStatus.Pass, item.Status);
    }

    [Fact]
    public async Task CheckAsync_NoSmokeSpec_SmokeSpecPresentFails()
    {
        // No spec files at all.
        WriteFile("src/app.ts", "export const x = 1;");

        var report = await _checker.CheckAsync(_root, scenarios: null);

        var item = GetItem(report, "smoke-spec-present");
        Assert.Equal(CheckStatus.Fail, item.Status);
    }

    // ── Test 14: Null workspace → all skip ────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NullWorkspace_AllSkip()
    {
        var report = await _checker.CheckAsync(workspaceRoot: null, scenarios: null);

        Assert.Equal(15, report.Items.Count);
        Assert.All(report.Items, item =>
            Assert.True(item.Status == CheckStatus.Skip,
                $"'{item.Id}' should Skip with null workspace but got {item.Status}: {item.Reason}"));
    }
}
