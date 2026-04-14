using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Persistence;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.Options;

namespace AgentSquad.Dashboard.Services;

/// <summary>
/// Service for reading/writing appsettings.json and performing GitHub repo cleanup operations.
/// </summary>
public sealed class ConfigurationService : IConfigurationService
{
    private readonly IOptionsMonitor<AgentSquadConfig> _config;
    private readonly IGitHubService _github;
    private readonly AgentRegistry _registry;
    private readonly AgentSpawnManager _spawnManager;
    private readonly WorkflowStateMachine _workflow;
    private readonly IDashboardDataService _dashboard;
    private readonly ILogger<ConfigurationService> _logger;
    private readonly IConfiguration _rootConfiguration;
    private readonly string _appSettingsPath;

    /// <summary>Tracks the latest saved config so GetCurrentConfig returns fresh data even before IOptions reloads.</summary>
    private AgentSquadConfig? _lastSavedConfig;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null // preserve PascalCase
    };

    public ConfigurationService(
        IOptionsMonitor<AgentSquadConfig> config,
        IGitHubService github,
        AgentRegistry registry,
        AgentSpawnManager spawnManager,
        WorkflowStateMachine workflow,
        IDashboardDataService dashboard,
        ILogger<ConfigurationService> logger,
        IConfiguration rootConfiguration,
        IWebHostEnvironment env)
    {
        _config = config;
        _github = github;
        _registry = registry;
        _spawnManager = spawnManager;
        _workflow = workflow;
        _dashboard = dashboard;
        _logger = logger;
        _rootConfiguration = rootConfiguration;
        _appSettingsPath = Path.Combine(env.ContentRootPath, "appsettings.json");
    }

    /// <summary>Returns the latest config — either from last save or from IOptionsMonitor.</summary>
    public AgentSquadConfig GetCurrentConfig() => _lastSavedConfig ?? _config.CurrentValue;

    /// <summary>
    /// Validates a GitHub PAT token against a specified repo.
    /// Returns a result with repo info on success, or error message on failure.
    /// </summary>
    public async Task<PatValidationResult> ValidatePatAsync(string token, string repoFullName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new PatValidationResult { Success = false, Error = "Token is empty." };

        if (string.IsNullOrWhiteSpace(repoFullName) || !repoFullName.Contains('/'))
            return new PatValidationResult { Success = false, Error = "Repo must be in 'owner/repo' format." };

        var parts = repoFullName.Split('/', 2);
        try
        {
            var client = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("AgentSquad-Validate"))
            {
                Credentials = new Octokit.Credentials(token)
            };

            var repo = await client.Repository.Get(parts[0], parts[1]);
            var user = await client.User.Current();

            // Check key permissions by testing a read-only endpoint
            var scopes = new List<string>();
            try
            {
                await client.Issue.GetAllForRepository(parts[0], parts[1],
                    new Octokit.RepositoryIssueRequest { State = Octokit.ItemStateFilter.Open });
                scopes.Add("issues:read");
            }
            catch { /* no access */ }

            return new PatValidationResult
            {
                Success = true,
                RepoName = repo.FullName,
                RepoDescription = repo.Description ?? "(no description)",
                IsPrivate = repo.Private,
                DefaultBranch = repo.DefaultBranch,
                AuthenticatedUser = user.Login,
                Permissions = scopes
            };
        }
        catch (Octokit.NotFoundException)
        {
            return new PatValidationResult { Success = false, Error = $"Repository '{repoFullName}' not found. Check the repo name and that your PAT has access." };
        }
        catch (Octokit.AuthorizationException)
        {
            return new PatValidationResult { Success = false, Error = "Authorization failed. The PAT token is invalid or expired." };
        }
        catch (Exception ex)
        {
            return new PatValidationResult { Success = false, Error = $"Validation failed: {ex.Message}" };
        }
    }

    /// <summary>Returns the GitHub repo name from config.</summary>
    public string GetRepoName() => _config.CurrentValue.Project.GitHubRepo;

    /// <summary>Reads the raw JSON from appsettings.json.</summary>
    public async Task<JsonObject?> ReadAppSettingsAsync()
    {
        if (!File.Exists(_appSettingsPath))
        {
            _logger.LogWarning("appsettings.json not found at {Path}", _appSettingsPath);
            return null;
        }

        var json = await File.ReadAllTextAsync(_appSettingsPath);
        return JsonNode.Parse(json)?.AsObject();
    }

    /// <summary>
    /// Merges updated AgentSquad config values into appsettings.json, preserving
    /// non-AgentSquad sections (e.g., Logging).
    /// </summary>
    public async Task SaveConfigAsync(AgentSquadConfig updatedConfig)
    {
        var root = await ReadAppSettingsAsync() ?? new JsonObject();

        // Serialize the updated config section
        var configJson = JsonSerializer.SerializeToNode(updatedConfig, JsonOptions);
        root["AgentSquad"] = configJson;

        var output = root.ToJsonString(JsonOptions);

        // Write to a temp file then move — avoids "user-mapped section open" IOException
        // on Windows where ASP.NET's file watcher memory-maps appsettings.json.
        var tempPath = _appSettingsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, output);
        File.Move(tempPath, _appSettingsPath, overwrite: true);

        // Cache the saved config so GetCurrentConfig returns it immediately
        _lastSavedConfig = updatedConfig;

        // Trigger IConfiguration reload so IOptionsMonitor picks up the new values
        if (_rootConfiguration is IConfigurationRoot configRoot)
            configRoot.Reload();

        _logger.LogInformation("Configuration saved to {Path}", _appSettingsPath);
    }

    /// <summary>
    /// Scans the GitHub repo and returns a summary of what would be cleaned up.
    /// </summary>
    public async Task<CleanupSummary> ScanRepoForCleanupAsync(CancellationToken ct = default)
    {
        var config = _config.CurrentValue.Project;
        var summary = new CleanupSummary { RepoFullName = config.GitHubRepo };

        try
        {
            // Get all issues (open + closed)
            var allIssues = await _github.GetAllIssuesAsync(ct);
            summary.OpenIssues = allIssues.Count(i => i.State == "open");
            summary.ClosedIssues = allIssues.Count(i => i.State != "open");

            // Get all PRs
            var allPrs = await _github.GetAllPullRequestsAsync(ct);
            summary.OpenPrs = allPrs.Count(p => p.State == "open" && !p.IsMerged);
            summary.MergedPrs = allPrs.Count(p => p.IsMerged);
            summary.ClosedPrs = allPrs.Count(p => p.State != "open" && !p.IsMerged);

            // Get repo file tree
            var files = await _github.GetRepositoryTreeAsync(config.DefaultBranch, ct);
            summary.FileCount = files.Count;
            summary.Files = files.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan repo for cleanup");
            summary.Error = ex.Message;
        }

        return summary;
    }

    /// <summary>
    /// Executes the full cleanup: stop agents → clean repo → reset state → restart agents.
    /// </summary>
    public async Task<CleanupResult> ExecuteCleanupAsync(
        string? caveats, CancellationToken ct = default)
    {
        var result = new CleanupResult();
        var config = _config.CurrentValue.Project;

        try
        {
            // ── Phase 1: Stop all running agents ─────────────────────
            _logger.LogWarning("CLEANUP Phase 1/4: Stopping all agents...");
            result.Phase = "Stopping agents";
            var agents = _registry.GetAllAgents();
            foreach (var agent in agents)
            {
                try
                {
                    await _registry.UnregisterAsync(agent.Identity.Id, ct);
                    result.AgentsStopped++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to stop agent {AgentId}", agent.Identity.Id);
                }
            }
            _logger.LogInformation("Stopped {Count} agents", result.AgentsStopped);
            await Task.Delay(500, ct); // brief pause for cleanup

            // ── Phase 2: Clean GitHub repository ─────────────────────
            _logger.LogWarning("CLEANUP Phase 2/4: Cleaning GitHub repository...");
            result.Phase = "Cleaning repository";

            // Parse caveats to find files to preserve
            var preserveFiles = ParseCaveats(caveats);

            // 2a. Delete ALL issues (open + closed) — with verify-and-retry
            _logger.LogWarning("CLEANUP: Deleting all issues in {Repo}", config.GitHubRepo);
            const int maxCleanupRetries = 3;
            for (var attempt = 1; attempt <= maxCleanupRetries; attempt++)
            {
                var allIssues = await _github.GetAllIssuesAsync(ct);
                if (allIssues.Count == 0) break;

                foreach (var issue in allIssues)
                {
                    try
                    {
                        var deleted = await _github.DeleteIssueAsync(issue.Number, ct);
                        if (deleted)
                            result.IssuesDeleted++;
                        else
                            result.IssuesClosed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete issue #{Number}", issue.Number);
                        result.Errors.Add($"Failed to delete issue #{issue.Number}: {ex.Message}");
                    }
                }

                // Verify: re-check for any remaining open issues
                await Task.Delay(2000, ct);
                var remaining = await _github.GetAllIssuesAsync(ct);
                var openRemaining = remaining.Where(i => i.State == "open").ToList();
                if (openRemaining.Count == 0)
                {
                    _logger.LogInformation("Issue cleanup verified — 0 open issues remain");
                    break;
                }
                _logger.LogWarning("Issue cleanup attempt {Attempt}/{Max}: {Count} open issues still remain, retrying...",
                    attempt, maxCleanupRetries, openRemaining.Count);
            }

            // 2b. Close all open PRs (with verify-and-retry) and label merged PRs
            _logger.LogWarning("CLEANUP: Closing open PRs and labeling merged PRs in {Repo}", config.GitHubRepo);
            for (var attempt = 1; attempt <= maxCleanupRetries; attempt++)
            {
                var allPrs = await _github.GetAllPullRequestsAsync(ct);
                var openPrs = allPrs.Where(p => p.State == "open" && !p.IsMerged).ToList();
                if (openPrs.Count == 0 && attempt > 1) break; // skip label pass on retries

                foreach (var pr in allPrs)
                {
                    try
                    {
                        if (pr.State == "open" && !pr.IsMerged)
                        {
                            await _github.ClosePullRequestAsync(pr.Number, ct);
                            result.PrsClosed++;
                        }

                        if (!pr.Labels.Contains("tested", StringComparer.OrdinalIgnoreCase))
                        {
                            var updatedLabels = pr.Labels
                                .Append("tested")
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                            await _github.UpdatePullRequestAsync(pr.Number, labels: updatedLabels, ct: ct);
                            result.PrsLabeled++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process PR #{Number}", pr.Number);
                        result.Errors.Add($"Failed to process PR #{pr.Number}: {ex.Message}");
                    }
                }

                // Verify: re-check for any remaining open PRs
                await Task.Delay(2000, ct);
                var remainingPrs = await _github.GetAllPullRequestsAsync(ct);
                var stillOpen = remainingPrs.Where(p => p.State == "open" && !p.IsMerged).ToList();
                if (stillOpen.Count == 0)
                {
                    _logger.LogInformation("PR cleanup verified — 0 open PRs remain");
                    break;
                }
                _logger.LogWarning("PR cleanup attempt {Attempt}/{Max}: {Count} open PRs still remain, retrying...",
                    attempt, maxCleanupRetries, stillOpen.Count);
            }

            // 2c. Delete ALL agent branches (with verify-and-retry)
            _logger.LogWarning("CLEANUP: Deleting agent branches in {Repo}", config.GitHubRepo);
            for (var attempt = 1; attempt <= maxCleanupRetries; attempt++)
            {
                var allAgentBranches = await _github.ListBranchesAsync("agent/", ct);
                if (allAgentBranches.Count == 0) break;

                foreach (var branch in allAgentBranches)
                {
                    try
                    {
                        await _github.DeleteBranchAsync(branch, ct);
                        result.BranchesDeleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete branch {Branch}", branch);
                    }
                }

                // Verify
                await Task.Delay(1000, ct);
                var remainingBranches = await _github.ListBranchesAsync("agent/", ct);
                if (remainingBranches.Count == 0)
                {
                    _logger.LogInformation("Branch cleanup verified — 0 agent branches remain");
                    break;
                }
                _logger.LogWarning("Branch cleanup attempt {Attempt}/{Max}: {Count} agent branches still remain, retrying...",
                    attempt, maxCleanupRetries, remainingBranches.Count);
            }

            // 2d. Atomically reset repo to baseline via Git Trees API (~4 API calls total)
            _logger.LogWarning("CLEANUP: Resetting repo to baseline files in {Repo}", config.GitHubRepo);
            var files = await _github.GetRepositoryTreeAsync(config.DefaultBranch, ct);

            List<string> filesToKeep;
            if (!string.IsNullOrWhiteSpace(config.BaselineCommitSha))
            {
                _logger.LogInformation("Using BaselineCommitSha {Sha} for repo reset", config.BaselineCommitSha[..Math.Min(8, config.BaselineCommitSha.Length)]);
                filesToKeep = (await _github.GetRepositoryTreeForCommitAsync(config.BaselineCommitSha, ct)).ToList();
            }
            else
            {
                filesToKeep = files.Where(f => IsFilePreserved(f, preserveFiles)).ToList();
            }

            // Always preserve .gitignore
            if (!filesToKeep.Any(f => f.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)))
            {
                if (files.Any(f => f.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)))
                    filesToKeep.Add(".gitignore");
            }

            try
            {
                await _github.CleanRepoToBaselineAsync(filesToKeep, "Clean slate reset via Dashboard", config.DefaultBranch, ct);
                result.FilesDeleted = files.Count - filesToKeep.Count;
                result.FilesPreserved = filesToKeep.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Atomic repo clean failed");
                result.Errors.Add($"Repo file cleanup failed: {ex.Message}");
            }

            // ── Phase 3: Reset internal state ────────────────────────
            _logger.LogWarning("CLEANUP Phase 3/4: Resetting workflow state...");
            result.Phase = "Resetting state";

            // Reset workflow to Initialization phase, clear all signals and checkpoints
            await _workflow.ResetAsync(ct);

            // Reset spawn slot counters so agents can be re-spawned
            _spawnManager.ResetSlots();

            // Reset dashboard caches
            _dashboard.ResetCaches();

            // Delete SQLite DB files (checkpoint/state persistence)
            try
            {
                var runnerDir = Path.GetDirectoryName(_appSettingsPath) ?? ".";
                var dbFiles = Directory.GetFiles(runnerDir, "agentsquad_*.db*");
                foreach (var db in dbFiles)
                {
                    File.Delete(db);
                    _logger.LogInformation("Deleted DB file: {File}", Path.GetFileName(db));
                }
                result.DbFilesDeleted = dbFiles.Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete DB files");
                result.Errors.Add($"DB cleanup failed: {ex.Message}");
            }

            // Clean agent workspace directories
            var workspaceRoot = _config.CurrentValue.Workspace.RootPath;
            if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
            {
                try
                {
                    var dirs = Directory.GetDirectories(workspaceRoot);
                    foreach (var dir in dirs)
                    {
                        Directory.Delete(dir, recursive: true);
                        result.WorkspaceDirsDeleted++;
                    }
                    _logger.LogInformation("Cleaned {Count} workspace directories from {Root}",
                        dirs.Length, workspaceRoot);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean workspace directories at {Root}", workspaceRoot);
                    result.Errors.Add($"Workspace cleanup failed: {ex.Message}");
                }
            }

            _logger.LogInformation("Internal state reset to Initialization");

            // ── Phase 4: Respawn core agents ─────────────────────────
            _logger.LogWarning("CLEANUP Phase 4/4: Spawning fresh agents...");
            result.Phase = "Starting agents";

            var roles = new[]
            {
                AgentRole.ProgramManager,
                AgentRole.Researcher,
                AgentRole.Architect,
                AgentRole.PrincipalEngineer,
                AgentRole.TestEngineer
            };

            foreach (var role in roles)
            {
                try
                {
                    var identity = await _spawnManager.SpawnAgentAsync(role, ct);
                    if (identity is not null)
                    {
                        result.AgentsStarted++;
                        _logger.LogInformation("Spawned {Role}: {Name}", role, identity.DisplayName);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to spawn {Role} — returned null", role);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to spawn {Role} agent during restart", role);
                    result.Errors.Add($"Failed to spawn {role}: {ex.Message}");
                }
            }

            result.Phase = "Complete";
            result.Success = true;
            _logger.LogWarning(
                "✅ CLEANUP COMPLETE: {Issues} issues deleted, {Prs} PRs closed, " +
                "{Branches} branches deleted, {Files} files deleted, {Db} DB files deleted, " +
                "{Workspaces} workspaces cleaned, {Stopped} agents stopped, {Started} agents restarted",
                result.IssuesDeleted, result.PrsClosed, result.BranchesDeleted,
                result.FilesDeleted, result.DbFilesDeleted, result.WorkspaceDirsDeleted,
                result.AgentsStopped, result.AgentsStarted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup failed");
            result.Errors.Add($"Cleanup failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Parses caveat text to extract file preservation patterns.
    /// Looks for patterns like: "Leave X", "Keep X", "Preserve X", "Don't delete X"
    /// </summary>
    private static HashSet<string> ParseCaveats(string? caveats)
    {
        var preserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(caveats)) return preserved;

        // Extract quoted strings and file-like tokens
        var words = caveats.Split(new[] { ' ', ',', ';', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var word in words)
        {
            // Match file-like patterns (has extension or path separator)
            var clean = word.Trim('\"', '\'', '`', '(', ')');
            if (clean.Contains('.') || clean.Contains('/') || clean.Contains('\\'))
            {
                preserved.Add(clean);
            }
        }

        return preserved;
    }

    /// <summary>Checks if a file matches any preservation pattern.</summary>
    private static bool IsFilePreserved(string filePath, HashSet<string> preservePatterns)
    {
        if (preservePatterns.Count == 0) return false;

        foreach (var pattern in preservePatterns)
        {
            // Exact match
            if (filePath.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;

            // Filename match (e.g., "Design.html" matches "src/Design.html")
            var fileName = Path.GetFileName(filePath);
            if (fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;

            // Contains match for partial paths
            if (filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;

            // Wildcard extension match (e.g., "*.html")
            if (pattern.StartsWith("*.") &&
                filePath.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>Summary of what would be affected by a repo cleanup.</summary>
public sealed class CleanupSummary
{
    public string RepoFullName { get; set; } = "";
    public int OpenIssues { get; set; }
    public int ClosedIssues { get; set; }
    public int OpenPrs { get; set; }
    public int MergedPrs { get; set; }
    public int ClosedPrs { get; set; }
    public int FileCount { get; set; }
    public List<string> Files { get; set; } = new();
    public string? Error { get; set; }

    public int TotalIssues => OpenIssues + ClosedIssues;
    public int TotalPrs => OpenPrs + MergedPrs + ClosedPrs;
}

/// <summary>Result of a repo cleanup operation.</summary>
public sealed class CleanupResult
{
    public bool Success { get; set; }
    public string Phase { get; set; } = "Pending";
    public int IssuesDeleted { get; set; }
    public int IssuesClosed { get; set; }
    public int PrsClosed { get; set; }
    public int PrsLabeled { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesPreserved { get; set; }
    public int BranchesDeleted { get; set; }
    public int DbFilesDeleted { get; set; }
    public int WorkspaceDirsDeleted { get; set; }
    public int AgentsStopped { get; set; }
    public int AgentsStarted { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>Result of PAT token validation.</summary>
public sealed class PatValidationResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? RepoName { get; set; }
    public string? RepoDescription { get; set; }
    public bool IsPrivate { get; set; }
    public string? DefaultBranch { get; set; }
    public string? AuthenticatedUser { get; set; }
    public List<string> Permissions { get; set; } = new();
}

