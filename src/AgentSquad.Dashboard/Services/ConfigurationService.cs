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

    private readonly IWebHostEnvironment _env;

    /// <summary>Serializes save operations so concurrent requests don't overlap file I/O with the file-watcher's auto-reload.</summary>
    private readonly SemaphoreSlim _saveLock = new(1, 1);

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
        _env = env;
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
    /// non-AgentSquad sections (e.g., Logging). Strips secrets (PAT, API keys)
    /// so they are never written to the committed config file.
    /// </summary>
    public async Task SaveConfigAsync(AgentSquadConfig updatedConfig)
    {
        await _saveLock.WaitAsync();
        try
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            var step = System.Diagnostics.Stopwatch.StartNew();

            var root = await ReadAppSettingsAsync() ?? new JsonObject();
            _logger.LogInformation("SaveConfig step[read]: {Ms}ms", step.ElapsedMilliseconds); step.Restart();

            // Serialize the updated config section
            var configJson = JsonSerializer.SerializeToNode(updatedConfig, JsonOptions);

            // Persist secrets to User Secrets before stripping them from the JSON.
            // This ensures that PAT/API keys entered in the UI survive app restarts.
            await PersistSecretsToUserSecretsAsync(updatedConfig);
            _logger.LogInformation("SaveConfig step[user-secrets]: {Ms}ms", step.ElapsedMilliseconds); step.Restart();

            // Strip secrets — these live in User Secrets / env vars, not appsettings.json
            StripSecrets(configJson);

            root["AgentSquad"] = configJson;

            var output = root.ToJsonString(JsonOptions);
            _logger.LogInformation("SaveConfig step[serialize]: {Ms}ms ({Bytes} bytes)", step.ElapsedMilliseconds, output.Length); step.Restart();

            // Write to a temp file then move — avoids "user-mapped section open" IOException
            // on Windows where ASP.NET's file watcher memory-maps appsettings.json.
            var tempPath = _appSettingsPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, output);
            _logger.LogInformation("SaveConfig step[write-temp]: {Ms}ms", step.ElapsedMilliseconds); step.Restart();

            File.Move(tempPath, _appSettingsPath, overwrite: true);
            _logger.LogInformation("SaveConfig step[move]: {Ms}ms", step.ElapsedMilliseconds); step.Restart();

            // Cache the saved config so GetCurrentConfig returns it immediately.
            // We intentionally do NOT call configRoot.Reload() here — that operation
            // synchronously re-binds every IOptionsMonitor<T> consumer and can take
            // seconds (degrading with each call in long-running processes).
            // The file watcher (reloadOnChange:true) will reload providers in the
            // background, and _lastSavedConfig gives immediate consistency for our
            // own reads in the meantime.
            _lastSavedConfig = updatedConfig;

            _logger.LogInformation("Configuration saved to {Path} (total {Ms}ms)", _appSettingsPath, total.ElapsedMilliseconds);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Removes secret values from a serialized config JSON node so they are not
    /// persisted to appsettings.json. Secrets should be stored in User Secrets
    /// or environment variables instead.
    /// </summary>
    private static void StripSecrets(JsonNode? configNode)
    {
        if (configNode is not JsonObject config) return;

        // Strip GitHubToken from Project section
        if (config["Project"] is JsonObject project && project.ContainsKey("GitHubToken"))
            project["GitHubToken"] = "";

        // Strip ApiKey from each model tier
        if (config["Models"] is JsonObject models)
        {
            foreach (var (_, tierNode) in models)
            {
                if (tierNode is JsonObject tier && tier.ContainsKey("ApiKey"))
                    tier["ApiKey"] = "";
            }
        }

        // Strip Env secrets from MCP server configs
        if (config["McpServers"] is JsonObject mcpServers)
        {
            foreach (var (_, serverNode) in mcpServers)
            {
                if (serverNode is not JsonObject server) continue;
                if (server["Env"] is not JsonObject env) continue;
                foreach (var (key, _) in env)
                {
                    if (key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("KEY", StringComparison.OrdinalIgnoreCase))
                    {
                        env[key] = "";
                    }
                }
            }
        }

        // Strip ADO secrets from DevPlatform section
        if (config["DevPlatform"] is JsonObject devPlatform &&
            devPlatform["AzureDevOps"] is JsonObject azureDevOps)
        {
            if (azureDevOps.ContainsKey("Pat"))
                azureDevOps["Pat"] = "";
            if (azureDevOps.ContainsKey("TenantId"))
                azureDevOps["TenantId"] = "";
        }
    }

    /// <summary>
    /// Persists secret values from the config to .NET User Secrets for both the
    /// Dashboard and Runner projects, so secrets survive app restarts without
    /// ever being written to the tracked appsettings.json file.
    /// Throws on failure so the caller can abort the save rather than lose secrets.
    /// </summary>
    private async Task PersistSecretsToUserSecretsAsync(AgentSquadConfig config)
    {
        var secrets = CollectSecrets(config);
        if (secrets.Count == 0) return;

        // Discover UserSecretsIds from both project csproj files
        var dashboardDir = _env.ContentRootPath;
        var runnerDir = Path.GetFullPath(Path.Combine(dashboardDir, "..", "AgentSquad.Runner"));

        var secretsIds = new HashSet<string>();
        foreach (var dir in new[] { dashboardDir, runnerDir })
        {
            var id = ReadUserSecretsIdFromCsproj(dir);
            if (id is not null) secretsIds.Add(id);
        }

        if (secretsIds.Count == 0)
        {
            _logger.LogWarning("No UserSecretsId found in project files — secrets will not be persisted");
            return;
        }

        // Write to all discovered stores — let exceptions propagate to abort the save
        foreach (var secretsId in secretsIds)
            await MergeUserSecretsAsync(secretsId, secrets);

        _logger.LogInformation("Persisted {Count} secret(s) to {Stores} User Secrets store(s)",
            secrets.Count, secretsIds.Count);
    }

    /// <summary>
    /// Collects all non-empty secret values from the config into a flat key-value map.
    /// </summary>
    private static Dictionary<string, string> CollectSecrets(AgentSquadConfig config)
    {
        var secrets = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(config.Project.GitHubToken))
            secrets["AgentSquad:Project:GitHubToken"] = config.Project.GitHubToken;

        if (!string.IsNullOrEmpty(config.DevPlatform?.AzureDevOps?.Pat))
            secrets["AgentSquad:DevPlatform:AzureDevOps:Pat"] = config.DevPlatform!.AzureDevOps!.Pat;

        if (!string.IsNullOrEmpty(config.DevPlatform?.AzureDevOps?.TenantId))
            secrets["AgentSquad:DevPlatform:AzureDevOps:TenantId"] = config.DevPlatform!.AzureDevOps!.TenantId;

        if (config.Models is not null)
        {
            foreach (var (tier, model) in config.Models)
            {
                if (!string.IsNullOrEmpty(model.ApiKey))
                    secrets[$"AgentSquad:Models:{tier}:ApiKey"] = model.ApiKey;
            }
        }

        // MCP server env vars that contain TOKEN/SECRET/KEY
        if (config.McpServers is not null)
        {
            foreach (var (serverName, server) in config.McpServers)
            {
                if (server.Env is null) continue;
                foreach (var (key, value) in server.Env)
                {
                    if (string.IsNullOrEmpty(value)) continue;
                    if (key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("KEY", StringComparison.OrdinalIgnoreCase))
                    {
                        secrets[$"AgentSquad:McpServers:{serverName}:Env:{key}"] = value;
                    }
                }
            }
        }

        return secrets;
    }

    /// <summary>
    /// Reads the UserSecretsId from the first .csproj file in the given directory.
    /// </summary>
    private static string? ReadUserSecretsIdFromCsproj(string projectDir)
    {
        if (!Directory.Exists(projectDir)) return null;

        var csproj = Directory.EnumerateFiles(projectDir, "*.csproj").FirstOrDefault();
        if (csproj is null) return null;

        var content = File.ReadAllText(csproj);
        var startTag = "<UserSecretsId>";
        var endTag = "</UserSecretsId>";
        var start = content.IndexOf(startTag, StringComparison.Ordinal);
        if (start < 0) return null;
        start += startTag.Length;
        var end = content.IndexOf(endTag, start, StringComparison.Ordinal);
        if (end < 0) return null;

        return content[start..end].Trim();
    }

    /// <summary>
    /// Merges secret key-value pairs into the User Secrets JSON file for the given secrets ID.
    /// Uses atomic temp-file-then-move writes to avoid corruption.
    /// Uses JsonNode to safely handle both flat and nested JSON formats.
    /// </summary>
    private static async Task MergeUserSecretsAsync(string secretsId, Dictionary<string, string> secrets)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var secretsDir = Path.Combine(appData, "Microsoft", "UserSecrets", secretsId);
        var secretsPath = Path.Combine(secretsDir, "secrets.json");

        // Read existing secrets using JsonNode (handles both flat and nested formats safely)
        JsonObject existing;
        if (File.Exists(secretsPath))
        {
            var json = await File.ReadAllTextAsync(secretsPath);
            existing = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        else
        {
            existing = new JsonObject();
        }

        // Merge new secrets (flat key:value format, which is what dotnet user-secrets produces)
        foreach (var (key, value) in secrets)
            existing[key] = value;

        // Atomic write: temp file then move
        Directory.CreateDirectory(secretsDir);
        var output = existing.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tempPath = secretsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, output);
        File.Move(tempPath, secretsPath, overwrite: true);
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

            // ⚠️ CRITICAL: Delete persisted SME agent definitions so stale specialists don't auto-respawn
            try
            {
                var runnerDir = Path.GetDirectoryName(_appSettingsPath) ?? ".";
                var smeFiles = Directory.GetFiles(runnerDir, "sme-definitions*");
                foreach (var f in smeFiles)
                {
                    File.Delete(f);
                    _logger.LogInformation("Deleted SME definitions file: {File}", Path.GetFileName(f));
                }
                // Also check bin output directory
                var binDir = Path.Combine(runnerDir, "bin");
                if (Directory.Exists(binDir))
                {
                    foreach (var f in Directory.GetFiles(binDir, "sme-definitions*", SearchOption.AllDirectories))
                    {
                        File.Delete(f);
                        _logger.LogInformation("Deleted SME definitions file: {File}", f);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete SME definition files");
                result.Errors.Add($"SME definitions cleanup failed: {ex.Message}");
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

            // No Phase 4: agents are NOT auto-respawned after reset.
            // The user must click "Start Project" to begin a new run.

            result.Phase = "Complete";
            result.Success = true;
            _logger.LogWarning(
                "✅ CLEANUP COMPLETE: {Issues} issues deleted, {Prs} PRs closed, " +
                "{Branches} branches deleted, {Files} files deleted, {Db} DB files deleted, " +
                "{Workspaces} workspaces cleaned, {Stopped} agents stopped. " +
                "Click 'Start Project' to begin a new run.",
                result.IssuesDeleted, result.PrsClosed, result.BranchesDeleted,
                result.FilesDeleted, result.DbFilesDeleted, result.WorkspaceDirsDeleted,
                result.AgentsStopped);
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

