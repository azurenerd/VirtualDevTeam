namespace AgentSquad.Core.Configuration;

public class AgentSquadConfig
{
    public ProjectConfig Project { get; set; } = new();
    public Dictionary<string, ModelConfig> Models { get; set; } = new();
    public AgentConfigs Agents { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
    public DashboardConfig Dashboard { get; set; } = new();
    public CopilotCliConfig CopilotCli { get; set; } = new();
}

public class ProjectConfig
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
    public string GitHubToken { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// The primary tech stack for the project. Agents use this in all prompts
    /// to ensure generated code, architecture, and plans target the correct language/framework.
    /// Examples: "C# .NET 8 with Blazor Server", "TypeScript with Next.js and React", "Python with FastAPI"
    /// </summary>
    public string TechStack { get; set; } = "C# .NET 8 with Blazor Server";

    /// <summary>
    /// GitHub username of the Executive stakeholder (human) for escalation.
    /// The PM agent creates executive-request Issues assigned to this user when
    /// it needs human clarification on requirements.
    /// </summary>
    public string ExecutiveGitHubUsername { get; set; } = "azurenerd";

    /// <summary>
    /// Custom prompt that guides the Researcher agent on what to investigate.
    /// When empty, a comprehensive default prompt is generated from the project description.
    /// Use this to steer research toward specific areas, technologies, or concerns.
    /// </summary>
    public string ResearchPrompt { get; set; } = "";
}

public class ModelConfig
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string? Endpoint { get; set; }
    public int MaxTokensPerRequest { get; set; } = 4096;
    public double Temperature { get; set; } = 0.3;
}

public class AgentConfigs
{
    public AgentConfig ProgramManager { get; set; } = new() { ModelTier = "premium", Enabled = true };
    public AgentConfig Researcher { get; set; } = new() { ModelTier = "standard", Enabled = true };
    public AgentConfig Architect { get; set; } = new() { ModelTier = "premium", Enabled = true };
    public AgentConfig PrincipalEngineer { get; set; } = new() { ModelTier = "premium", Enabled = true };
    public AgentConfig TestEngineer { get; set; } = new() { ModelTier = "standard", Enabled = true };
    public AgentConfig SeniorEngineerTemplate { get; set; } = new() { ModelTier = "standard" };
    public AgentConfig JuniorEngineerTemplate { get; set; } = new() { ModelTier = "local" };
}

public class AgentConfig
{
    public string ModelTier { get; set; } = "standard";
    public bool Enabled { get; set; } = true;
    public int? MaxDailyTokens { get; set; }
}

public class LimitsConfig
{
    public int MaxAdditionalEngineers { get; set; } = 3;
    public int MaxDailyTokenBudget { get; set; } = 1_000_000;
    public int GitHubPollIntervalSeconds { get; set; } = 30;
    public int AgentTimeoutMinutes { get; set; } = 60;
    public int MaxConcurrentAgents { get; set; } = 10;

    /// <summary>
    /// Maximum number of clarification round-trips between an engineer and the PM
    /// on a single Issue before the engineer proceeds with best understanding.
    /// </summary>
    public int MaxClarificationRoundTrips { get; set; } = 5;

    /// <summary>
    /// Maximum number of rework cycles (review → change → re-review) per PR before
    /// the reviewer force-approves to prevent infinite loops.
    /// </summary>
    public int MaxReworkCycles { get; set; } = 3;

    /// <summary>
    /// If the Principal Engineer estimates all remaining tasks can be completed within
    /// this many minutes, it won't request additional engineers.
    /// </summary>
    public int SelfCompletionThresholdMinutes { get; set; } = 10;

    /// <summary>
    /// Minimum number of parallelizable tasks required before the Principal Engineer
    /// requests a new engineer from the PM.
    /// </summary>
    public int MinParallelizableTasksForNewEngineer { get; set; } = 3;
}

public class DashboardConfig
{
    public int Port { get; set; } = 5000;
    public bool EnableSignalR { get; set; } = true;
}

/// <summary>
/// Configuration for the Copilot CLI AI provider.
/// When enabled, agents use the copilot CLI as the default AI backend instead of API keys.
/// </summary>
public class CopilotCliConfig
{
    /// <summary>Whether to use Copilot CLI as the default AI provider.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Path to the copilot executable. Defaults to "copilot" (assumes it's on PATH).</summary>
    public string ExecutablePath { get; set; } = "copilot";

    /// <summary>Maximum number of concurrent copilot processes.</summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>Timeout in seconds for a single AI request.</summary>
    public int RequestTimeoutSeconds { get; set; } = 600;

    /// <summary>Alias for RequestTimeoutSeconds (backwards compatibility with appsettings).</summary>
    public int ProcessTimeoutSeconds
    {
        get => RequestTimeoutSeconds;
        set => RequestTimeoutSeconds = value;
    }

    /// <summary>Model to request from Copilot CLI (e.g., "claude-opus-4.6").</summary>
    public string ModelName { get; set; } = "claude-opus-4.6";

    /// <summary>Alias for ModelName (backwards compatibility with appsettings).</summary>
    public string DefaultModel
    {
        get => ModelName;
        set => ModelName = value;
    }

    /// <summary>
    /// Automatically approve all interactive prompts (y/n, selections, etc.).
    /// When true, the interactive watchdog auto-responds to unexpected prompts.
    /// </summary>
    public bool AutoApprovePrompts { get; set; } = true;

    /// <summary>Reasoning effort level: "low", "medium", "high", "xhigh", or null for default.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Use --silent flag to suppress stats and chrome in output.</summary>
    public bool SilentMode { get; set; } = true;

    /// <summary>Use --output-format json for structured JSONL output.</summary>
    public bool JsonOutput { get; set; } = false;

    /// <summary>
    /// When true, injects a brevity constraint into every prompt so AI responses
    /// return in ~10 seconds instead of minutes. Uses a faster model (claude-haiku-4.5)
    /// and limits responses to 500 words. Useful for testing E2E flow quickly.
    /// Set to false for production-quality output.
    /// </summary>
    public bool FastMode { get; set; } = false;

    /// <summary>Model to use when FastMode is enabled. Defaults to claude-haiku-4.5.</summary>
    public string FastModeModel { get; set; } = "claude-haiku-4.5";

    /// <summary>
    /// Maximum number of automatic retries for transient errors (auth failures, timeouts).
    /// Retries use exponential backoff (5s, 15s, 30s). Set to 0 to disable retries.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// When true, multi-turn agents (Researcher, Architect) collapse their
    /// chain-of-thought into a single comprehensive prompt instead of multiple
    /// conversational turns. Faster but potentially less thorough.
    /// Automatically enabled when FastMode is true.
    /// </summary>
    public bool SinglePassMode { get; set; } = false;

    /// <summary>Tools to exclude from the CLI's available tools (e.g., "shell", "write").</summary>
    public List<string> ExcludedTools { get; set; } = new();

    /// <summary>Working directory for copilot processes. Null uses the current directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Additional arguments to pass to the copilot CLI.</summary>
    public string? AdditionalArgs { get; set; }
}
