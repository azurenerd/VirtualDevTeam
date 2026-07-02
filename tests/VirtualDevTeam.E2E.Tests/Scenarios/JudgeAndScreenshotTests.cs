using VirtualDevTeam.Agents.AI;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Workspace;
using VirtualDevTeam.E2E.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.E2E.Tests.Scenarios;

/// <summary>
/// Scenario 3: Judge evaluation with real LLM and Playwright screenshots.
///
/// Tests the evaluation pipeline using pre-built HelloWorldApp content:
/// - Real LLM calls for the judge scoring (via Copilot CLI or configured provider)
/// - Real Playwright screenshot capture of the HelloWorldApp
/// - Verifies judge returns meaningful scores and feedback
/// - Verifies screenshot bytes are captured
///
/// These tests require:
/// - A working LLM provider (Copilot CLI or API key)
/// - Playwright Chromium browser (auto-installed if missing)
/// - .NET SDK for building/running HelloWorldApp
///
/// Tests are skipped automatically when prerequisites are not available.
/// </summary>
public class JudgeAndScreenshotTests : IDisposable
{
    private readonly E2ETestHarness _harness;
    private readonly string _helloWorldAppPath;

    public JudgeAndScreenshotTests()
    {
        _harness = E2ETestHarness.Create(config =>
        {
            config.Limits.SinglePRMode = true;
            config.Limits.SingleIssueMode = true;
            config.Limits.MaxAdditionalEngineers = 0;
            config.Workspace.CaptureScreenshots = true;
            config.Workspace.ScreenshotRenderDelaySeconds = 2;
        });

        // Locate pre-built HelloWorldApp content
        _helloWorldAppPath = FindHelloWorldAppPath();
    }

    /// <summary>
    /// Validate that the LlmJudge returns meaningful scores and feedback
    /// when given a real code patch (the HelloWorldApp content).
    /// Uses real LLM calls — skipped when no provider is available.
    /// </summary>
    [SkippableFact]
    public async Task LlmJudge_ScoresHelloWorldApp_ReturnsMeaningfulFeedback()
    {
        // Arrange: build a real LlmJudge with real LLM
        var modelRegistry = _harness.Services.GetRequiredService<ModelRegistry>();
        var stratCfg = new OptionsWrapper<StrategyFrameworkConfig>(new StrategyFrameworkConfig());
        var logger = _harness.Services.GetRequiredService<ILogger<LlmJudge>>();

        // Check if real LLM is available by attempting a trivial call
        Skip.IfNot(await IsRealLlmAvailableAsync(modelRegistry),
            "No real LLM provider available (Copilot CLI or API key required)");

        var judge = new LlmJudge(modelRegistry, new OptionsMonitorWrapper<StrategyFrameworkConfig>(stratCfg.Value), logger);

        // Build a code patch from the HelloWorldApp files
        var patch = BuildHelloWorldPatch();
        Assert.False(string.IsNullOrWhiteSpace(patch), "HelloWorldApp patch should not be empty");

        var input = new JudgeInput
        {
            TaskId = "e2e-judge-test",
            TaskTitle = "Create Hello World ASP.NET Core website",
            TaskDescription = "Build a basic Hello World ASP.NET Core Razor Pages website with an Index page and Privacy page. " +
                              "The site should use the standard template layout with navigation, styling, and proper routing.",
            CandidatePatches = new Dictionary<string, string>
            {
                ["copilot-cli"] = patch
            }
        };

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await judge.ScoreAsync(input, cts.Token);

        // Assert: judge should return valid scores
        Assert.NotNull(result);
        Assert.Null(result.Error);
        Assert.False(result.IsFallback, "Judge should return real scores, not fallback");
        Assert.True(result.Scores.ContainsKey("copilot-cli"), "Scores should include our candidate");

        var score = result.Scores["copilot-cli"];
        Assert.InRange(score.AcceptanceCriteriaScore, 1, 10);
        Assert.InRange(score.DesignScore, 1, 10);
        Assert.InRange(score.ReadabilityScore, 1, 10);
        Assert.False(string.IsNullOrWhiteSpace(score.Reasoning),
            "Judge should provide reasoning for scores");

        // A complete HelloWorld app should score reasonably well
        Assert.True(score.AcceptanceCriteriaScore >= 5,
            $"HelloWorldApp should score >= 5 on acceptance criteria, got {score.AcceptanceCriteriaScore}");

        Assert.True(result.TokensUsed > 0, "Judge should report tokens used");
    }

    /// <summary>
    /// Validate that Playwright can screenshot the HelloWorldApp.
    /// Uses real Playwright + Chromium — skipped when not available.
    /// </summary>
    [SkippableFact]
    public async Task PlaywrightRunner_CapturesScreenshot_ReturnsValidPng()
    {
        // Arrange
        var playwrightRunner = new PlaywrightRunner(
            _harness.Services.GetRequiredService<ILogger<PlaywrightRunner>>(),
            _harness.Services.GetRequiredService<AppLauncher>(),
            _harness.Services.GetRequiredService<MediaRecorder>(),
            _harness.Services.GetRequiredService<ApiSmokeRunner>());

        var workspaceConfig = new WorkspaceConfig
        {
            CaptureScreenshots = true,
            ScreenshotRenderDelaySeconds = 2,
        };

        // Validate Playwright is operational
        var isReady = await playwrightRunner.ValidateAsync(workspaceConfig, _helloWorldAppPath);
        Skip.IfNot(isReady, $"Playwright not ready: {playwrightRunner.NotReadyReason}");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var screenshotResult = await playwrightRunner.CaptureAppScreenshotAsync(
            _helloWorldAppPath, workspaceConfig, cts.Token);

        // Assert
        Assert.NotNull(screenshotResult);
        Assert.True(screenshotResult.Bytes.Length > 1000,
            $"Screenshot should be a substantial PNG (got {screenshotResult.Bytes.Length} bytes)");

        // Validate PNG header (magic bytes: 0x89 0x50 0x4E 0x47)
        Assert.Equal(0x89, screenshotResult.Bytes[0]);
        Assert.Equal((byte)'P', screenshotResult.Bytes[1]);
        Assert.Equal((byte)'N', screenshotResult.Bytes[2]);
        Assert.Equal((byte)'G', screenshotResult.Bytes[3]);
    }

    /// <summary>
    /// Full integration: Judge scores + Playwright screenshot for the same HelloWorldApp.
    /// Verifies the complete evaluation pipeline produces both scores and visual evidence.
    /// Skipped when either LLM or Playwright is unavailable.
    /// </summary>
    [SkippableFact]
    public async Task FullEvaluation_JudgeAndScreenshot_BothProduceResults()
    {
        // Check prerequisites
        var modelRegistry = _harness.Services.GetRequiredService<ModelRegistry>();
        Skip.IfNot(await IsRealLlmAvailableAsync(modelRegistry),
            "No real LLM provider available");

        var playwrightRunner = new PlaywrightRunner(
            _harness.Services.GetRequiredService<ILogger<PlaywrightRunner>>(),
            _harness.Services.GetRequiredService<AppLauncher>(),
            _harness.Services.GetRequiredService<MediaRecorder>(),
            _harness.Services.GetRequiredService<ApiSmokeRunner>());
        var workspaceConfig = new WorkspaceConfig
        {
            CaptureScreenshots = true,
            ScreenshotRenderDelaySeconds = 2,
        };
        var isPlaywrightReady = await playwrightRunner.ValidateAsync(workspaceConfig, _helloWorldAppPath);
        Skip.IfNot(isPlaywrightReady, $"Playwright not ready: {playwrightRunner.NotReadyReason}");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        // Step 1: Capture screenshot
        var screenshotResult = await playwrightRunner.CaptureAppScreenshotAsync(
            _helloWorldAppPath, workspaceConfig, cts.Token);
        Assert.NotNull(screenshotResult);
        Assert.True(screenshotResult.Bytes.Length > 1000, "Screenshot should be captured");

        // Step 2: Judge the code
        var stratCfg = new OptionsWrapper<StrategyFrameworkConfig>(new StrategyFrameworkConfig());
        var logger = _harness.Services.GetRequiredService<ILogger<LlmJudge>>();
        var judge = new LlmJudge(modelRegistry, new OptionsMonitorWrapper<StrategyFrameworkConfig>(stratCfg.Value), logger);

        var patch = BuildHelloWorldPatch();
        var input = new JudgeInput
        {
            TaskId = "e2e-full-eval",
            TaskTitle = "Create Hello World ASP.NET Core website",
            TaskDescription = "Build a basic Hello World ASP.NET Core Razor Pages website with Index and Privacy pages.",
            CandidatePatches = new Dictionary<string, string> { ["copilot-cli"] = patch }
        };

        var judgeResult = await judge.ScoreAsync(input, cts.Token);

        // Assert both components produced results
        Assert.NotNull(judgeResult);
        Assert.Null(judgeResult.Error);
        Assert.True(judgeResult.Scores.ContainsKey("copilot-cli"));
        Assert.True(judgeResult.Scores["copilot-cli"].AcceptanceCriteriaScore >= 1);
        Assert.False(string.IsNullOrWhiteSpace(judgeResult.Scores["copilot-cli"].Reasoning));

        // Both screenshot and judge scores exist — evaluation pipeline is complete
        Assert.True(screenshotResult.Bytes.Length > 0 && judgeResult.Scores.Count > 0,
            "Both screenshot capture and judge scoring should produce results");
    }

    // ── Helpers ──

    private static string FindHelloWorldAppPath()
    {
        // Walk up from test output directory to find Content/HelloWorldApp
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "Content", "HelloWorldApp");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir) ?? dir;
        }

        // Fallback: look relative to the working directory
        var cwd = Directory.GetCurrentDirectory();
        var fallback = Path.Combine(cwd, "Content", "HelloWorldApp");
        if (Directory.Exists(fallback))
            return fallback;

        throw new DirectoryNotFoundException(
            "Could not find Content/HelloWorldApp in test output or working directory");
    }

    private string BuildHelloWorldPatch()
    {
        var sb = new System.Text.StringBuilder();
        var appPath = _helloWorldAppPath;

        foreach (var file in Directory.GetFiles(appPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(appPath, file).Replace('\\', '/');

            // Skip bin/obj directories
            if (relativePath.Contains("bin/") || relativePath.Contains("obj/"))
                continue;

            try
            {
                var content = File.ReadAllText(file);
                sb.AppendLine($"--- a/{relativePath}");
                sb.AppendLine($"+++ b/{relativePath}");
                foreach (var line in content.Split('\n'))
                {
                    sb.AppendLine($"+{line.TrimEnd('\r')}");
                }
                sb.AppendLine();
            }
            catch
            {
                // Skip binary files
            }
        }

        return sb.ToString();
    }

    private static async Task<bool> IsRealLlmAvailableAsync(ModelRegistry modelRegistry)
    {
        // ScriptedModelRegistry is used in E2E tests — not a real LLM provider
        if (modelRegistry is ScriptedModelRegistry)
            return false;

        try
        {
            // Try to get a kernel — this will fail if no provider is configured
            var kernel = modelRegistry.GetKernel("standard");
            if (kernel is null) return false;

            // Quick test: ask a trivial question
            var chat = kernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
            var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
            history.AddUserMessage("Reply with exactly: OK");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: cts.Token);
            return response is { Count: > 0 };
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _harness.Dispose();
    }

    /// <summary>Wraps a static value as IOptionsMonitor for DI compatibility.</summary>
    private sealed class OptionsMonitorWrapper<T> : IOptionsMonitor<T>
    {
        public OptionsMonitorWrapper(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
