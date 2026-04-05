using System.Text.RegularExpressions;

namespace AgentSquad.Core.Configuration;

public static partial class ConfigValidator
{
    private static readonly string[] CloudProviders = ["anthropic", "openai"];

    public static List<string> Validate(AgentSquadConfig config)
    {
        var errors = new List<string>();

        ValidateProject(config.Project, errors);
        ValidateModels(config.Models, errors);
        ValidateAgents(config.Agents, config.Models, errors);
        ValidateLimits(config.Limits, errors);

        return errors;
    }

    private static void ValidateProject(ProjectConfig project, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(project.Name))
            errors.Add("Project.Name is required.");

        if (!string.IsNullOrWhiteSpace(project.GitHubRepo) && !GitHubRepoRegex().IsMatch(project.GitHubRepo))
            errors.Add($"Project.GitHubRepo '{project.GitHubRepo}' is not in 'owner/repo' format.");

        if (!string.IsNullOrWhiteSpace(project.GitHubRepo) && string.IsNullOrWhiteSpace(project.GitHubToken))
            errors.Add("Project.GitHubToken is required when GitHubRepo is configured.");

        if (string.IsNullOrWhiteSpace(project.DefaultBranch))
            errors.Add("Project.DefaultBranch is required.");
    }

    private static void ValidateModels(Dictionary<string, ModelConfig> models, List<string> errors)
    {
        if (models.Count == 0)
        {
            errors.Add("At least one model must be configured in Models.");
            return;
        }

        foreach (var (tier, model) in models)
        {
            if (string.IsNullOrWhiteSpace(model.Provider))
                errors.Add($"Models['{tier}'].Provider is required.");

            if (string.IsNullOrWhiteSpace(model.Model))
                errors.Add($"Models['{tier}'].Model is required.");

            if (CloudProviders.Contains(model.Provider, StringComparer.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(model.ApiKey))
            {
                errors.Add($"Models['{tier}'].ApiKey is required for provider '{model.Provider}'.");
            }

            if (string.Equals(model.Provider, "ollama", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(model.Endpoint))
                {
                    errors.Add($"Models['{tier}'].Endpoint is required for Ollama provider.");
                }
                else if (!Uri.TryCreate(model.Endpoint, UriKind.Absolute, out var uri)
                         || (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    errors.Add($"Models['{tier}'].Endpoint '{model.Endpoint}' is not a valid HTTP/HTTPS URL.");
                }
            }

            if (model.MaxTokensPerRequest <= 0)
                errors.Add($"Models['{tier}'].MaxTokensPerRequest must be greater than 0.");

            if (model.Temperature is < 0.0 or > 2.0)
                errors.Add($"Models['{tier}'].Temperature must be between 0.0 and 2.0.");
        }
    }

    private static void ValidateAgents(AgentConfigs agents, Dictionary<string, ModelConfig> models, List<string> errors)
    {
        var agentProperties = new Dictionary<string, AgentConfig>
        {
            ["ProgramManager"] = agents.ProgramManager,
            ["Researcher"] = agents.Researcher,
            ["Architect"] = agents.Architect,
            ["PrincipalEngineer"] = agents.PrincipalEngineer,
            ["TestEngineer"] = agents.TestEngineer,
            ["SeniorEngineerTemplate"] = agents.SeniorEngineerTemplate,
            ["JuniorEngineerTemplate"] = agents.JuniorEngineerTemplate,
        };

        foreach (var (name, agent) in agentProperties)
        {
            if (string.IsNullOrWhiteSpace(agent.ModelTier))
            {
                errors.Add($"Agents.{name}.ModelTier is required.");
                continue;
            }

            if (!models.ContainsKey(agent.ModelTier))
                errors.Add($"Agents.{name}.ModelTier '{agent.ModelTier}' does not match any configured model tier. Available: {string.Join(", ", models.Keys)}.");
        }
    }

    private static void ValidateLimits(LimitsConfig limits, List<string> errors)
    {
        if (limits.MaxAdditionalEngineers < 0)
            errors.Add("Limits.MaxAdditionalEngineers must be >= 0.");

        if (limits.MaxDailyTokenBudget <= 0)
            errors.Add("Limits.MaxDailyTokenBudget must be > 0.");

        if (limits.GitHubPollIntervalSeconds < 5)
            errors.Add("Limits.GitHubPollIntervalSeconds must be >= 5.");

        if (limits.AgentTimeoutMinutes <= 0)
            errors.Add("Limits.AgentTimeoutMinutes must be > 0.");

        if (limits.MaxConcurrentAgents <= 0)
            errors.Add("Limits.MaxConcurrentAgents must be > 0.");
    }

    [GeneratedRegex(@"^[a-zA-Z0-9\-_.]+/[a-zA-Z0-9\-_.]+$")]
    private static partial Regex GitHubRepoRegex();
}
