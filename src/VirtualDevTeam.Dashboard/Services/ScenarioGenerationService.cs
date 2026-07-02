using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Prompts;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Calls the Copilot CLI with the <c>prompts/wizard/scenario-generation.md</c> prompt
/// and returns a parsed list of <see cref="Scenario"/> objects ready for the wizard review step.
/// </summary>
public sealed class ScenarioGenerationService
{
    private readonly CopilotCliProcessManager? _cli;
    private readonly IPromptTemplateService? _promptTemplate;
    private readonly IOptions<VirtualDevTeamConfig> _config;
    private readonly ILogger<ScenarioGenerationService> _logger;
    private string _scratchDir = "";

    public ScenarioGenerationService(
        IOptions<VirtualDevTeamConfig> config,
        ILogger<ScenarioGenerationService> logger,
        CopilotCliProcessManager? cli = null,
        IPromptTemplateService? promptTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
        _cli = cli;
        _promptTemplate = promptTemplate;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the Copilot CLI is reachable and scenario generation
    /// can proceed.
    /// </summary>
    public bool IsAvailable => _cli?.IsAvailable == true;

    /// <summary>Last error message from generation (null if last call succeeded).</summary>
    public string? LastError { get; private set; }

    /// <summary>Cached scenarios from last successful generation, keyed by description hash.</summary>
    private IReadOnlyList<Scenario>? _cachedScenarios;
    private string? _cachedDescriptionHash;

    /// <summary>
    /// Returns cached scenarios if the description hash matches, otherwise null.
    /// Used by ScenarioReview to skip regeneration on page refresh.
    /// </summary>
    public IReadOnlyList<Scenario>? GetCachedScenarios(string descriptionHash)
    {
        if (_cachedScenarios is not null && _cachedDescriptionHash == descriptionHash)
            return _cachedScenarios;
        return null;
    }

    /// <summary>Clear the scenario cache so the next GenerateAsync call regenerates from scratch.</summary>
    public void ClearCache()
    {
        _cachedScenarios = null;
        _cachedDescriptionHash = null;
    }

    public async Task<IReadOnlyList<Scenario>> GenerateAsync(
        string projectDescription,
        string? projectName,
        IReadOnlyList<ClarifyingQA> qaPairs,
        CancellationToken ct = default,
        IProgress<Core.Frameworks.FrameworkActivityEvent>? activitySink = null)
    {
        LastError = null;

        // Check cache: if description hasn't changed, return cached scenarios
        var descHash = ComputeHash(projectDescription);
        var cached = GetCachedScenarios(descHash);
        if (cached is not null && cached.Count > 0)
        {
            _logger.LogDebug("ScenarioGenerationService: returning {Count} cached scenarios (hash match)", cached.Count);
            return cached;
        }

        if (_cli is null || !_cli.IsAvailable)
        {
            LastError = "Copilot CLI is not available — ensure it is installed and authenticated (copilot --version).";
            _logger.LogWarning("ScenarioGenerationService: {Error}", LastError);
            return Array.Empty<Scenario>();
        }

        var qaPairsText = FormatQaPairs(qaPairs);
        var effectiveProjectName = string.IsNullOrWhiteSpace(projectName)
            ? InferProjectName(projectDescription)
            : projectName;

        string prompt;
        if (_promptTemplate is not null)
        {
            var vars = new Dictionary<string, string>
            {
                ["project_name"] = effectiveProjectName,
                ["project_description"] = projectDescription,
                ["clarifying_qa_pairs"] = qaPairsText
            };
            var rendered = await _promptTemplate.RenderAsync("wizard/scenario-generation", vars)
                .ConfigureAwait(false);
            prompt = !string.IsNullOrWhiteSpace(rendered)
                ? rendered
                : BuildFallbackPrompt(effectiveProjectName, projectDescription, qaPairsText);
        }
        else
        {
            prompt = BuildFallbackPrompt(effectiveProjectName, projectDescription, qaPairsText);
        }

        var workspaceRoot = _config.Value.Workspace?.RootPath;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            workspaceRoot = Path.Combine(AppContext.BaseDirectory, ".agents");
        _scratchDir = Path.Combine(workspaceRoot, ".wizard");
        Directory.CreateDirectory(_scratchDir);

        // Attempt 1: primary prompt
        var scenarios = await TryGenerateOnceAsync(prompt, "primary", ct, activitySink);
        if (scenarios.Count > 0)
        {
            _cachedScenarios = scenarios;
            _cachedDescriptionHash = descHash;
            return scenarios;
        }

        // Attempt 2: retry with stricter instructions
        _logger.LogInformation("ScenarioGenerationService: primary attempt returned 0 scenarios — retrying with stricter prompt");
        var retryPrompt = prompt +
            "\n\nIMPORTANT: Your previous response could not be parsed. " +
            "You MUST respond with ONLY valid YAML. No markdown code fences (```). No preamble text. No explanation. " +
            "Start your response with the literal text 'project_archetype:' on the first line. " +
            "The 'scenarios:' key must contain a YAML list of scenario objects.";

        scenarios = await TryGenerateOnceAsync(retryPrompt, "retry", ct, activitySink);
        if (scenarios.Count > 0)
        {
            _cachedScenarios = scenarios;
            _cachedDescriptionHash = descHash;
            return scenarios;
        }

        LastError ??= "Scenario generation failed after 2 attempts — the AI response could not be parsed as YAML scenarios.";
        return Array.Empty<Scenario>();
    }

    private async Task<IReadOnlyList<Scenario>> TryGenerateOnceAsync(string prompt, string attemptLabel, CancellationToken ct, IProgress<Core.Frameworks.FrameworkActivityEvent>? activitySink = null)
    {
        CopilotCliResult result;
        try
        {
            // Use agentic mode so MCP tools are available for reading referenced documents.
            // The prompt template enforces READ-ONLY mode (no file creation).
            var options = new CopilotCliRequestOptions
            {
                Pool = CopilotCliPool.Agentic,
                AllowAll = true,
                CloseStdinAfterPrompt = true,
                WorkingDirectory = _scratchDir,
                WatchdogMode = CopilotCliWatchdogMode.Agentic,
                ActivitySink = activitySink,
            };
            var agenticResult = await _cli!.ExecuteAgenticSessionAsync(prompt, options, ct).ConfigureAwait(false);
            result = agenticResult.Succeeded
                ? CopilotCliResult.Success(agenticResult.LogBuffer ?? "", 0)
                : CopilotCliResult.Failure(agenticResult.ErrorMessage ?? "Agentic session failed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = $"CLI execution failed ({attemptLabel}): {ex.Message}";
            _logger.LogWarning(ex, "ScenarioGenerationService: CLI execution failed ({Attempt})", attemptLabel);
            return Array.Empty<Scenario>();
        }

        if (!result.IsSuccess)
        {
            LastError = $"CLI returned failure ({attemptLabel}): {result.Error ?? "unknown error"}";
            _logger.LogWarning("ScenarioGenerationService: CLI returned failure ({Attempt}): {Error}",
                attemptLabel, result.Error ?? "unknown");
            return Array.Empty<Scenario>();
        }

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            LastError = $"CLI returned empty output ({attemptLabel}).";
            _logger.LogWarning("ScenarioGenerationService: CLI returned empty output ({Attempt})", attemptLabel);
            return Array.Empty<Scenario>();
        }

        // Strip JSONL wrapping if JsonOutput mode is enabled in CopilotCli config
        var textOutput = CliOutputParser.ParseJsonOutput(result.Output) ?? result.Output;

        _logger.LogDebug("ScenarioGenerationService: raw output ({Attempt}, {Len} chars): {Excerpt}",
            attemptLabel, textOutput.Length,
            textOutput.Length > 500 ? textOutput[..500] + "..." : textOutput);

        try
        {
            var scenarios = ScenarioYamlExtractor.ExtractFromYamlString(textOutput, _logger);
            _logger.LogInformation("ScenarioGenerationService: parsed {Count} scenarios ({Attempt})",
                scenarios.Count, attemptLabel);
            if (scenarios.Count == 0)
                LastError = $"YAML parsed but contained 0 scenarios ({attemptLabel}).";
            return scenarios;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = $"Failed to parse YAML ({attemptLabel}): {ex.Message}";
            _logger.LogWarning(ex, "ScenarioGenerationService: failed to parse YAML output ({Attempt})", attemptLabel);
            return Array.Empty<Scenario>();
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string FormatQaPairs(IReadOnlyList<ClarifyingQA> pairs)
    {
        if (pairs.Count == 0)
            return "(none)";
        return string.Join("\n", pairs
            .Where(qa => !string.IsNullOrWhiteSpace(qa.Question))
            .Select(qa => $"Q: {qa.Question}\nA: {(string.IsNullOrWhiteSpace(qa.Answer) ? "(not answered)" : qa.Answer)}"));
    }

    private static string InferProjectName(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "My Project";
        var first = description.Trim().Split(['\n', '\r', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? description;
        return first.Length > 60 ? first[..60].TrimEnd() + "…" : first.Trim();
    }

    private static string BuildFallbackPrompt(string projectName, string description, string qaPairs) =>
        $"""
        You are a senior product analyst generating behavioral scenarios for a software project.
        Your response will be parsed directly as YAML — output ONLY a YAML document:
        no preamble, no markdown code fences, no explanation.
        Your response must begin with `project_archetype:`.

        Project name: {projectName}
        Project description:
        {description}

        Clarifying Q&A pairs:
        {qaPairs}

        Generate 5-15 scenarios. Each scenario must have: id (S01, S02...), title, journey_kind,
        actor, trigger, preconditions, steps, expected_terminal_state, observation_surfaces,
        subsystems_involved, priority (critical/important/nice-to-have), status (always proposed).
        """;

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input ?? ""));
        return Convert.ToHexString(bytes)[..16];
    }
}
