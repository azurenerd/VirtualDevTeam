using System.Text.Json;
using VirtualDevTeam.Core.DevPlatform.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Loads and saves <see cref="DevelopSettings"/> from develop-settings.json.
/// Thread-safe via SemaphoreSlim; uses atomic temp-file-then-move writes.
/// </summary>
public sealed class DevelopSettingsService : IDisposable
{
    private readonly string _filePath;
    private readonly ILogger<DevelopSettingsService> _logger;
    private readonly VirtualDevTeamConfig? _existingConfig;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Last-loaded develop settings, refreshed by <see cref="LoadAsync"/> and
    /// <see cref="MergeIntoConfig"/>. Consumed by <see cref="GateCheckService"/>
    /// to consult the wizard's master gate switch BEFORE falling back to
    /// <see cref="VirtualDevTeamConfig.HumanInteraction"/> from appsettings.json.
    /// Returns <c>null</c> until the first <c>LoadAsync</c> call.
    /// </summary>
    public DevelopSettings? Current { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DevelopSettingsService(
        ILogger<DevelopSettingsService> logger,
        IOptions<VirtualDevTeamConfig>? existingConfig = null,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _existingConfig = existingConfig?.Value;
        _filePath = filePath ?? Path.Combine(Directory.GetCurrentDirectory(), "develop-settings.json");
    }

    /// <summary>
    /// Reads develop-settings.json. Returns defaults if the file doesn't exist.
    /// </summary>
    public async Task<DevelopSettings> LoadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
            {
                if (_existingConfig is not null)
                {
                    _logger.LogInformation(
                        "Develop settings file not found at {Path}, pre-populating from existing config", _filePath);
                    var seeded = CreateFromExistingConfig(_existingConfig);
                    // Persist so subsequent loads use the file
                    var seedJson = JsonSerializer.Serialize(seeded, JsonOptions);
                    var tempPath = _filePath + ".tmp";
                    await File.WriteAllTextAsync(tempPath, seedJson, ct);
                    File.Move(tempPath, _filePath, overwrite: true);
                    Current = seeded;
                    return seeded;
                }

                _logger.LogDebug("Develop settings file not found at {Path}, returning defaults", _filePath);
                var defaults = new DevelopSettings();
                Current = defaults;
                return defaults;
            }

            var json = await File.ReadAllTextAsync(_filePath, ct);
            var settings = JsonSerializer.Deserialize<DevelopSettings>(json, JsonOptions);
            _logger.LogDebug("Loaded develop settings from {Path}", _filePath);
            var resolved = settings ?? new DevelopSettings();
            Current = resolved;
            return resolved;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize develop settings from {Path}, returning defaults", _filePath);
            var fallback = new DevelopSettings();
            Current = fallback;
            return fallback;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Writes develop-settings.json atomically (write to temp, then move).
    /// </summary>
    public async Task SaveAsync(DevelopSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, _filePath, overwrite: true);
            _logger.LogInformation("Saved develop settings to {Path}", _filePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Overlays develop settings onto the runtime <see cref="VirtualDevTeamConfig"/>.
    /// Only touches project-level fields (description, tech stack, repo settings).
    /// Does NOT modify PATs, model config, agent config, limits, or any non-project fields.
    /// </summary>
    public void MergeIntoConfig(VirtualDevTeamConfig config, DevelopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);

        // Cache so GateCheckService can consult develop-settings's master switch
        // BEFORE falling back to appsettings.json HumanInteraction.Enabled.
        Current = settings;

        MergeIntoConfigStatic(config, settings);
    }

    /// <summary>
    /// Pure-static variant of <see cref="MergeIntoConfig"/> that doesn't touch the
    /// <see cref="Current"/> cache or any instance state. Used by
    /// <see cref="DevelopSettingsPostConfigure"/> which runs without an instance during
    /// options-snapshot construction. Keeping the merge logic in one place avoids drift
    /// between the static and instance entrypoints.
    /// </summary>
    public static void MergeIntoConfigStatic(VirtualDevTeamConfig config, DevelopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);
        MergeBody(config, settings);
    }

    private static void MergeBody(VirtualDevTeamConfig config, DevelopSettings settings)
    {
        // Raw description always goes to Config.Project.Description — PM, Researcher,
        // and Architect use this for rich doc creation (may trigger MCP doc reads).
        if (!string.IsNullOrWhiteSpace(settings.Description))
            config.Project.Description = settings.Description;

        // Resolved summary goes to ResolvedDescription — SE, TE, reviews, and all
        // other agents use this to avoid redundant MCP doc reads on every prompt.
        if (!string.IsNullOrWhiteSpace(settings.ResolvedProjectDescription))
            config.Project.ResolvedDescription = settings.ResolvedProjectDescription;

        // Existing project context — AI-generated summary of the codebase, docs, and conventions.
        // Flows to all prompt templates via {{existing_project_context}} auto-fill.
        if (!string.IsNullOrWhiteSpace(settings.ExistingProjectContext))
            config.Project.ExistingProjectContext = settings.ExistingProjectContext;

        // Append answered clarifying Q&A to the description
        var answeredQAs = settings.ClarifyingAnswers?
            .Where(qa => !string.IsNullOrWhiteSpace(qa.Answer))
            .ToList();
        if (answeredQAs?.Count > 0 && !string.IsNullOrWhiteSpace(config.Project.Description))
        {
            var sb = new System.Text.StringBuilder(config.Project.Description);
            sb.AppendLine().AppendLine().AppendLine("## Clarifying Details");
            foreach (var qa in answeredQAs)
            {
                sb.AppendLine($"**Q: {qa.Question}**");
                sb.AppendLine($"A: {qa.Answer}");
                sb.AppendLine();
            }
            config.Project.Description = sb.ToString().TrimEnd();
        }

        // Populate unanswered questions for decision gating
        var unansweredQs = settings.GetUnansweredQuestions();
        if (unansweredQs.Count > 0)
        {
            config.UnansweredDecisionQuestions = unansweredQs.Select(q => q.Question).ToList();
        }

        if (!string.IsNullOrWhiteSpace(settings.TechStack))
            config.Project.TechStack = settings.TechStack;

        if (!string.IsNullOrWhiteSpace(settings.ExecutiveUsername))
            config.Project.ExecutiveGitHubUsername = settings.ExecutiveUsername;

        config.Project.ParentWorkItemId = settings.ParentWorkItemId;

        // Agent docs and issue mode
        if (!string.IsNullOrWhiteSpace(settings.DocsFolderPath))
            config.Project.DocsFolderPath = settings.DocsFolderPath;

        config.Limits.SingleIssueMode = settings.SingleIssueMode;
        // Map PrMode string from settings to the enum
        if (Enum.TryParse<PrDeliveryMode>(settings.PrMode, ignoreCase: true, out var prMode))
            config.Limits.PrMode = prMode;
        else
            config.Limits.PrMode = PrDeliveryMode.SinglePR; // safe default

        // Agent reviewer preferences
        config.Review.PmReviews = settings.AgentReviewers.PmReviews;
        config.Review.ArchitectReviews = settings.AgentReviewers.ArchitectReviews;
        config.Review.EngineerReviews = settings.AgentReviewers.EngineerReviews;
        config.Review.TestEngineerReviews = settings.AgentReviewers.TestEngineerReviews;

        // Working branch
        config.Project.WorkingBranch = string.IsNullOrWhiteSpace(settings.WorkingBranch) ? null : settings.WorkingBranch;

        // Platform-specific repo settings
        if (string.Equals(settings.Platform, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            config.DevPlatform.Platform = DevPlatformType.GitHub;

            if (!string.IsNullOrWhiteSpace(settings.GitHub.Repo))
            {
                config.Project.GitHubRepo = settings.GitHub.Repo;
                // Derive project name from repo (e.g., "owner/MyProject" → "MyProject")
                var repoName = settings.GitHub.Repo.Contains('/')
                    ? settings.GitHub.Repo.Split('/')[1]
                    : settings.GitHub.Repo;
                config.Project.Name = repoName;
            }

            if (!string.IsNullOrWhiteSpace(settings.GitHub.DefaultBranch))
                config.Project.DefaultBranch = settings.GitHub.DefaultBranch;
        }
        else if (string.Equals(settings.Platform, "AzureDevOps", StringComparison.OrdinalIgnoreCase))
        {
            config.DevPlatform.Platform = DevPlatformType.AzureDevOps;
            config.DevPlatform.AzureDevOps ??= new AzureDevOpsConfig();

            if (!string.IsNullOrWhiteSpace(settings.AzureDevOps.Organization))
                config.DevPlatform.AzureDevOps.Organization = settings.AzureDevOps.Organization;

            if (!string.IsNullOrWhiteSpace(settings.AzureDevOps.Project))
                config.DevPlatform.AzureDevOps.Project = settings.AzureDevOps.Project;

            if (!string.IsNullOrWhiteSpace(settings.AzureDevOps.Repository))
            {
                config.DevPlatform.AzureDevOps.Repository = settings.AzureDevOps.Repository;
                // Derive project name from repository name
                config.Project.Name = settings.AzureDevOps.Repository;
            }

            if (!string.IsNullOrWhiteSpace(settings.AzureDevOps.DefaultBranch))
                config.DevPlatform.AzureDevOps.DefaultBranch = settings.AzureDevOps.DefaultBranch;
        }

        // Local dev mode override: agents work locally, final PR goes to the chosen platform
        if (settings.UseLocalDevMode)
            config.DevPlatform.Platform = DevPlatformType.Local;

        // Map auth method
        config.DevPlatform.AuthMethod = settings.AuthMethod switch
        {
            "AzureCliBearer" => DevPlatformAuthMethod.AzureCliBearer,
            "ServicePrincipal" => DevPlatformAuthMethod.ServicePrincipal,
            "GhCli" => DevPlatformAuthMethod.GhCli,
            _ => DevPlatformAuthMethod.Pat
        };

        // Gate preferences override
        if (settings.GatePreferences is not null)
        {
            config.HumanInteraction.Enabled = settings.GatePreferences.Enabled;
            foreach (var (gateId, requiresHuman) in settings.GatePreferences.Gates)
            {
                if (config.HumanInteraction.Gates.TryGetValue(gateId, out var gateConfig))
                {
                    gateConfig.RequiresHuman = requiresHuman;
                }
                else
                {
                    config.HumanInteraction.Gates[gateId] = new GateConfig { RequiresHuman = requiresHuman };
                }
            }
        }

        // Pre-PR clarification gate toggle (convenience shortcut from wizard).
        // This is a per-gate override only — it must NOT re-enable the master switch
        // when the wizard explicitly disabled it. The master switch is the single
        // source of truth for "all gates auto-pass" (see GateCheckService.AreAllGatesDisabled).
        if (config.HumanInteraction.Gates.TryGetValue(GateIds.PrePRClarification, out var clarifyGate))
        {
            clarifyGate.RequiresHuman = settings.PrePRClarificationGate;
        }
        else
        {
            config.HumanInteraction.Gates[GateIds.PrePRClarification] = new GateConfig { RequiresHuman = settings.PrePRClarificationGate };
        }

        // UI quality gate toggle (PM blocks force-approval on failing UI tests when false)
        config.Review.AllowFailingUiTests = settings.AllowFailingUiTests;

        // Image-generation settings (wizard step 2 footer)
        if (settings.AzureOpenAIImage is not null)
        {
            var img = settings.AzureOpenAIImage;
            config.AzureOpenAIImage.Endpoint = img.Endpoint ?? "";
            if (!string.IsNullOrWhiteSpace(img.ApiVersion))
                config.AzureOpenAIImage.ApiVersion = img.ApiVersion;
            if (!string.IsNullOrWhiteSpace(img.PrimaryDeployment))
                config.AzureOpenAIImage.PrimaryDeployment = img.PrimaryDeployment;
            if (img.FallbackDeployments is { Count: > 0 })
                config.AzureOpenAIImage.FallbackDeployments = img.FallbackDeployments
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => d.Trim())
                    .ToList();
            if (img.MaxAttemptsPerImage > 0)
                config.AzureOpenAIImage.MaxAttemptsPerImage = img.MaxAttemptsPerImage;
            if (img.VerificationConfidenceThreshold is > 0.0 and <= 1.0)
                config.AzureOpenAIImage.VerificationConfidenceThreshold = img.VerificationConfidenceThreshold;
            config.AzureOpenAIImage.EnableVerification = img.EnableVerification;
            config.AzureOpenAIImage.AuthMethod = string.Equals(img.AuthMethod, "ApiKey", StringComparison.OrdinalIgnoreCase)
                ? ImageAuthMethod.ApiKey
                : ImageAuthMethod.DefaultAzureCredential;
        }

        // FlowMonitor auto-approval timeout
        config.FlowMonitor.AutoApprovalMinutes = settings.FlowMonitorAutoApprovalMinutes;

        // Workspace mode from develop-settings (Large Project support)
        if (!string.IsNullOrWhiteSpace(settings.WorkspaceMode)
            && Enum.TryParse<VirtualDevTeam.Core.Workspace.WorkspaceMode>(settings.WorkspaceMode, ignoreCase: true, out var wsMode))
        {
            config.Workspace.WorkspaceMode = wsMode;
        }
        else if (!string.IsNullOrWhiteSpace(settings.ExistingRepoPath))
        {
            // User provided a local checkout path — use InPlace mode
            config.Workspace.WorkspaceMode = VirtualDevTeam.Core.Workspace.WorkspaceMode.InPlace;
        }
        else if (string.IsNullOrWhiteSpace(settings.WorkspaceMode))
        {
            // No workspace mode or path specified — default to Worktree
            // (Clone mode is deprecated; Worktree auto-creates .agents/.shared-clone)
            config.Workspace.WorkspaceMode = VirtualDevTeam.Core.Workspace.WorkspaceMode.Worktree;
        }
        if (!string.IsNullOrWhiteSpace(settings.ExistingRepoPath))
            config.Workspace.LocalCheckoutPath = settings.ExistingRepoPath;
        if (!string.IsNullOrWhiteSpace(settings.WorktreeRoot))
            config.Workspace.WorktreeRoot = settings.WorktreeRoot;
        if (settings.SparseCheckoutPaths is { Count: > 0 })
            config.Workspace.SparseCheckoutPaths = settings.SparseCheckoutPaths;
        if (settings.LargeProject is not null)
            config.LargeProject = settings.LargeProject;

        // Local DevPlatform: redirect agent pushes to the local bare repo, not GitHub.
        // The bare repo path follows the convention: {RootPath}/local-platform/{repoName}.git
        // IMPORTANT: Set unconditionally — do NOT gate on Directory.Exists. The bare repo
        // is created by LocalPlatformInitializer (IHostedService) which may run AFTER this
        // config merge. If we skip the redirect, agents push to GitHub origin instead of
        // the bare repo, leaking agent/* branches to the remote (lesson #155).
        if (config.DevPlatform.Platform == VirtualDevTeam.Core.DevPlatform.Config.DevPlatformType.Local
            && !string.IsNullOrWhiteSpace(config.Workspace.RootPath))
        {
            var repoName = config.Project.Name ?? "project";
            var bareRepoPath = Path.Combine(config.Workspace.RootPath, "local-platform", $"{repoName}.git");
            config.Workspace.AgentPushRemote = bareRepoPath;
        }

        // CLI wrapper command override (per-user, so appsettings default doesn't break
        // users who don't have the wrapper installed). Null = use appsettings default.
        // Empty string = explicitly disable wrapper.
        if (settings.WrapperCommand is not null)
            config.CopilotCli.WrapperCommand = settings.WrapperCommand;
    }

    /// <summary>
    /// Creates initial DevelopSettings from existing VirtualDevTeamConfig.
    /// Called on first load when develop-settings.json doesn't exist.
    /// </summary>
    public DevelopSettings CreateFromExistingConfig(VirtualDevTeamConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var settings = new DevelopSettings();

        // Map platform
        settings.UseLocalDevMode = config.DevPlatform.Platform == DevPlatformType.Local;
        settings.Platform = config.DevPlatform.Platform switch
        {
            DevPlatformType.GitHub => "GitHub",
            DevPlatformType.AzureDevOps => "AzureDevOps",
            // When Local mode, preserve the underlying platform (default to GitHub)
            DevPlatformType.Local => "GitHub",
            _ => "GitHub"
        };

        // Map auth method
        settings.AuthMethod = config.DevPlatform.AuthMethod switch
        {
            DevPlatformAuthMethod.Pat => "Pat",
            DevPlatformAuthMethod.AzureCliBearer => "AzureCliBearer",
            DevPlatformAuthMethod.ServicePrincipal => "ServicePrincipal",
            DevPlatformAuthMethod.GhCli => "GhCli",
            _ => "Pat"
        };

        // Map GitHub settings
        settings.GitHub = new GitHubRepoSettings
        {
            Repo = config.Project.GitHubRepo,
            DefaultBranch = config.Project.DefaultBranch
        };

        // Map ADO settings
        settings.AzureDevOps = new AdoRepoSettings
        {
            Organization = config.DevPlatform.AzureDevOps?.Organization ?? "",
            Project = config.DevPlatform.AzureDevOps?.Project ?? "",
            Repository = config.DevPlatform.AzureDevOps?.Repository ?? "",
            DefaultBranch = config.DevPlatform.AzureDevOps?.DefaultBranch ?? config.Project.DefaultBranch
        };

        // Map project settings — never import PATs
        settings.Description = config.Project.Description;
        settings.TechStack = config.Project.TechStack;
        settings.ExecutiveUsername = config.Project.ExecutiveGitHubUsername;
        settings.ParentWorkItemId = config.Project.ParentWorkItemId;
        settings.DocsFolderPath = config.Project.DocsFolderPath;
        settings.SingleIssueMode = config.Limits.SingleIssueMode;
        settings.PrMode = config.Limits.PrMode.ToString();
        settings.PrePRClarificationGate = config.HumanInteraction.Gates.TryGetValue(GateIds.PrePRClarification, out var clarify)
            && clarify.RequiresHuman;
        settings.AllowFailingUiTests = config.Review.AllowFailingUiTests;
        settings.WorkingBranch = config.Project.WorkingBranch;

        // Agent reviewer preferences
        settings.AgentReviewers = new AgentReviewerSettings
        {
            PmReviews = config.Review.PmReviews,
            ArchitectReviews = config.Review.ArchitectReviews,
            EngineerReviews = config.Review.EngineerReviews,
            TestEngineerReviews = config.Review.TestEngineerReviews,
        };

        return settings;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _lock.Dispose();
            _disposed = true;
        }
    }
}
