using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Agents;

/// <summary>
/// Opt-in security review persona that watches ready-for-review PRs and runs an
/// OWASP-flavoured audit on the ones whose title, body, labels, or changed files
/// match security-sensitive triggers (auth, sessions, file uploads, config files,
/// external IO, etc.). Skips quietly on PRs that are not in scope.
///
/// Findings are posted as a single <c>[SecurityAuditor]</c> comment on the PR.
/// When the AI response classifies any finding as <c>critical</c> or <c>high</c>,
/// the agent also adds the <c>security-blocked</c> label so the merge gate can
/// see and surface the block. Lower-severity findings are advisory only.
///
/// Activates only on PRs that are already ready-for-review — the auditor is the
/// LAST reviewer in the pipeline, not a gate ahead of build/test/PM/Architect.
/// </summary>
public class SecurityAuditorAgent : AgentBase
{
    private readonly AgentPlatformServices _platform;

    /// <summary>
    /// Tracks the last-audited HEAD SHA per PR number (in-process dedup).
    /// Re-audits when HEAD advances beyond the last audited SHA (rework detection).
    /// </summary>
    private readonly Dictionary<int, string> _lastAuditedShas = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>Path of the security-auditor system prompt template.</summary>
    private const string SystemPromptTemplate = "security-auditor/role-description";

    /// <summary>Label added when the auditor blocks a PR (critical/high findings).</summary>
    public const string SecurityBlockedLabel = PullRequestWorkflow.Labels.SecurityBlocked;

    /// <summary>Label added when the auditor flags medium/low advisory findings that should be tracked.</summary>
    public const string SecurityAdvisoryLabel = PullRequestWorkflow.Labels.SecurityAdvisory;

    /// <summary>Label added when the auditor needs human escalation.</summary>
    public const string SecurityEscalatedLabel = "security-escalated";

    /// <summary>
    /// Keywords that trigger a security audit when found in PR title or body
    /// (case-insensitive substring match). Mirrors the activation rules in the
    /// security-auditor prompt template — keep both lists in sync if you change
    /// either side.
    /// </summary>
    private static readonly string[] SecurityTriggerKeywords =
    {
        "auth", "login", "password", "token", "session", "oauth", "jwt",
        "api key", "secret", "cookie", "encryption", "encrypt", "decrypt",
        "hash", "sanitiz", "validat", "upload", "parse", "xml", "json parsing",
        "cors", "csp", "rate limit", "external http", "csrf", "xss",
        "sql injection", "permission", "authoriz",
    };

    /// <summary>
    /// Regex patterns matched against removed diff lines (lines starting with '-') to detect
    /// authorization regression — i.e., agents accidentally deleting existing auth guards.
    /// These trigger an audit even if no trigger keyword appears in the PR title or body.
    /// </summary>
    private static readonly Regex[] AuthRemovalPatterns =
    {
        new(@"^\-.*\[Authorize", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*\.RequireAuthorization", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*\.IsInRole\(", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*HasClaim", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*ValidateToken", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*User\.Identity\.IsAuthenticated", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*request\.IsAuthenticated", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*Unauthorized\(\)", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*Forbid\(\)", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*checkPermission", RegexOptions.IgnoreCase | RegexOptions.Multiline),
        new(@"^\-.*verifyAuth", RegexOptions.IgnoreCase | RegexOptions.Multiline),
    };

    /// <summary>
    /// Filenames whose modification triggers a security audit even if the PR title
    /// and body do not contain trigger keywords. Catches secret/config drift.
    /// </summary>
    private static readonly string[] SecuritySensitiveFiles =
    {
        "appsettings.json", "develop-settings.json", ".env", ".env.local",
        ".env.production", "secrets.json", "Web.config", "App.config",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SecurityAuditorAgent(
        AgentIdentity identity,
        AgentCoreServices core,
        AgentPlatformServices platform,
        ILogger<SecurityAuditorAgent> logger)
        : base(identity, core, logger)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        // Wake immediately when a review is explicitly requested (e.g., from FlowMonitor)
        Subscribe<ReviewRequestMessage>(async (msg, _) =>
        {
            Logger.LogInformation("SecurityAuditor received ReviewRequestMessage for PR #{Number}",
                msg.PrNumber);
            WakeLoop();
        });
        Logger.LogInformation("Security Auditor initialized (opt-in reviewer; polls every {Sec}s)",
            (int)PollInterval.TotalSeconds);
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Watching for security-sensitive PRs");

        while (!ct.IsCancellationRequested)
        {
            await WaitIfPausedAsync(ct);

            try
            {
                await ScanOpenPRsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Security Auditor scan loop error — continuing");
            }

            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ScanOpenPRsAsync(CancellationToken ct)
    {
        var openPRs = await _platform.PrService.ListOpenAsync(ct);

        foreach (var pr in openPRs)
        {
            if (ct.IsCancellationRequested) return;

            // Only review PRs that are ready-for-review — be the LAST reviewer in the pipeline.
            if (!pr.Labels.Contains("ready-for-review", StringComparer.OrdinalIgnoreCase))
                continue;

            // In-process SHA-aware dedup: if we already audited this exact HEAD SHA, skip.
            if (_lastAuditedShas.TryGetValue(pr.Number, out var inProcessSha) &&
                string.Equals(inProcessSha, pr.HeadSha, StringComparison.OrdinalIgnoreCase))
                continue;

            // Cross-restart dedup: check for an existing audit comment.
            // If the comment covers the current HEAD SHA, we're done. If HEAD advanced
            // (rework happened), re-audit so security reviews track code changes.
            var (hasAudit, commentSha) = await FindExistingAuditAsync(pr.Number, ct);
            if (hasAudit)
            {
                if (string.Equals(commentSha, pr.HeadSha, StringComparison.OrdinalIgnoreCase))
                {
                    // Existing audit is current — no re-review needed.
                    _lastAuditedShas[pr.Number] = pr.HeadSha;
                    continue;
                }
                // HEAD advanced since the last audit — re-audit (rework happened).
                Logger.LogInformation(
                    "SecurityAuditor re-auditing PR #{Number} — HEAD advanced from {OldSha} to {NewSha} (rework detected)",
                    pr.Number, commentSha ?? "unknown", pr.HeadSha);
            }

            // Decide whether the change needs a security pass at all.
            var triggered = await IsSecuritySensitiveAsync(pr, ct);
            if (!triggered.matched)
            {
                _lastAuditedShas[pr.Number] = pr.HeadSha;
                continue;
            }

            UpdateStatus(AgentStatus.Working,
                $"🔒 Auditing PR #{pr.Number} ({triggered.reason}): {pr.Title}");
            LogActivity("review", $"Starting security audit on PR #{pr.Number} — {triggered.reason}");

            try
            {
                await AuditPrAsync(pr, triggered.reason, ct);
                _lastAuditedShas[pr.Number] = pr.HeadSha;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Security audit failed for PR #{Number}", pr.Number);
                LogActivity("error", $"Security audit failed for PR #{pr.Number}: {ex.Message}");
                // Don't record the SHA — let the next cycle retry.
            }
        }

        UpdateStatus(AgentStatus.Idle, "Watching for security-sensitive PRs");
    }

    /// <summary>
    /// Decide whether the PR is in security-audit scope. Four trigger paths:
    /// 1) the PR has the <c>security-sensitive</c> label;
    /// 2) the PR title or body contains any of <see cref="SecurityTriggerKeywords"/>;
    /// 3) the PR modifies any file matching <see cref="SecuritySensitiveFiles"/>;
    /// 4) the diff removes existing authorization guards (auth regression detection).
    /// </summary>
    private async Task<(bool matched, string reason)> IsSecuritySensitiveAsync(
        PlatformPullRequest pr, CancellationToken ct)
    {
        if (pr.Labels.Contains("security-sensitive", StringComparer.OrdinalIgnoreCase))
            return (true, "security-sensitive label");

        var haystack = (pr.Title + "\n" + (pr.Body ?? "")).ToLowerInvariant();
        foreach (var keyword in SecurityTriggerKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return (true, $"keyword '{keyword}'");
        }

        try
        {
            var diffs = await _platform.PrService.GetFileDiffsAsync(pr.Number, ct);

            // Trigger 3: sensitive file names
            foreach (var diff in diffs)
            {
                var name = Path.GetFileName(diff.FileName);
                if (SecuritySensitiveFiles.Any(sf =>
                    string.Equals(sf, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return (true, $"sensitive file '{name}'");
                }
            }

            // Trigger 4: auth regression — removed authorization guard patterns.
            // Catches agents that accidentally delete [Authorize], RequireAuthorization,
            // IsInRole, etc. without any auth keyword appearing in the PR title/body.
            foreach (var diff in diffs)
            {
                var patch = diff.Patch ?? "";
                foreach (var pattern in AuthRemovalPatterns)
                {
                    if (pattern.IsMatch(patch))
                        return (true, $"authorization guard removed in {diff.FileName} (auth regression risk)");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not list diffs for PR #{Number} during trigger check", pr.Number);

            // Fall back to changed-file names only (pre-diff-content code path).
            try
            {
                var files = await _platform.PrService.GetChangedFilesAsync(pr.Number, ct);
                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    if (SecuritySensitiveFiles.Any(sf =>
                        string.Equals(sf, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return (true, $"sensitive file '{name}'");
                    }
                }
            }
            catch (Exception ex2)
            {
                Logger.LogDebug(ex2, "Could not list changed files for PR #{Number} during trigger check", pr.Number);
            }
        }

        return (false, "");
    }

    /// <summary>
    /// Returns whether a SecurityAuditor comment already exists on the PR,
    /// along with the HEAD SHA it was written against (extracted from the comment body).
    /// <para>
    /// SHA-aware dedup: if the returned <c>commentSha</c> differs from the PR's current
    /// HEAD SHA, the caller should re-audit (rework happened since the last review).
    /// A null <c>commentSha</c> means the existing comment predates SHA embedding and
    /// should be treated as a stale review requiring re-audit.
    /// </para>
    /// </summary>
    private async Task<(bool hasAudit, string? commentSha)> FindExistingAuditAsync(
        int prNumber, CancellationToken ct)
    {
        try
        {
            var comments = await _platform.ReviewService.GetCommentsAsync(prNumber, ct);
            var auditComment = comments.FirstOrDefault(c =>
                c.Body.Contains("[SecurityAuditor]", StringComparison.OrdinalIgnoreCase));
            if (auditComment is null) return (false, null);

            // Extract the HEAD SHA embedded by PostFindingsCommentAsync.
            // Format: "HEAD `<sha>`" (7–40 hex chars)
            var shaMatch = Regex.Match(
                auditComment.Body,
                @"HEAD `([a-f0-9]{7,40})`",
                RegexOptions.IgnoreCase);
            var commentSha = shaMatch.Success ? shaMatch.Groups[1].Value : null;
            return (true, commentSha);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not fetch comments for PR #{Number}", prNumber);
            return (false, null);
        }
    }

    private async Task AuditPrAsync(PlatformPullRequest pr, string triggerReason, CancellationToken ct)
    {
        // 1. Build the system prompt from the template (or fall back to a minimal stub).
        var systemPrompt = await ResolveSystemPromptAsync(ct);

        // 2. Build the user message with PR metadata + the diffs of changed files.
        var userPrompt = await BuildUserPromptAsync(pr, ct);

        // 3. Invoke the LLM.
        var tier = Core!.Config.Agents.GetConfigForRole(AgentRole.SecurityAuditor).ModelTier ?? "standard";
        var kernel = Core!.ModelRegistry.GetKernel(tier, $"security-auditor/pr-{pr.Number}");
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        Microsoft.SemanticKernel.ChatMessageContent response;
        try
        {
            response = await chat.GetChatMessageContentAsync(history, kernel: kernel, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Security auditor LLM call failed for PR #{Number}", pr.Number);
            return; // Skip silently — better to no-op than to post bogus findings.
        }

        var raw = response?.Content ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            Logger.LogWarning("Security auditor returned empty response for PR #{Number}", pr.Number);
            return;
        }

        var parsed = TryParse(raw);
        if (parsed is null)
        {
            Logger.LogWarning("Security auditor produced unparseable JSON for PR #{Number}; skipping", pr.Number);
            return;
        }

        // 4. Skip applicable=false (the prompt explicitly returns this for out-of-scope PRs).
        if (!parsed.Applicable)
        {
            Logger.LogInformation("Security auditor classified PR #{Number} as not-applicable: {Summary}",
                pr.Number, parsed.Summary);
            return;
        }

        // 5. Post the findings comment.
        await PostFindingsCommentAsync(pr, triggerReason, parsed, ct);

        // 6. Apply labels based on approval verdict.
        await ApplyVerdictLabelAsync(pr, parsed, ct);

        LogActivity("review",
            $"Posted security audit on PR #{pr.Number}: {parsed.Findings.Count} findings, verdict={parsed.Approval}");
    }

    private async Task<string> ResolveSystemPromptAsync(CancellationToken ct)
    {
        var techStack = Core!.Config.Project.TechStack ?? "";
        var promptService = Core?.PromptService;
        if (promptService is not null)
        {
            try
            {
                var rendered = await promptService.RenderAsync(
                    SystemPromptTemplate,
                    new Dictionary<string, string> { ["tech_stack"] = techStack },
                    ct);
                if (!string.IsNullOrWhiteSpace(rendered))
                    return rendered;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to load security-auditor template; using stub fallback");
            }
        }

        // Minimal stub — only used when the template file is missing or the prompt service is unavailable.
        return
            $"You are a Security Auditor reviewing a pull request for security defects ONLY. " +
            $"The project uses {techStack}. Identify OWASP Top 10 issues. Output JSON: " +
            "{\"applicable\":true|false,\"findings\":[{\"severity\":\"critical|high|medium|low\"," +
            "\"category\":\"OWASP-A0X\",\"location\":\"<file>:<line>\",\"issue\":\"...\",\"fix\":\"...\"}]," +
            "\"approval\":\"approve|block|escalate\",\"summary\":\"...\"}";
    }

    private async Task<string> BuildUserPromptAsync(PlatformPullRequest pr, CancellationToken ct)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("# Pull Request to audit").AppendLine();
        sb.Append("Number: ").Append(pr.Number).AppendLine();
        sb.Append("Title: ").AppendLine(pr.Title);
        if (pr.Labels.Count > 0)
            sb.Append("Labels: ").AppendLine(string.Join(", ", pr.Labels));
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(pr.Body))
        {
            sb.AppendLine("## PR Body").AppendLine(pr.Body).AppendLine();
        }

        sb.AppendLine("## File diffs").AppendLine();
        try
        {
            var diffs = await _platform.PrService.GetFileDiffsAsync(pr.Number, ct);
            const int perFileBudget = 6000; // characters
            foreach (var diff in diffs)
            {
                sb.Append("### ").Append(diff.FileName)
                  .Append(" (").Append(diff.Status)
                  .Append(", +").Append(diff.Additions)
                  .Append("/-").Append(diff.Deletions).AppendLine(")");
                var patch = diff.Patch ?? "";
                if (patch.Length > perFileBudget)
                    patch = patch[..perFileBudget] + "\n…[truncated]";
                sb.AppendLine("```diff").AppendLine(patch).AppendLine("```").AppendLine();
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not fetch diffs for PR #{Number}", pr.Number);
            sb.AppendLine("(diffs unavailable — judge based on title/body only)");
        }

        sb.AppendLine("Apply your activation rules and produce the JSON output as specified.");
        return sb.ToString();
    }

    private async Task PostFindingsCommentAsync(
        PlatformPullRequest pr, string triggerReason, AuditResult parsed, CancellationToken ct)
    {
        var sb = new StringBuilder(2048);
        var icon = parsed.Approval?.ToLowerInvariant() switch
        {
            "block"     => "🛑",
            "escalate"  => "⚠️",
            _           => "✅",
        };
        sb.Append(icon).Append(" **[SecurityAuditor]** ").Append(Capitalize(parsed.Approval ?? "review"))
          .Append(" — ").AppendLine(parsed.Summary ?? "");
        sb.AppendLine();
        // Embed HEAD SHA so the re-review dedup logic can detect when rework advances the branch.
        sb.Append("_Triggered by: ").Append(triggerReason)
          .Append(" — HEAD `").Append(pr.HeadSha ?? "unknown").AppendLine("`._").AppendLine();

        if (parsed.Findings.Count == 0)
        {
            sb.AppendLine("No security defects found in this diff.");
        }
        else
        {
            sb.AppendLine("| Severity | Category | Location | Issue | Suggested fix |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var f in parsed.Findings)
            {
                sb.Append("| `").Append(f.Severity ?? "?").Append("` | ")
                  .Append(EscapeCell(f.Category)).Append(" | `")
                  .Append(EscapeCell(f.Location)).Append("` | ")
                  .Append(EscapeCell(f.Issue)).Append(" | ")
                  .Append(EscapeCell(f.Fix)).AppendLine(" |");
            }
        }

        try
        {
            await _platform.ReviewService.AddCommentAsync(pr.Number, sb.ToString(), ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to post security-audit comment on PR #{Number}", pr.Number);
        }
    }

    private async Task ApplyVerdictLabelAsync(PlatformPullRequest pr, AuditResult parsed, CancellationToken ct)
    {
        var verdict = parsed.Approval?.ToLowerInvariant();

        // Determine which security label to add (if any) based on verdict.
        var labelToAdd = verdict switch
        {
            "block"    => SecurityBlockedLabel,
            "escalate" => SecurityEscalatedLabel,
            _ => null,
        };

        // For approve verdicts, track advisory findings (medium/low) with a separate label
        // so they are visible on the PR and can be followed up without blocking the pipeline.
        var hasAdvisoryFindings = verdict == "approve" &&
            parsed.Findings.Any(f =>
                string.Equals(f.Severity, "medium", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Severity, "low", StringComparison.OrdinalIgnoreCase));
        if (hasAdvisoryFindings)
            labelToAdd = SecurityAdvisoryLabel;

        // On a clean re-review (approve, no findings), remove stale security labels so the
        // merge path is unblocked. This handles the rework+re-review cycle correctly.
        if (verdict == "approve" && parsed.Findings.Count == 0)
        {
            foreach (var staleLabel in new[] { SecurityBlockedLabel, SecurityEscalatedLabel, SecurityAdvisoryLabel })
            {
                try
                {
                    if (pr.Labels.Contains(staleLabel, StringComparer.OrdinalIgnoreCase))
                    {
                        await _platform.PrService.RemoveLabelAsync(pr.Number, staleLabel, ct);
                        Logger.LogInformation(
                            "Removed stale '{Label}' from PR #{Number} after clean re-review",
                            staleLabel, pr.Number);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not remove label '{Label}' from PR #{Number}", staleLabel, pr.Number);
                }
            }
            return;
        }

        if (labelToAdd is null) return;

        // If we're adding security-blocked, ensure the advisory label is removed (block supersedes advisory).
        if (string.Equals(labelToAdd, SecurityBlockedLabel, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (pr.Labels.Contains(SecurityAdvisoryLabel, StringComparer.OrdinalIgnoreCase))
                    await _platform.PrService.RemoveLabelAsync(pr.Number, SecurityAdvisoryLabel, ct);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not remove advisory label from PR #{Number}", pr.Number);
            }
        }

        try
        {
            await _platform.PrService.AddLabelAsync(pr.Number, labelToAdd, ct);
            Logger.LogInformation("Added '{Label}' to PR #{Number}", labelToAdd, pr.Number);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to apply '{Label}' to PR #{Number}", labelToAdd, pr.Number);
        }
    }

    private static AuditResult? TryParse(string raw)
    {
        // Strip any code fences or stray prose around the JSON.
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = raw[start..(end + 1)];
        try
        {
            return JsonSerializer.Deserialize<AuditResult>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeCell(string? text) =>
        string.IsNullOrEmpty(text) ? ""
            : text.Replace("|", @"\|").Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Capitalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private sealed class AuditResult
    {
        [JsonPropertyName("applicable")]
        public bool Applicable { get; set; }

        [JsonPropertyName("findings")]
        public List<Finding> Findings { get; set; } = new();

        [JsonPropertyName("approval")]
        public string? Approval { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }
    }

    private sealed class Finding
    {
        [JsonPropertyName("severity")]
        public string? Severity { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("issue")]
        public string? Issue { get; set; }

        [JsonPropertyName("fix")]
        public string? Fix { get; set; }
    }
}
