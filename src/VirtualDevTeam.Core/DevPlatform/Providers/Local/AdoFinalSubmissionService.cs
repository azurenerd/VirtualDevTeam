using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Config;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.DevPlatform.Providers.AzureDevOps;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// Publishes the final integration result from Local Dev Mode to Azure DevOps.
/// Creates one clean PR with all agent work merged, targeting the user's configured branch.
/// The PR is NOT merged — a human reviews and merges it.
/// Persists submission state to the local DB for idempotency across restarts.
/// </summary>
/// <remarks>
/// Parallel to <see cref="GitHubFinalSubmissionService"/> which handles Local → GitHub.
/// Uses ADO REST API directly (no <c>az</c> CLI dependency) with self-contained auth
/// resolution since the DI-registered <see cref="IDevPlatformAuthProvider"/> is an empty
/// <c>PatAuthProvider("")</c> in Local mode.
/// </remarks>
public sealed class AdoFinalSubmissionService : IFinalSubmissionService, IDisposable
{
    private readonly LocalPlatformContext _ctx;
    private readonly IOptions<VirtualDevTeamConfig> _config;
    private readonly ILogger<AdoFinalSubmissionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private AzureCliBearerProvider? _bearerProvider;
    private bool _disposed;

    // ADO PR description limit (same as AdoPullRequestService)
    private const int MaxDescriptionLength = 4000;
    private const string OverflowMarker = "<!-- overflow-body: true -->";
    private const int OverflowDescriptionBudget = MaxDescriptionLength - 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public AdoFinalSubmissionService(
        LocalPlatformContext ctx,
        IOptions<VirtualDevTeamConfig> config,
        ILoggerFactory loggerFactory,
        ILogger<AdoFinalSubmissionService> logger)
    {
        _ctx = ctx;
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _http = new HttpClient();
    }

    private AzureDevOpsConfig Ado => _config.Value.DevPlatform?.AzureDevOps
        ?? throw new InvalidOperationException("AzureDevOps config is required for ADO final submission");

    public async Task<PlatformPullRequest> SubmitFinalPRAsync(
        string branchName, string title, string body, string baseBranch,
        CancellationToken ct = default)
    {
        // Idempotency: check for existing submission
        var existing = await GetExistingSubmissionAsync(ct);
        if (existing is not null)
        {
            _logger.LogInformation("Final ADO PR already submitted as #{Number} — reusing", existing.Number);
            return existing;
        }

        // Defensive: head branch must differ from base branch (ADO rejects head==base PRs)
        if (string.Equals(branchName, baseBranch, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = $"vdt/final/{baseBranch}";
            _logger.LogWarning(
                "Final submission branch {Branch} equals base {Base} — using fallback {Fallback}",
                branchName, baseBranch, fallback);
            branchName = fallback;
        }

        var ado = Ado;
        if (string.IsNullOrWhiteSpace(ado.Organization) || string.IsNullOrWhiteSpace(ado.Project)
            || string.IsNullOrWhiteSpace(ado.Repository))
        {
            throw new InvalidOperationException(
                "ADO org/project/repository not configured — cannot submit final PR to Azure DevOps");
        }

        // Step 1: Push the integration branch to ADO
        var token = await ResolveTokenAsync(ct);
        _logger.LogInformation("Pushing branch {Branch} to ADO remote {Org}/{Project}/{Repo}",
            branchName, ado.Organization, ado.Project, ado.Repository);
        await PushBranchToAdoAsync(branchName, token, ct);

        // Step 2: Ensure the target branch exists on ADO
        var defaultBranch = ado.DefaultBranch ?? "main";
        await EnsureRemoteBranchExistsAsync(baseBranch, defaultBranch, token, ct);

        // Step 3: Check for existing active PR (idempotency for "PR created but persist failed")
        var existingPrId = await FindExistingPrAsync(branchName, baseBranch, token, ct);
        int prNumber;
        if (existingPrId > 0)
        {
            prNumber = existingPrId;
            _logger.LogInformation("Found existing ADO PR #{Id} for {Branch} — reusing", prNumber, branchName);
        }
        else
        {
            // Step 4: Create the PR via ADO REST API
            _logger.LogInformation("Creating final PR on ADO: {Title} (base: {Base})", title, baseBranch);
            prNumber = await CreatePRViaRestAsync(branchName, baseBranch, title, body, token, ct);
        }

        // Step 5: Persist submission state
        await PersistSubmissionAsync(prNumber, branchName, title, ct);

        var webUrl = $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_git/{ado.Repository}/pullrequest/{prNumber}";
        var pr = new PlatformPullRequest
        {
            Number = prNumber,
            Title = title,
            Body = body,
            State = "open",
            HeadBranch = branchName,
            BaseBranch = baseBranch,
            Url = webUrl,
            Labels = new List<string> { "final-integration", "awaiting-human-review" },
        };

        _logger.LogInformation("✅ Final PR #{Number} created on Azure DevOps: {Url}", prNumber, webUrl);
        return pr;
    }

    public async Task<PlatformPullRequest?> GetExistingSubmissionAsync(CancellationToken ct = default)
    {
        using var conn = _ctx.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pr_number, branch_name, title, submitted_at
            FROM local_final_submissions WHERE run_id = @runId
            ORDER BY submitted_at DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);

        try
        {
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            var ado = Ado;
            var prNumber = reader.GetInt32(0);
            return new PlatformPullRequest
            {
                Number = prNumber,
                Title = reader.GetString(2),
                State = "open",
                HeadBranch = reader.GetString(1),
                Url = $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_git/{ado.Repository}/pullrequest/{prNumber}",
            };
        }
        catch
        {
            // Table may not exist yet
            return null;
        }
    }

    #region Auth Resolution

    /// <summary>
    /// Resolves an auth token for ADO. In Local mode, the DI-registered auth provider
    /// is an empty PatAuthProvider, so we resolve auth ourselves from config.
    /// Priority: 1) Pre-supplied BearerToken, 2) PAT from config, 3) AzureCliBearerProvider
    /// </summary>
    private async Task<string> ResolveTokenAsync(CancellationToken ct)
    {
        var ado = Ado;
        var authMethod = _config.Value.DevPlatform?.AuthMethod ?? DevPlatformAuthMethod.Pat;

        // 1. Pre-supplied bearer token (short-lived, for testing)
        if (!string.IsNullOrWhiteSpace(ado.BearerToken))
        {
            _logger.LogDebug("Using pre-supplied ADO bearer token");
            return ado.BearerToken.Trim();
        }

        // 2. PAT from config/user-secrets
        if (authMethod == DevPlatformAuthMethod.Pat && !string.IsNullOrWhiteSpace(ado.Pat))
        {
            _logger.LogDebug("Using ADO PAT from config");
            return ado.Pat.Trim();
        }

        // 3. Azure CLI bearer token (auto-refresh)
        if (authMethod == DevPlatformAuthMethod.AzureCliBearer || string.IsNullOrWhiteSpace(ado.Pat))
        {
            await _authLock.WaitAsync(ct);
            try
            {
                _bearerProvider ??= new AzureCliBearerProvider(
                    _loggerFactory.CreateLogger<AzureCliBearerProvider>(),
                    ado.TenantId);

                var token = await _bearerProvider.GetTokenAsync(ct);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    _logger.LogDebug("Using Azure CLI bearer token for ADO");
                    return token;
                }
            }
            finally
            {
                _authLock.Release();
            }
        }

        throw new InvalidOperationException(
            "No ADO authentication available. Configure a PAT via user secrets " +
            "(dotnet user-secrets set \"VirtualDevTeam:DevPlatform:AzureDevOps:Pat\" \"<token>\"), " +
            "a bearer token, or ensure 'az login' is done for AzureCliBearer auth.");
    }

    /// <summary>Returns the auth scheme ("Basic" for PAT, "Bearer" for CLI tokens).</summary>
    private string GetAuthScheme()
    {
        var ado = Ado;
        var authMethod = _config.Value.DevPlatform?.AuthMethod ?? DevPlatformAuthMethod.Pat;

        if (!string.IsNullOrWhiteSpace(ado.BearerToken))
            return "Bearer";
        if (authMethod == DevPlatformAuthMethod.AzureCliBearer)
            return "Bearer";
        return "Basic";
    }

    /// <summary>Formats the auth header value for the given token and scheme.</summary>
    private string FormatAuthHeaderValue(string token)
    {
        var scheme = GetAuthScheme();
        if (scheme == "Basic")
        {
            // ADO PAT uses Basic auth with empty username: ":token" base64-encoded
            return Convert.ToBase64String(Encoding.UTF8.GetBytes($":{token}"));
        }
        return token; // Bearer — use token directly
    }

    #endregion

    #region Git Push

    private async Task PushBranchToAdoAsync(string branchName, string token, CancellationToken ct)
    {
        var cfg = _config.Value;
        var inPlaceCheckout = cfg.Workspace.LocalCheckoutPath;

        if (!string.IsNullOrWhiteSpace(inPlaceCheckout) && Directory.Exists(Path.Combine(inPlaceCheckout, ".git")))
        {
            await MergeInTempWorktreeAndPushAsync(inPlaceCheckout, branchName, token, ct);
        }
        else
        {
            if (_ctx.BareRepo.BareRepoPath is null)
                throw new InvalidOperationException("Local bare repo not initialized");
            await PushFromBareRepoAsync(branchName, token, ct);
        }
    }

    private async Task MergeInTempWorktreeAndPushAsync(
        string inPlaceCheckout, string branchName,
        string token, CancellationToken ct)
    {
        var ado = Ado;
        var defaultBranch = ado.DefaultBranch ?? "main";
        var remoteUrl = GetAdoCloneUrl(token);

        // branchName = the working branch (e.g., "behumphr") that receives merged agent code.
        _logger.LogInformation("Final submission: fetching latest from ADO origin in {Path}", inPlaceCheckout);
        var startRef = branchName;
        try
        {
            await RunGitInDirAsync(inPlaceCheckout, $"fetch --prune origin {branchName}", token, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("couldn't find remote ref"))
        {
            _logger.LogWarning("Working branch '{Branch}' not found on remote — starting from '{Default}'",
                branchName, defaultBranch);
            await RunGitInDirAsync(inPlaceCheckout, $"fetch --prune origin {defaultBranch}", token, ct);
            startRef = defaultBranch;
        }

        // Step 2: Add local bare repo as a remote
        var bareRepoPath = _ctx.BareRepo.BareRepoPath;
        if (bareRepoPath is not null)
        {
            try { await RunGitInDirAsync(inPlaceCheckout, "remote remove vdt-local", null, ct); }
            catch { }
            await RunGitInDirAsync(inPlaceCheckout, $"remote add vdt-local \"{bareRepoPath}\"", null, ct);
            await RunGitInDirAsync(inPlaceCheckout, "fetch vdt-local", null, ct);
        }

        // Step 3: Create temp DETACHED worktree from origin/{startRef}. We use --detach (not
        // -b <branch>) deliberately — the push below uses `HEAD:refs/heads/{branch}`, so no named
        // local branch is needed. Creating one only leaks a `vdt-temp-final-*` ref into the
        // operator's checkout that worktree-remove never deleted. (Parity with
        // GitHubFinalSubmissionService.)
        var tempDir = Path.Combine(Path.GetTempPath(), $"vdt-final-ado-{Guid.NewGuid():N}");
        var pushSucceeded = false;
        try
        {
            _logger.LogInformation("Final submission: creating temp worktree at {Path} from origin/{Base}",
                tempDir, startRef);
            await RunGitInDirAsync(inPlaceCheckout,
                $"worktree add --detach \"{tempDir}\" origin/{startRef}", null, ct);

            try
            {
                await RunGitInDirAsync(tempDir, "config user.name \"VirtualDevTeam\"", null, ct);
                await RunGitInDirAsync(tempDir, "config user.email \"virtualdevteam@noreply.github.com\"", null, ct);
            }
            catch { }

            // Step 4: Merge local changes — try working branch first (where PRs merge
            // in Local mode), then defaultBranch, fall back to individual branches
            bool mergedViaMainBranch = false;
            if (bareRepoPath is not null)
            {
                var mergedPrCount = GetMergedPrBranches().Count;
                if (mergedPrCount > 0)
                {
                    var candidates = new List<string> { branchName };
                    if (!string.Equals(defaultBranch, branchName, StringComparison.OrdinalIgnoreCase))
                        candidates.Add(defaultBranch);

                    foreach (var candidate in candidates)
                    {
                        if (mergedViaMainBranch) break;
                        try
                        {
                            await RunGitInDirAsync(tempDir, $"merge vdt-local/{candidate} --no-edit --allow-unrelated-histories", null, ct);
                            var diffStat = await RunGitCaptureInDirAsync(tempDir,
                                $"diff --stat origin/{startRef}..HEAD", ct);
                            if (!string.IsNullOrWhiteSpace(diffStat))
                            {
                                _logger.LogInformation(
                                    "Final submission: merged vdt-local/{Branch} — all {Count} local PRs included",
                                    candidate, mergedPrCount);
                                mergedViaMainBranch = true;
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Merge of vdt-local/{Branch} produced no diff — trying next candidate",
                                    candidate);
                                try { await RunGitInDirAsync(tempDir, "reset --hard HEAD~1", null, ct); }
                                catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to merge vdt-local/{Branch} — trying next candidate",
                                candidate);
                            try { await RunGitInDirAsync(tempDir, "merge --abort", null, ct); }
                            catch { try { await RunGitInDirAsync(tempDir, "reset --merge", null, ct); } catch { } }
                        }
                    }
                }
            }

            if (!mergedViaMainBranch)
            {
                await MergeIndividualBranchesAsync(inPlaceCheckout, tempDir, branchName, bareRepoPath, ct);
            }

            // Step 5: Push to ADO remote as the working branch
            _logger.LogInformation("Final submission: pushing to working branch {Branch} on ADO", branchName);
            await PushFromDirAsync(tempDir, branchName, remoteUrl, token, ct);
            pushSucceeded = true;

            _logger.LogInformation("✅ Working branch {Branch} pushed to ADO {Org}/{Project}/{Repo}",
                branchName, ado.Organization, ado.Project, ado.Repository);
        }
        finally
        {
            // Only remove the worktree once the push succeeded — with a detached HEAD the merge
            // commit lives solely in this worktree until pushed; removing after a push failure
            // would discard it. On failure, keep the worktree and log its path for recovery.
            if (!pushSucceeded)
            {
                _logger.LogWarning(
                    "Final submission push did not complete — preserving temp worktree for recovery at {Path}. " +
                    "Remove it manually (git worktree remove --force) once resolved.", tempDir);
            }
            else
            {
                try
                {
                    await RunGitInDirAsync(inPlaceCheckout, $"worktree remove \"{tempDir}\" --force", null, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temp worktree at {Path}", tempDir);
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        // Best-effort: prune any LEGACY vdt-temp-final-* branches left by pre---detach versions.
        // Runs only after a successful push, so origin/{branchName} reflects the pushed work.
        await CleanupLegacyTempBranchesAsync(inPlaceCheckout, branchName, token, ct);
    }

    /// <summary>
    /// Best-effort cleanup of LEGACY <c>vdt-temp-final-*</c> branches left in the operator's
    /// checkout by pre-<c>--detach</c> versions of this service. Parity with
    /// <c>GitHubFinalSubmissionService.CleanupLegacyTempBranchesAsync</c>: deletes a branch ONLY
    /// when it is reachable from the just-pushed final branch (<c>origin/{branchName}</c>), so its
    /// commits are preserved on the remote. Never throws.
    /// </summary>
    private async Task CleanupLegacyTempBranchesAsync(
        string inPlaceCheckout, string branchName, string token, CancellationToken ct)
    {
        try
        {
            try { await RunGitInDirAsync(inPlaceCheckout, "worktree prune", null, ct); } catch { /* best-effort */ }

            try { await RunGitInDirAsync(inPlaceCheckout, $"fetch --prune origin {branchName}", token, ct); }
            catch { return; /* can't verify reachability without a current ref — keep branches */ }

            var listing = await RunGitCaptureInDirAsync(inPlaceCheckout,
                "for-each-ref --format=%(refname:short) refs/heads/vdt-temp-final-*", ct);
            var branches = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var b in branches)
            {
                ct.ThrowIfCancellationRequested();

                bool reachable;
                try
                {
                    await RunGitInDirAsync(inPlaceCheckout,
                        $"merge-base --is-ancestor {b} origin/{branchName}", null, ct);
                    reachable = true; // exit 0
                }
                catch { reachable = false; } // not ancestor or error → keep the branch

                if (!reachable) continue;

                try
                {
                    await RunGitInDirAsync(inPlaceCheckout, $"branch -D {b}", null, ct);
                    _logger.LogInformation(
                        "Cleaned up legacy temp branch {Branch} (reachable from origin/{Final})", b, branchName);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipped deleting legacy temp branch {Branch}", b);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy temp-branch cleanup skipped (non-fatal)");
        }
    }

    private async Task MergeIndividualBranchesAsync(
        string inPlaceCheckout, string tempDir, string branchName,
        string? bareRepoPath, CancellationToken ct)
    {
        var cfg = _config.Value;
        var runScope = _ctx.RunId[..8];
        var branchRunScope = cfg_BranchRunScope();
        var scopeFilter = !string.IsNullOrEmpty(branchRunScope) ? branchRunScope : runScope;

        var mergedPrBranches = GetMergedPrBranches();
        var remotePrefix = bareRepoPath is not null ? "vdt-local" : "origin";
        var allBranches = await RunGitCaptureInDirAsync(inPlaceCheckout,
            $"branch -r --list \"{remotePrefix}/agent/*\"", ct);
        var agentBranches = (allBranches ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => b.Contains($"/{scopeFilter}/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mergedPrBranches.Count > 0)
        {
            agentBranches = agentBranches
                .Where(remoteBranch =>
                {
                    var localRef = remoteBranch.Contains('/')
                        ? remoteBranch[(remoteBranch.IndexOf('/') + 1)..]
                        : remoteBranch;
                    return mergedPrBranches.Any(mb =>
                        localRef.Equals(mb, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
        }

        if (agentBranches.Count == 0 && remotePrefix == "vdt-local")
        {
            allBranches = await RunGitCaptureInDirAsync(inPlaceCheckout,
                "branch -r --list \"origin/agent/*\"", ct);
            agentBranches = (allBranches ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(b => b.Contains($"/{scopeFilter}/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _logger.LogInformation("Final submission: merging {Count} agent branch(es) into {Branch}",
            agentBranches.Count, branchName);

        var merged = new List<string>();
        var failed = new List<(string Branch, string Error)>();

        foreach (var branch in agentBranches)
        {
            try
            {
                await RunGitInDirAsync(tempDir, $"merge {branch} --no-edit --allow-unrelated-histories", null, ct);
                merged.Add(branch);
            }
            catch (Exception ex)
            {
                try { await RunGitInDirAsync(tempDir, "merge --abort", null, ct); }
                catch { try { await RunGitInDirAsync(tempDir, "reset --merge", null, ct); } catch { } }
                failed.Add((branch, ex.Message));
                _logger.LogWarning(ex, "Failed to merge {Branch} into final branch", branch);
            }
        }

        if (failed.Count > 0)
        {
            var failList = string.Join("\n", failed.Select(f => $"  - {f.Branch}: {f.Error}"));
            throw new InvalidOperationException(
                $"Final submission aborted: {failed.Count} agent branch(es) failed to merge:\n{failList}");
        }

        if (merged.Count == 0)
        {
            throw new InvalidOperationException(
                "Final submission aborted: no agent branches found to merge.");
        }
    }

    private async Task PushFromBareRepoAsync(string branchName, string token, CancellationToken ct)
    {
        // In bare repo mode, push bare repo's default branch as the working branch on the remote.
        var defaultBranch = Ado.DefaultBranch ?? "main";
        var remoteUrl = GetAdoCloneUrl(token);
        // Use --force: bare repo pushing to raw URL has no tracking ref for --force-with-lease.
        var psi = new ProcessStartInfo("git",
            $"push \"{remoteUrl}\" refs/heads/{defaultBranch}:refs/heads/{branchName} --force")
        {
            WorkingDirectory = _ctx.BareRepo.BareRepoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_DIR"] = _ctx.BareRepo.BareRepoPath!;
        SetAdoGitAuthEnv(psi, token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        await stdoutTask;
        if (proc.ExitCode != 0)
        {
            var stderr = await stderrTask;
            stderr = RedactToken(stderr, token);
            throw new InvalidOperationException($"git push to ADO failed (exit {proc.ExitCode}): {stderr}");
        }
    }

    private async Task PushFromDirAsync(string workDir, string branchName, string remoteUrl,
        string token, CancellationToken ct)
    {
        // Use --force: temp worktree pushing to raw URL has no tracking ref for --force-with-lease.
        var psi = new ProcessStartInfo("git",
            $"push \"{remoteUrl}\" HEAD:refs/heads/{branchName} --force")
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        SetAdoGitAuthEnv(psi, token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        await stdoutTask;
        if (proc.ExitCode != 0)
        {
            var stderr = await stderrTask;
            stderr = RedactToken(stderr, token);
            throw new InvalidOperationException($"git push to ADO failed (exit {proc.ExitCode}): {stderr}");
        }
    }

    /// <summary>
    /// Builds the ADO clone URL with embedded credentials.
    /// Format: https://:{token}@dev.azure.com/{org}/{project}/_git/{repo}
    /// </summary>
    private string GetAdoCloneUrl(string token)
    {
        var ado = Ado;
        var scheme = GetAuthScheme();
        if (scheme == "Bearer")
        {
            // For bearer tokens, use the token directly as password with empty user
            return $"https://:{token}@dev.azure.com/{ado.Organization}/{ado.Project}/_git/{ado.Repository}";
        }
        // PAT auth — same format, colon prefix means empty username
        return $"https://:{token}@dev.azure.com/{ado.Organization}/{ado.Project}/_git/{ado.Repository}";
    }

    private void SetAdoGitAuthEnv(ProcessStartInfo psi, string token)
    {
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = "echo";
        // Use extraHeader for auth — works for both PAT and bearer
        var scheme = GetAuthScheme();
        string authValue;
        if (scheme == "Basic")
        {
            authValue = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($":{token}"))}";
        }
        else
        {
            authValue = $"Bearer {token}";
        }
        psi.Environment["GIT_CONFIG_COUNT"] = "1";
        psi.Environment["GIT_CONFIG_KEY_0"] = "http.https://dev.azure.com/.extraHeader";
        psi.Environment["GIT_CONFIG_VALUE_0"] = $"Authorization: {authValue}";
    }

    #endregion

    #region ADO REST API

    private async Task<int> CreatePRViaRestAsync(
        string headBranch, string baseBranch, string title, string body,
        string token, CancellationToken ct)
    {
        var ado = Ado;
        var url = $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_apis/git/repositories/{ado.Repository}/pullrequests?api-version=7.1";

        // Handle ADO 4000-char description limit
        string description;
        bool needsOverflow = body.Length > MaxDescriptionLength;
        if (needsOverflow)
        {
            var cutPoint = body.LastIndexOf('\n', OverflowDescriptionBudget);
            if (cutPoint <= 0) cutPoint = OverflowDescriptionBudget;
            description = body[..cutPoint] + $"\n\n---\n{OverflowMarker}\n*Full description in first comment (ADO 4000 char limit)*";
        }
        else
        {
            description = body;
        }

        var payload = new
        {
            sourceRefName = $"refs/heads/{headBranch}",
            targetRefName = $"refs/heads/{baseBranch}",
            title,
            description
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Post, url, token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ADO PR creation failed ({response.StatusCode}): {RedactToken(responseBody, token)}");
        }

        var result = JsonSerializer.Deserialize<AdoPrResponse>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("ADO returned null for PR creation");

        var prId = result.PullRequestId;

        // Add labels
        await AddLabelsAsync(prId, new[] { "final-integration", "awaiting-human-review", "AI-Generated" }, token, ct);

        // Post overflow comment with full body
        if (needsOverflow)
            await PostOverflowCommentAsync(prId, body, token, ct);

        _logger.LogInformation("Created ADO PR #{Id}: {Title} (overflow={Overflow})", prId, title, needsOverflow);
        return prId;
    }

    /// <summary>
    /// Search for an existing active PR with the same source/target branches.
    /// Handles "PR created but persist failed" idempotency case.
    /// </summary>
    private async Task<int> FindExistingPrAsync(
        string headBranch, string baseBranch, string token, CancellationToken ct)
    {
        try
        {
            var ado = Ado;
            var url = $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_apis/git/repositories/{ado.Repository}/pullrequests" +
                      $"?searchCriteria.sourceRefName=refs/heads/{headBranch}" +
                      $"&searchCriteria.targetRefName=refs/heads/{baseBranch}" +
                      $"&searchCriteria.status=active" +
                      $"&api-version=7.1";

            var request = CreateAuthenticatedRequest(HttpMethod.Get, url, token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return -1;

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<AdoPrListResponse>(body, JsonOptions);
            if (result?.Value is { Count: > 0 })
            {
                return result.Value[0].PullRequestId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to search for existing ADO PR — will create new one");
        }
        return -1;
    }

    private async Task AddLabelsAsync(int prNumber, string[] labels, string token, CancellationToken ct)
    {
        var ado = Ado;
        var url = $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_apis/git/repositories/{ado.Repository}/pullrequests/{prNumber}/labels?api-version=7.1-preview";

        foreach (var label in labels)
        {
            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Post, url, token);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { name = label }, JsonOptions),
                    Encoding.UTF8, "application/json");
                await _http.SendAsync(request, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not add label '{Label}' to ADO PR #{PrId}", label, prNumber);
            }
        }
    }

    private async Task PostOverflowCommentAsync(int prNumber, string fullBody, string token, CancellationToken ct)
    {
        try
        {
            var ado = Ado;
            var url = $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_apis/git/repositories/{ado.Repository}/pullrequests/{prNumber}/threads?api-version=7.1";

            var thread = new
            {
                comments = new[]
                {
                    new
                    {
                        parentCommentId = 0,
                        content = fullBody,
                        commentType = 1 // Text
                    }
                },
                status = 4 // Closed (informational, not a review thread)
            };

            var request = CreateAuthenticatedRequest(HttpMethod.Post, url, token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(thread, JsonOptions),
                Encoding.UTF8, "application/json");
            await _http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post overflow comment on ADO PR #{PrId}", prNumber);
        }
    }

    /// <summary>
    /// Ensures the target branch exists on ADO. Creates it from the default branch if missing.
    /// </summary>
    private async Task EnsureRemoteBranchExistsAsync(
        string workingBranch, string defaultBranch, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingBranch) || workingBranch == defaultBranch)
            return;

        // Check if branch exists via refs API
        var exists = await RemoteBranchExistsAsync(workingBranch, token, ct);
        if (exists)
        {
            _logger.LogInformation("Working branch '{Branch}' exists on ADO remote", workingBranch);
            return;
        }

        _logger.LogInformation("Working branch '{Branch}' does not exist on ADO — creating from '{Default}'",
            workingBranch, defaultBranch);

        try
        {
            // Get the default branch HEAD SHA
            var defaultSha = await GetBranchHeadShaAsync(defaultBranch, token, ct);
            if (string.IsNullOrWhiteSpace(defaultSha))
                throw new InvalidOperationException($"Could not resolve SHA for {defaultBranch}");

            // Create branch via refs POST
            var ado = Ado;
            var url = $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_apis/git/repositories/{ado.Repository}/refs?api-version=7.1";

            var payload = new[]
            {
                new
                {
                    name = $"refs/heads/{workingBranch}",
                    oldObjectId = "0000000000000000000000000000000000000000",
                    newObjectId = defaultSha
                }
            };

            var request = CreateAuthenticatedRequest(HttpMethod.Post, url, token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Re-check — might have been created concurrently
                if (await RemoteBranchExistsAsync(workingBranch, token, ct))
                {
                    _logger.LogInformation("Working branch '{Branch}' was created concurrently", workingBranch);
                    return;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Failed to create branch '{workingBranch}' on ADO ({response.StatusCode}): {body}");
            }

            _logger.LogInformation("✅ Created working branch '{Branch}' on ADO from {Default} ({Sha})",
                workingBranch, defaultBranch, defaultSha[..Math.Min(8, defaultSha.Length)]);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            if (await RemoteBranchExistsAsync(workingBranch, token, ct))
            {
                _logger.LogInformation("Working branch '{Branch}' was created concurrently", workingBranch);
                return;
            }
            throw new InvalidOperationException(
                $"Unable to create working branch '{workingBranch}' on ADO: {ex.Message}", ex);
        }
    }

    private async Task<bool> RemoteBranchExistsAsync(string branch, string token, CancellationToken ct)
    {
        try
        {
            var ado = Ado;
            var url = $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_apis/git/repositories/{ado.Repository}/refs" +
                      $"?filter=heads/{branch}&api-version=7.1";

            var request = CreateAuthenticatedRequest(HttpMethod.Get, url, token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<AdoRefsResponse>(body, JsonOptions);
            return result?.Value?.Any(r =>
                r.Name?.Equals($"refs/heads/{branch}", StringComparison.OrdinalIgnoreCase) == true) == true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> GetBranchHeadShaAsync(string branch, string token, CancellationToken ct)
    {
        try
        {
            var ado = Ado;
            var url = $"https://dev.azure.com/{ado.Organization}/{ado.Project}/_apis/git/repositories/{ado.Repository}/refs" +
                      $"?filter=heads/{branch}&api-version=7.1";

            var request = CreateAuthenticatedRequest(HttpMethod.Get, url, token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<AdoRefsResponse>(body, JsonOptions);
            return result?.Value?.FirstOrDefault(r =>
                r.Name?.Equals($"refs/heads/{branch}", StringComparison.OrdinalIgnoreCase) == true)?.ObjectId;
        }
        catch
        {
            return null;
        }
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        var scheme = GetAuthScheme();
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, FormatAuthHeaderValue(token));
        return request;
    }

    #endregion

    #region Shared Git Helpers

    private string? cfg_BranchRunScope()
    {
        try
        {
            using var conn = _ctx.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT head_branch FROM local_pull_requests
                WHERE run_id = @runId AND head_branch LIKE 'agent/%' AND state = 'merged'
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            var result = cmd.ExecuteScalar() as string;
            if (result is not null)
            {
                var parts = result.Split('/');
                if (parts.Length >= 2) return parts[1];
            }
        }
        catch { }
        return null;
    }

    private List<string> GetMergedPrBranches()
    {
        try
        {
            using var conn = _ctx.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT head_branch FROM local_pull_requests
                WHERE run_id = @runId AND state = 'merged' AND head_branch LIKE 'agent/%'
                """;
            cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
            var branches = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var branch = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(branch))
                    branches.Add(branch);
            }
            return branches;
        }
        catch
        {
            return new List<string>();
        }
    }

    private async Task RunGitInDirAsync(string workDir, string args, string? token, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (token is not null) SetAdoGitAuthEnv(psi, token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var proc = Process.Start(psi)!;
        // Read pipes concurrently to prevent pipe deadlock (Lesson #44)
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        var stderr = await stderrTask;
        await stdoutTask;
        if (proc.ExitCode != 0)
        {
            if (token is not null) stderr = RedactToken(stderr, token);
            throw new InvalidOperationException($"git {args.Split(' ')[0]} failed (exit {proc.ExitCode}): {stderr}");
        }
    }

    private async Task<string> RunGitCaptureInDirAsync(string workDir, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        return output;
    }

    #endregion

    #region Persistence

    private async Task PersistSubmissionAsync(int prNumber, string branchName, string title, CancellationToken ct)
    {
        using var conn = _ctx.CreateConnection();

        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS local_final_submissions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                pr_number INTEGER NOT NULL,
                branch_name TEXT NOT NULL,
                title TEXT NOT NULL,
                submitted_at TEXT NOT NULL
            )
            """;
        await createCmd.ExecuteNonQueryAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_final_submissions (run_id, pr_number, branch_name, title, submitted_at)
            VALUES (@runId, @prNumber, @branch, @title, @now)
            """;
        cmd.Parameters.AddWithValue("@runId", _ctx.RunId);
        cmd.Parameters.AddWithValue("@prNumber", prNumber);
        cmd.Parameters.AddWithValue("@branch", branchName);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    #endregion

    #region Helpers

    private static string RedactToken(string text, string token)
    {
        if (string.IsNullOrEmpty(token)) return text;
        return text.Replace(token.Trim(), "***");
    }

    #endregion

    #region DTO types for JSON deserialization

    private sealed record AdoPrResponse
    {
        [JsonPropertyName("pullRequestId")]
        public int PullRequestId { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    private sealed record AdoPrListResponse
    {
        [JsonPropertyName("value")]
        public List<AdoPrResponse>? Value { get; init; }

        [JsonPropertyName("count")]
        public int Count { get; init; }
    }

    private sealed record AdoRefsResponse
    {
        [JsonPropertyName("value")]
        public List<AdoRef>? Value { get; init; }
    }

    private sealed record AdoRef
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("objectId")]
        public string? ObjectId { get; init; }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _bearerProvider?.Dispose();
        _authLock.Dispose();
    }
}
