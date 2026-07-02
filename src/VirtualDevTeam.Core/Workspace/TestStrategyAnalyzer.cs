using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Analyzes PR changes and linked issue context to determine which test tiers
/// (unit, integration, UI) should be generated. Uses code-based heuristics
/// (file extensions, naming patterns, keyword detection) — no AI calls needed.
/// </summary>
public class TestStrategyAnalyzer
{
    private readonly ILogger<TestStrategyAnalyzer> _logger;

    /// <summary>File extensions that indicate UI/frontend code requiring Playwright tests.</summary>
    private static readonly HashSet<string> UIExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".razor", ".cshtml", ".tsx", ".jsx", ".vue", ".svelte", ".html"
    };

    /// <summary>File name patterns that indicate service/integration layer code.</summary>
    private static readonly string[] IntegrationPatterns =
    [
        "Controller", "Endpoint", "Handler", "Service", "Repository",
        "Client", "Gateway", "Middleware", "Hub", "Startup", "Program"
    ];

    /// <summary>Keywords in issue/PR body that suggest UI testing is needed.</summary>
    private static readonly string[] UIKeywords =
    [
        "ui", "page", "form", "button", "navigation", "display", "render",
        "modal", "dialog", "menu", "tab", "sidebar", "layout", "theme",
        "responsive", "click", "submit", "input", "dropdown", "checkbox",
        "user can see", "user clicks", "displayed", "visible", "shows",
        "screenshot", "browser", "frontend", "client-side"
    ];

    /// <summary>Keywords that suggest integration testing is needed.</summary>
    private static readonly string[] IntegrationKeywords =
    [
        "api", "endpoint", "database", "http", "rest", "graphql",
        "authentication", "authorization", "middleware", "pipeline",
        "dependency injection", "service bus", "queue", "cache",
        "external service", "third-party", "webhook"
    ];

    public TestStrategyAnalyzer(ILogger<TestStrategyAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze a PR's changed files and context to determine which test tiers are needed.
    /// </summary>
    /// <param name="changedFilePaths">File paths changed in the PR.</param>
    /// <param name="prBody">PR description text.</param>
    /// <param name="issueBody">Linked issue body (acceptance criteria, user story). Null if no linked issue.</param>
    /// <param name="techStack">Project tech stack (e.g., "C# .NET 8 with Blazor Server").</param>
    public TestStrategy Analyze(
        IReadOnlyList<string> changedFilePaths,
        string? prBody,
        string? issueBody,
        string techStack)
    {
        var rationale = new List<string>();
        var uiScenarios = new List<string>();
        bool needsUnit = false, needsIntegration = false, needsUI = false;

        // --- File extension analysis ---
        foreach (var filePath in changedFilePaths)
        {
            var ext = Path.GetExtension(filePath);
            var fileName = Path.GetFileNameWithoutExtension(filePath);

            // Any code file → unit tests
            if (IsCodeFile(ext))
                needsUnit = true;

            // UI extensions → UI tests
            if (UIExtensions.Contains(ext))
            {
                needsUI = true;
                rationale.Add($"UI file detected: {filePath}");
                uiScenarios.Add($"Verify {fileName} renders correctly and user interactions work");
            }

            // Integration patterns in file names → integration tests
            if (IntegrationPatterns.Any(p => fileName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                needsIntegration = true;
                rationale.Add($"Integration layer file: {filePath}");
            }
        }

        // --- PR body keyword analysis ---
        var combinedText = $"{prBody}\n{issueBody}".ToLowerInvariant();

        if (!needsUI && ContainsAnyKeyword(combinedText, UIKeywords))
        {
            needsUI = true;
            rationale.Add("UI-related keywords found in PR/issue description");
        }

        if (!needsIntegration && ContainsAnyKeyword(combinedText, IntegrationKeywords))
        {
            needsIntegration = true;
            rationale.Add("Integration-related keywords found in PR/issue description");
        }

        // --- Extract UI test scenarios from acceptance criteria ---
        if (needsUI && !string.IsNullOrWhiteSpace(issueBody))
        {
            var criteria = ExtractAcceptanceCriteria(issueBody);
            foreach (var criterion in criteria)
            {
                if (ContainsAnyKeyword(criterion.ToLowerInvariant(), UIKeywords))
                    uiScenarios.Add($"Verify: {criterion.Trim()}");
            }
        }

        // --- Guarantee: unit tests are ALWAYS generated for code changes ---
        if (changedFilePaths.Any(f => IsCodeFile(Path.GetExtension(f))))
        {
            needsUnit = true;
            if (rationale.Count == 0)
                rationale.Add("Code files changed — unit tests required");
        }

        var strategy = new TestStrategy
        {
            NeedsUnitTests = needsUnit,
            NeedsIntegrationTests = needsIntegration,
            NeedsUITests = needsUI,
            Rationale = string.Join("; ", rationale),
            UITestScenarios = uiScenarios
        };

        _logger.LogInformation(
            "Test strategy: Unit={Unit}, Integration={Integration}, UI={UI} — {Rationale}",
            strategy.NeedsUnitTests, strategy.NeedsIntegrationTests, strategy.NeedsUITests, strategy.Rationale);

        return strategy;
    }

    private static bool IsCodeFile(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".java" or ".go" or ".rs" => true,
            ".razor" or ".vue" or ".svelte" or ".rb" or ".php" or ".swift" or ".kt" => true,
            _ => false
        };
    }

    private static bool ContainsAnyKeyword(string text, string[] keywords)
    {
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extract acceptance criteria lines from an issue body.
    /// Looks for checklist items (- [ ] or * [ ]) and numbered items.
    /// </summary>
    internal static IReadOnlyList<string> ExtractAcceptanceCriteria(string issueBody)
    {
        var criteria = new List<string>();
        var lines = issueBody.Split('\n');

        bool inAcceptanceCriteria = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Detect acceptance criteria section headers
            if (trimmed.Contains("acceptance criteria", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("acceptance", StringComparison.OrdinalIgnoreCase) && trimmed.StartsWith('#'))
            {
                inAcceptanceCriteria = true;
                continue;
            }

            // Another heading ends the section
            if (inAcceptanceCriteria && trimmed.StartsWith('#'))
            {
                inAcceptanceCriteria = false;
                continue;
            }

            // Collect checklist items and numbered items
            if (trimmed.StartsWith("- [") || trimmed.StartsWith("* [") ||
                (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.'))
            {
                var cleaned = trimmed
                    .TrimStart('-', '*', ' ')
                    .Replace("[ ]", "").Replace("[x]", "").Replace("[X]", "")
                    .Trim();
                if (cleaned.Length > 0)
                    criteria.Add(cleaned);
            }
        }

        return criteria;
    }
}

/// <summary>
/// Result of test strategy analysis — indicates which test tiers should be generated.
/// </summary>
public record TestStrategy
{
    /// <summary>Whether unit tests should be generated. Almost always true for code changes.</summary>
    public required bool NeedsUnitTests { get; init; }

    /// <summary>Whether integration tests should be generated (service/API/data layer changes).</summary>
    public required bool NeedsIntegrationTests { get; init; }

    /// <summary>Whether UI/E2E tests with Playwright should be generated.</summary>
    public required bool NeedsUITests { get; init; }

    /// <summary>Human-readable explanation of why each tier was selected.</summary>
    public required string Rationale { get; init; }

    /// <summary>Specific UI test scenarios extracted from acceptance criteria.</summary>
    public IReadOnlyList<string> UITestScenarios { get; init; } = [];

    /// <summary>All tiers that should be tested.</summary>
    public IEnumerable<TestTier> RequiredTiers
    {
        get
        {
            if (NeedsUnitTests) yield return TestTier.Unit;
            if (NeedsIntegrationTests) yield return TestTier.Integration;
            if (NeedsUITests) yield return TestTier.UI;
        }
    }
}
