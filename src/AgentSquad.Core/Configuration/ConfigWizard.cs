using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentSquad.Core.Configuration;

public static partial class ConfigWizard
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AgentSquadConfig Run()
    {
        var config = new AgentSquadConfig();

        WriteHeader("AgentSquad Configuration Wizard");
        Console.WriteLine();

        // Project settings
        WriteHeader("Project Settings");
        config.Project.Name = Prompt("Project name");
        config.Project.Description = Prompt("Project description");

        var repoInput = Prompt("GitHub repository URL or owner/repo");
        config.Project.GitHubRepo = ParseGitHubRepo(repoInput);

        config.Project.GitHubToken = PromptSecret("GitHub Personal Access Token (PAT)");
        config.Project.DefaultBranch = Prompt("Default branch", "main");
        Console.WriteLine();

        // Model providers
        WriteHeader("Model Providers");
        Console.WriteLine("  Select which providers to configure:");
        Console.WriteLine();

        if (PromptYesNo("Configure Anthropic (Claude)?", true))
        {
            var apiKey = PromptSecret("Anthropic API key");

            config.Models["premium"] = new ModelConfig
            {
                Provider = "anthropic",
                Model = Prompt("Premium model name", "claude-opus-4-6"),
                ApiKey = apiKey,
                MaxTokensPerRequest = 8192,
                Temperature = 0.3,
            };

            config.Models["standard"] = new ModelConfig
            {
                Provider = "anthropic",
                Model = Prompt("Standard model name", "claude-sonnet-4"),
                ApiKey = apiKey,
                MaxTokensPerRequest = 4096,
                Temperature = 0.3,
            };
        }

        if (PromptYesNo("Configure OpenAI?", false))
        {
            var apiKey = PromptSecret("OpenAI API key");

            config.Models["budget"] = new ModelConfig
            {
                Provider = "openai",
                Model = Prompt("Budget model name", "gpt-4.1-mini"),
                ApiKey = apiKey,
                MaxTokensPerRequest = 4096,
                Temperature = 0.3,
            };
        }

        if (PromptYesNo("Configure Ollama (local)?", false))
        {
            var endpoint = Prompt("Ollama endpoint", "http://localhost:11434");

            config.Models["local"] = new ModelConfig
            {
                Provider = "ollama",
                Model = Prompt("Local model name", "deepseek-coder-v2"),
                Endpoint = endpoint,
                MaxTokensPerRequest = 4096,
                Temperature = 0.3,
            };
        }

        Console.WriteLine();

        // Assign model tiers to agents based on what's available
        AssignAgentModelTiers(config);

        // Limits
        WriteHeader("Resource Limits");
        var maxEngineers = Prompt("Max additional engineers", "3");
        config.Limits.MaxAdditionalEngineers = int.TryParse(maxEngineers, out var me) ? me : 3;

        var tokenBudget = Prompt("Max daily token budget", "1000000");
        config.Limits.MaxDailyTokenBudget = int.TryParse(tokenBudget, out var tb) ? tb : 1_000_000;
        Console.WriteLine();

        // Summary
        WriteHeader("Configuration Summary");
        WriteInfo($"  Project:  {config.Project.Name}");
        WriteInfo($"  Repo:     {config.Project.GitHubRepo}");
        WriteInfo($"  Branch:   {config.Project.DefaultBranch}");
        WriteInfo($"  Models:   {string.Join(", ", config.Models.Keys)}");
        WriteInfo($"  Engineers: up to {config.Limits.MaxAdditionalEngineers} additional");
        Console.WriteLine();

        return config;
    }

    public static async Task SaveToFileAsync(AgentSquadConfig config, string filePath)
    {
        var wrapper = new Dictionary<string, object>
        {
            ["Logging"] = new Dictionary<string, object>
            {
                ["LogLevel"] = new Dictionary<string, string>
                {
                    ["Default"] = "Information",
                    ["Microsoft.Hosting.Lifetime"] = "Information",
                }
            },
            ["AgentSquad"] = config,
        };

        var json = JsonSerializer.Serialize(wrapper, JsonOptions);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(filePath, json);

        WriteSuccess($"Configuration saved to {filePath}");
    }

    public static string SerializeToJson(AgentSquadConfig config)
    {
        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static void AssignAgentModelTiers(AgentSquadConfig config)
    {
        var tiers = config.Models.Keys.ToList();

        string ResolveTier(string preferred, string fallback)
        {
            if (tiers.Contains(preferred)) return preferred;
            if (tiers.Contains(fallback)) return fallback;
            return tiers.FirstOrDefault() ?? preferred;
        }

        config.Agents.ProgramManager.ModelTier = ResolveTier("premium", "standard");
        config.Agents.Researcher.ModelTier = ResolveTier("standard", "budget");
        config.Agents.Architect.ModelTier = ResolveTier("premium", "standard");
        config.Agents.PrincipalEngineer.ModelTier = ResolveTier("premium", "standard");
        config.Agents.TestEngineer.ModelTier = ResolveTier("standard", "budget");
        config.Agents.SeniorEngineerTemplate.ModelTier = ResolveTier("standard", "budget");
        config.Agents.JuniorEngineerTemplate.ModelTier = ResolveTier("local", "budget");
    }

    private static string ParseGitHubRepo(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        // Handle full URLs like https://github.com/owner/repo or https://github.com/owner/repo.git
        var urlMatch = GitHubUrlRegex().Match(input);
        if (urlMatch.Success)
            return $"{urlMatch.Groups[1].Value}/{urlMatch.Groups[2].Value}";

        // Already in owner/repo format
        var shortMatch = OwnerRepoRegex().Match(input);
        if (shortMatch.Success)
            return input.Trim();

        return input.Trim();
    }

    private static string Prompt(string label, string? defaultValue = null)
    {
        var defaultHint = defaultValue != null ? $" [{defaultValue}]" : "";
        WritePrompt($"  {label}{defaultHint}: ");
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) && defaultValue != null ? defaultValue : input ?? "";
    }

    private static string PromptSecret(string label)
    {
        WritePrompt($"  {label}: ");
        var input = Console.ReadLine()?.Trim();
        return input ?? "";
    }

    private static bool PromptYesNo(string label, bool defaultValue)
    {
        var hint = defaultValue ? "[Y/n]" : "[y/N]";
        WritePrompt($"  {label} {hint}: ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(input))
            return defaultValue;

        return input is "y" or "yes";
    }

    private static void WriteHeader(string text)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"═══ {text} ═══");
        Console.ForegroundColor = prev;
    }

    private static void WritePrompt(string text)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(text);
        Console.ForegroundColor = prev;
    }

    private static void WriteInfo(string text)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }

    private static void WriteSuccess(string text)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }

    [GeneratedRegex(@"https?://github\.com/([a-zA-Z0-9\-_.]+)/([a-zA-Z0-9\-_.]+?)(?:\.git)?/?$")]
    private static partial Regex GitHubUrlRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9\-_.]+/[a-zA-Z0-9\-_.]+$")]
    private static partial Regex OwnerRepoRegex();
}
