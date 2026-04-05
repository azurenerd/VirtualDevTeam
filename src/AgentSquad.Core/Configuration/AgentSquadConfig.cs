namespace AgentSquad.Core.Configuration;

public class AgentSquadConfig
{
    public ProjectConfig Project { get; set; } = new();
    public Dictionary<string, ModelConfig> Models { get; set; } = new();
    public AgentConfigs Agents { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
    public DashboardConfig Dashboard { get; set; } = new();
}

public class ProjectConfig
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
    public string GitHubToken { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
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
    public int AgentTimeoutMinutes { get; set; } = 15;
    public int MaxConcurrentAgents { get; set; } = 10;
}

public class DashboardConfig
{
    public int Port { get; set; } = 5000;
    public bool EnableSignalR { get; set; } = true;
}
