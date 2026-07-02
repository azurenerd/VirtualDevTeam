using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.GitHub.Models;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Strategies.Contracts;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Agents;

/// <summary>
/// A specialist engineer agent created dynamically from an <see cref="SMEAgentDefinition"/>.
/// Unlike <see cref="SmeAgent"/> (which extends CustomAgent), this extends <see cref="EngineerAgentBase"/>
/// and has full engineering capabilities: rework loops, build/test verification, clarification handling,
/// and the complete PR lifecycle. The specialist persona is injected from the definition.
/// 
/// Registers as <see cref="AgentRole.SoftwareEngineer"/> so the leader SE sees it as a team member
/// and can assign work to it via skill-based matching on <see cref="AgentIdentity.Capabilities"/>.
/// </summary>
public class SpecialistEngineerAgent : EngineerAgentBase
{
    /// <summary>The SME definition that created this specialist.</summary>
    public SMEAgentDefinition Definition { get; }

    /// <summary>
    /// Used by the self-claim loop to inspect peer agents' capabilities so we can defer
    /// to a strictly better-matched specialist instead of grabbing a task naively.
    /// Optional: when null (e.g. unit tests), peer comparison is skipped and the legacy
    /// "any keyword match wins" behavior remains.
    /// </summary>
    private readonly AgentRegistry? _registry;

    // Strategy Framework integration (mirrors SoftwareEngineerAgent). When these are wired
    // and StrategyFrameworkConfig.Enabled, the specialist's PR implementation runs through
    // the AGENTIC orchestrator (candidate worktrees + Copilot CLI agentic sessions) so it
    // can produce binary artifacts (PNGs, screenshots, videos) that the legacy FILE-block
    // chat-completion path cannot. On any guard failure the specialist falls back to the
    // base class single-pass path. Optional — null in unit tests.
    private readonly StrategyOrchestrator? _strategyOrchestrator;
    private readonly WinnerApplyService? _winnerApply;
    private readonly IOptionsMonitor<StrategyFrameworkConfig>? _strategyConfig;
    private readonly StrategyTaskStepBridge? _strategyStepBridge;

    public SpecialistEngineerAgent(
        AgentIdentity identity,
        SMEAgentDefinition definition,
        AgentCoreServices core,
        AgentPlatformServices platform,
        AgentWorkspaceServices workspace,
        ILogger<SpecialistEngineerAgent> logger,
        DecisionGateService? decisionGate = null,
        IDecisionLog? decisionLog = null,
        PrePRClarificationStore? clarificationStore = null,
        AgentRegistry? registry = null,
        StrategyOrchestrator? strategyOrchestrator = null,
        WinnerApplyService? winnerApply = null,
        IOptionsMonitor<StrategyFrameworkConfig>? strategyConfig = null,
        StrategyTaskStepBridge? strategyStepBridge = null,
        ClaimedTaskRegistry? claimRegistry = null)
        : base(identity, core, platform, workspace, logger, decisionGate, decisionLog, clarificationStore, claimRegistry)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _registry = registry;
        _strategyOrchestrator = strategyOrchestrator;
        _winnerApply = winnerApply;
        _strategyConfig = strategyConfig;
        _strategyStepBridge = strategyStepBridge;
    }

    protected override string GetRoleDisplayName() => Definition.RoleName;

    protected override string GetImplementationSystemPrompt(string techStack)
    {
        // Try loading from specialist-engineer prompt template first.
        // 2026-05-12: the specialist-engineer/implementation-system template now includes
        // a {{> _shared/image-gen-instructions}} reference + an imperative directive about
        // visual deliverables, so a single template serves all specialists. No more
        // category-specific branching — the agent reads its capabilities + the task body
        // and decides whether the recipe applies.
        if (PromptService is not null)
        {
            var rendered = PromptService.RenderAsync("specialist-engineer/implementation-system",
                new Dictionary<string, string>
                {
                    ["tech_stack"] = techStack,
                    ["role_name"] = Definition.RoleName,
                    ["specialist_persona"] = Definition.SystemPrompt,
                    ["capabilities"] = string.Join(", ", Definition.Capabilities),
                }).GetAwaiter().GetResult();
            if (rendered is not null) return rendered;
        }

        // Fallback: build prompt from definition
        var capabilities = Definition.Capabilities.Count > 0
            ? $"Your specialized capabilities: {string.Join(", ", Definition.Capabilities)}. "
            : "";

        return $"You are a {Definition.RoleName} — a specialist engineer on the development team. " +
            $"{Definition.SystemPrompt}\n\n" +
            $"The project uses {techStack} as its technology stack. " +
            $"{capabilities}" +
            "The PM Specification defines the business requirements, and the Architecture " +
            "document defines the technical design. The GitHub Issue contains the User Story " +
            "and acceptance criteria for this specific task. " +
            "Produce detailed, production-quality code that leverages your domain expertise. " +
            "Ensure the implementation fulfills the business goals from the PM spec.\n\n" +
            "DEPENDENCY RULE: Before using ANY external library, package, or framework, check the project's " +
            "dependency manifest (e.g., .csproj, package.json, requirements.txt, etc.). " +
            "If a dependency is not already listed, add it to the manifest and include that file in your output. " +
            "Never import/using/require a package without ensuring it is declared in the project.";
    }

    protected override string GetReworkSystemPrompt(string techStack)
    {
        if (PromptService is not null)
        {
            var rendered = PromptService.RenderAsync("specialist-engineer/rework-system",
                new Dictionary<string, string>
                {
                    ["tech_stack"] = techStack,
                    ["role_name"] = Definition.RoleName,
                    ["specialist_persona"] = Definition.SystemPrompt,
                    ["capabilities"] = string.Join(", ", Definition.Capabilities)
                }).GetAwaiter().GetResult();
            if (rendered is not null) return rendered;
        }

        return $"You are a {Definition.RoleName} making SURGICAL fixes to an existing pull request based on reviewer feedback. " +
            $"The project uses {techStack}. " +
            $"{Definition.SystemPrompt}\n\n" +
            "SURGICAL REWORK RULES: " +
            "1. Read each feedback item carefully. Make ONLY the changes needed to address that specific item. " +
            "2. Do NOT rewrite, reorganize, or regenerate files that weren't mentioned in the feedback. " +
            "3. Do NOT touch CSS, config, project files, or infrastructure unless the reviewer SPECIFICALLY asked. " +
            "4. Your diff should be minimal — a reviewer should see a small, focused set of changes. " +
            "5. Only include files you actually changed in your output.";
    }

    private int _idleLoopCount;
    private const int SelfClaimAfterIdleLoops = 2; // Self-claim faster than SE workers (specialists are always idle)

    /// <summary>
    /// Extracts a normalized set of keywords from raw capability strings: lowercases, splits on
    /// common separators, drops trivially-short tokens. Used by both the self's match scoring
    /// and by <see cref="CollectPeerCapabilityKeywords"/> so apples-to-apples comparison holds.
    /// </summary>
    private static HashSet<string> ExtractCapabilityKeywords(IReadOnlyList<string> caps) =>
        caps
            .SelectMany(c => c.ToLowerInvariant().Split(new[] { ' ', '-', '_', '/', ',' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length > 3)
            .ToHashSet();

    /// <summary>
    /// Snapshots every peer specialist's capability-keyword set so this agent can compare its
    /// own task-match score against the best peer score. "Peer specialists" = other agents
    /// registered as <see cref="AgentRole.SoftwareEngineer"/> that aren't me and have at least
    /// one capability declared (generalist SE workers have empty caps and aren't competition).
    /// Returns empty list when the registry isn't available (unit tests) — the caller then
    /// falls back to plain "any keyword match wins" behaviour.
    /// </summary>
    private List<HashSet<string>> CollectPeerCapabilityKeywords()
    {
        if (_registry is null) return new List<HashSet<string>>();
        try
        {
            return _registry.GetAllAgents()
                .Where(a => a.Identity.Role == AgentRole.SoftwareEngineer)
                .Where(a => !string.Equals(a.Identity.Id, Identity.Id, StringComparison.Ordinal))
                .Where(a => a.Identity.Capabilities is { Count: > 0 })
                .Select(a => ExtractCapabilityKeywords(a.Identity.Capabilities!))
                .Where(s => s.Count > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to enumerate peer capabilities; falling back to no-peer-comparison");
            return new List<HashSet<string>>();
        }
    }

    /// <summary>
    /// Override the base implementation to FIRST attempt the Strategy Framework path,
    /// which uses agentic Copilot CLI sessions (full shell + tool access). The base class
    /// path is chat-completion-only, which can only emit text/FILE-blocks and therefore
    /// cannot produce binary deliverables (PNGs, screenshots, videos, audio). Falling
    /// back to base when the framework isn't wired or declines preserves legacy behaviour.
    /// </summary>
    protected override async Task ImplementAndCommitAsync(AgentPullRequest pr, AgentIssue issue, CancellationToken ct)
    {
        if (await TrySpecialistFrameworkAsync(pr, issue, ct))
        {
            Logger.LogInformation(
                "{Role} {Name}: strategy framework shipped winner for PR #{PrNumber}; skipping legacy single-pass",
                Identity.Role, Identity.DisplayName, pr.Number);
            return;
        }
        await base.ImplementAndCommitAsync(pr, issue, ct);
    }

    /// <summary>
    /// Rework override: specialist deliverables are often binary (PNGs, screenshots, audio
    /// clips) which cannot be authored through the base class's chat-completion FILE-block
    /// pipeline. Re-run the Strategy Framework on rework so agentic Copilot CLI candidates
    /// can re-invoke their shell tools (REST image-gen, Playwright capture, etc.) against
    /// the reviewer feedback. The framework receives the rework feedback as additional
    /// context appended to the task description; the LLM judge ranks the new candidates
    /// against the same acceptance criteria. Falls back to base.HandleReworkAsync when
    /// framework isn't wired, declines, or produces no winner — preserving the legacy
    /// surgical-edit path for code-producing specialists.
    /// Observed 2026-05-12: Artist SME 1 rework loop on PR #1505 was stuck running
    /// dotnet build → tests → "nothing staged" → push repeatedly with NO image-gen calls,
    /// because the rework path never invoked the framework that the initial implementation
    /// had used. This override closes the gap.
    /// </summary>
    protected override async Task HandleReworkAsync(List<ReworkItem> reworkBatch, CancellationToken ct)
    {
        if (reworkBatch.Count == 0)
        {
            await base.HandleReworkAsync(reworkBatch, ct);
            return;
        }

        var firstItem = reworkBatch[0];
        var prData = await PrService.GetAsync(firstItem.PrNumber, ct);
        if (prData is null || !string.Equals(prData.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            await base.HandleReworkAsync(reworkBatch, ct);
            return;
        }

        var pr = prData.ToAgentPR();
        AgentIssue? issue = null;
        if (CurrentIssueNumber is int issueNum)
        {
            var issueData = await WorkItemService.GetAsync(issueNum, ct);
            issue = issueData?.ToAgentIssue();
        }
        if (issue is null)
        {
            Logger.LogDebug("Specialist rework framework: no current issue context — falling back to base for PR #{PrNumber}", pr.Number);
            await base.HandleReworkAsync(reworkBatch, ct);
            return;
        }

        // Append concatenated reviewer feedback to the issue body so the framework's task
        // context carries the rework reasons through to each candidate's CLI session.
        var feedbackSection = string.Join("\n\n---\n\n", reworkBatch.Select((r, i) =>
            $"## Rework feedback {i + 1} from {r.Reviewer}\n\n{r.Feedback}"));
        var augmentedBody = string.IsNullOrWhiteSpace(issue.Body)
            ? feedbackSection
            : issue.Body + "\n\n---\n\n# REWORK CONTEXT\n\n" + feedbackSection;
        var augmentedIssue = issue with { Body = augmentedBody };

        if (await TrySpecialistFrameworkAsync(pr, augmentedIssue, ct))
        {
            Logger.LogInformation(
                "{Role} {Name}: strategy framework shipped rework winner for PR #{PrNumber}; skipping legacy surgical-edit path",
                Identity.Role, Identity.DisplayName, pr.Number);
            return;
        }

        Logger.LogInformation(
            "Specialist rework framework declined or produced no winner for PR #{PrNumber}; falling back to base surgical-edit path",
            pr.Number);
        await base.HandleReworkAsync(reworkBatch, ct);
    }

    /// <summary>
    /// Run the strategy framework for this specialist's PR. Mirrors
    /// <c>SoftwareEngineerAgent.TryRunStrategyFrameworkAsync</c> but takes its inputs from
    /// the specialist's <see cref="AgentIssue"/> instead of the leader's
    /// <c>EngineeringTask</c>, and skips a few leader-only conveniences
    /// (failed-winner context capture, multi-task bridges).
    /// Returns true when the framework committed AND pushed a winner — caller skips base.
    /// </summary>
    private async Task<bool> TrySpecialistFrameworkAsync(AgentPullRequest pr, AgentIssue issue, CancellationToken ct)
    {
        // agent-workspace-on-wrong-branch (2026-05-12): each early-exit path now emits an
        // INFORMATION-level log line including the SPECIFIC reason. Previously the framework
        // could decline silently for any of 5+ guard conditions, leaving the dashboard showing
        // "Working" with no indication of why the agentic path didn't fire. With these logs an
        // operator (or FlowMonitor's framework-log watcher) can immediately see "Strategy
        // framework declined for PR #X: <reason>" and act.
        if (_strategyOrchestrator is null || _winnerApply is null || _strategyConfig is null)
        {
            Logger.LogInformation(
                "Strategy framework declined for PR #{PrNumber}: required DI services not injected " +
                "(orchestrator={Orch}, winnerApply={WA}, strategyConfig={SC}) — this specialist's host " +
                "likely lacks AddStrategyFramework() registration.",
                pr.Number,
                _strategyOrchestrator is not null,
                _winnerApply is not null,
                _strategyConfig is not null);
            return false;
        }

        var cfg = _strategyConfig.CurrentValue;
        if (!cfg.Enabled || cfg.EnabledStrategies.Count == 0)
        {
            Logger.LogInformation(
                "Strategy framework declined for PR #{PrNumber}: framework disabled in config " +
                "(Enabled={Enabled}, EnabledStrategies.Count={Count}).",
                pr.Number, cfg.Enabled, cfg.EnabledStrategies.Count);
            return false;
        }

        if (BuildRunnerSvc is null)
        {
            Logger.LogInformation(
                "Strategy framework declined for PR #{PrNumber}: workspace prerequisites missing (BuildRunnerSvc=false).",
                pr.Number);
            return false;
        }

        // Strategy claim coordination: prevent duplicate strategy evaluation by multiple agents
        if (ClaimRegistry is not null)
        {
            if (!ClaimRegistry.TryClaimStrategy(issue.Number, Identity.Id))
            {
                Logger.LogInformation(
                    "Strategy framework declined for PR #{PrNumber}: another agent is already evaluating strategies for issue #{IssueNumber}",
                    pr.Number, issue.Number);
                return false;
            }
        }

        if (Workspace is null)
        {
            Logger.LogInformation(
                "Strategy framework: Workspace is null for PR #{PrNumber}; attempting on-demand workspace initialization (mode={Mode}, LocalCheckoutPath={LocalCheckoutPath}).",
                pr.Number, Config.Workspace.WorkspaceMode, Config.Workspace.LocalCheckoutPath ?? "(null)");

            if (!await EnsureWorkspaceInitializedAsync(ct))
            {
                Logger.LogInformation(
                    "Strategy framework declined for PR #{PrNumber}: workspace prerequisites missing (Workspace=false after on-demand init attempt).",
                    pr.Number);
                return false;
            }
        }

        var branchName = pr.HeadBranch;
        if (string.IsNullOrEmpty(branchName))
        {
            Logger.LogInformation(
                "Strategy framework declined for PR #{PrNumber}: PR has no HeadBranch (PlatformPullRequest may be incomplete).",
                pr.Number);
            return false;
        }

        try
        {
            // Resume PR branch state from the remote — base CreateTaskBranchAsync already pushed it.
            await Workspace.CheckoutBranchAsync(branchName, ct);

            var localHead = (await Workspace.GetHeadShaAsync("HEAD", ct)).Trim();
            if (string.IsNullOrEmpty(localHead))
            {
                Logger.LogWarning("Strategy framework declined for PR #{PrNumber}: could not resolve local HEAD for {Branch} after checkout.", pr.Number, branchName);
                return false;
            }

            var remoteHead = (await Workspace.GetRemoteShaAsync(branchName, ct)).Trim();
            if (!string.IsNullOrEmpty(remoteHead) &&
                !string.Equals(remoteHead, localHead, StringComparison.OrdinalIgnoreCase))
            {
                // 2026-05-12 agent-workspace-on-wrong-branch fix: previously this path declined
                // silently, leaving the framework permanently stuck for any agent whose local clone
                // got out of sync with the remote (which is common after restarts, manual operator
                // pushes, or concurrent PR updates). Recover proactively by fetching + hard-resetting
                // local to the remote's HEAD. The Workspace is throwaway scratch space; losing
                // unstaged local state here is acceptable because (a) any meaningful work would
                // already be committed and pushed, (b) the framework runs in a candidate worktree
                // anyway so this main workspace is just a base reference.
                Logger.LogInformation(
                    "Strategy framework: remote {Branch} ({Remote}) ahead of local ({Local}) — re-checking out + resetting to recover before framework run.",
                    branchName, remoteHead, localHead);
                try
                {
                    // CheckoutBranchAsync already does: fetch origin {branch} + checkout + reset --hard origin/{branch}
                    // (LocalWorkspace.cs lines 252-255). That's exactly the recovery semantics we want here.
                    await Workspace.CheckoutBranchAsync(branchName, ct);
                    localHead = (await Workspace.GetHeadShaAsync("HEAD", ct)).Trim();
                    if (!string.Equals(remoteHead, localHead, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogWarning(
                            "Strategy framework declined for PR #{PrNumber}: fetch+reset did not converge ({Local} != {Remote}); falling back to base path.",
                            pr.Number, localHead, remoteHead);
                        return false;
                    }
                }
                catch (Exception fetchEx)
                {
                    Logger.LogWarning(fetchEx,
                        "Strategy framework declined for PR #{PrNumber}: fetch+reset failed; falling back to base path.",
                        pr.Number);
                    return false;
                }
            }

            var runId = StateStore.LastBootUtc != DateTime.MinValue
                ? StateStore.LastBootUtc.ToString("yyyyMMddTHHmmssZ")
                : "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

            var techStack = Config.Project.TechStack ?? "";
            string pmSpecDoc = "", architectureDoc = "";
            try { pmSpecDoc = await ProjectFiles.GetPMSpecAsync(ct) ?? ""; } catch { /* best-effort */ }
            try { architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct) ?? ""; } catch { /* best-effort */ }

            var issueContext = $"\n\n## GitHub Issue #{issue.Number}: {issue.Title}\n{issue.Body}";

            // Synthesize a TaskContext from the issue. Task name = issue title (stripped of agent prefix),
            // task id = `specialist-{issueNumber}` (unique per issue), wave/complexity defaults are
            // reasonable; the orchestrator only uses these for telemetry/routing.
            var cleanTitle = issue.Title.Contains(':')
                ? issue.Title[(issue.Title.IndexOf(':') + 1)..].Trim()
                : issue.Title;

            var taskCtx = new TaskContext
            {
                TaskId = $"specialist-{issue.Number}",
                TaskTitle = cleanTitle,
                TaskDescription = issue.Body ?? "",
                PrBranch = branchName,
                BaseSha = localHead,
                RunId = runId,
                AgentRepoPath = Workspace.RepoPath,
                Complexity = 3, // medium default — specialists usually don't get the complexity field
                IsWebTask = false, // specialist deliverables are domain-specific (art/security/etc), not generic web
                Wave = "1",
                PmSpec = pmSpecDoc,
                Architecture = architectureDoc,
                TechStack = techStack,
                IssueContext = issueContext,
                DesignContext = "",
                ExistingProjectContext = Config.Project.ExistingProjectContext,
            };

            UpdateStatus(AgentStatus.Working, $"Strategy candidates: {cleanTitle}");

            var enabledCount = cfg.EnabledStrategies.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var containerStepId = _strategyStepBridge?.RegisterTask(taskCtx.RunId, taskCtx.TaskId, Identity.Id, enabledCount);

            try
            {
                await _strategyOrchestrator.EmitTaskPrLinkedAsync(
                    taskCtx.RunId, taskCtx.TaskId, pr.Number, pr.Url, pr.Title, ct);
            }
            catch (Exception linkEx)
            {
                Logger.LogDebug(linkEx, "Failed to emit TaskPrLinked for PR #{PrNumber}", pr.Number);
            }

            var outcome = await _strategyOrchestrator.RunCandidatesAsync(taskCtx, ct);

            if (!outcome.HasWinner)
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, taskCtx.TaskId, succeeded: false, winnerStrategy: null);
                Logger.LogInformation("Strategy framework: no winner for PR #{PrNumber}; falling back", pr.Number);
                return false;
            }

            var winner = outcome.Evaluation.Winner!;
            if (string.IsNullOrEmpty(winner.Patch))
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, taskCtx.TaskId, succeeded: false);
                Logger.LogInformation("Strategy framework: winner {Strategy} produced empty patch; falling back", winner.StrategyId);
                return false;
            }

            // Apply winner patch — re-capture localHead right before apply since strategy
            // evaluation may have taken 15+ min and SyncWithMainAsync could rebase the branch.
            localHead = (await Workspace.GetHeadShaAsync("HEAD", ct)).Trim();

            // Primary: file-level copy from candidate worktree (avoids git apply brittleness).
            var winnerWorktreePath = outcome.Evaluation.WinnerWorktreePath;
            ApplyOutcome apply;
            if (!string.IsNullOrEmpty(winnerWorktreePath) && Directory.Exists(winnerWorktreePath))
            {
                apply = await _winnerApply.ApplyFromWorktreeAsync(
                    Workspace.RepoPath, branchName, localHead, winnerWorktreePath, ct);
                // Fall back to patch-based apply when file-copy fails for any recoverable reason
                if (!apply.Applied && (apply.FailureReason?.StartsWith("overlap") == true
                                    || apply.FailureReason == "worktree-no-changes"))
                {
                    Logger.LogInformation(
                        "File-copy failed ({Reason}); falling back to 3-way patch apply for PR #{PrNumber}",
                        apply.FailureReason, pr.Number);
                    if (!string.IsNullOrWhiteSpace(winner.Patch))
                    {
                        apply = await _winnerApply.ApplyAsync(Workspace.RepoPath, branchName, localHead, winner.Patch, ct);
                    }
                    else
                    {
                        Logger.LogError(
                            "Strategy framework: winner {Strategy} worktree apply returned {Reason} AND " +
                            "winner.Patch is empty for PR #{PrNumber} — no recovery path",
                            winner.StrategyId, apply.FailureReason, pr.Number);
                    }
                }
            }
            else
            {
                apply = await _winnerApply.ApplyAsync(Workspace.RepoPath, branchName, localHead, winner.Patch, ct);
            }

            // Dispose the winner worktree handle now
            if (outcome.Evaluation.WinnerWorktreeHandle is not null)
            {
                try { await outcome.Evaluation.WinnerWorktreeHandle.DisposeAsync(); }
                catch (Exception ex) { Logger.LogDebug(ex, "Failed to dispose winner worktree handle"); }
            }

            if (!apply.Applied)
            {
                _strategyStepBridge?.UnregisterTask(taskCtx.RunId, taskCtx.TaskId, succeeded: false);
                Logger.LogWarning("Strategy framework: winner apply failed for PR #{PrNumber}: {Reason}; falling back",
                    pr.Number, apply.FailureReason);
                return false;
            }

            // Build-verify before committing. If build fails, try build-fix loop on the
            // framework output rather than discarding it — the framework code is a much
            // better starting point than starting from scratch with legacy single-pass.
            var wsConfig = Config.Workspace;
            var build = await BuildRunnerSvc.BuildAsync(Workspace.RepoPath, wsConfig.BuildCommand, wsConfig.BuildTimeoutSeconds, ct);
            if (!build.Success)
            {
                Logger.LogInformation(
                    "Strategy framework: winner build failed for PR #{PrNumber} — attempting build-fix on framework output",
                    pr.Number);
                UpdateStatus(AgentStatus.Working, $"🔧 Fixing framework build errors for PR #{pr.Number}");

                // Snapshot workspace-changed files so the build-fix scope filter knows which
                // files are "ours" and doesn't revert them. Empty originalFiles causes the
                // post-hoc filter to classify ALL application files as out-of-scope.
                var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
                var chat = kernel.GetRequiredService<IChatCompletionService>();
                var changedPaths = await Workspace.GetChangedFilePathsAsync(ct);
                var syntheticFiles = changedPaths
                    .Select(p => new VirtualDevTeam.Core.AI.CodeFileParser.CodeFile(p, ""))
                    .ToArray();

                var (buildFixed, _) = await BuildWithRetryAsync(
                    syntheticFiles, chat, wsConfig,
                    stepNumber: 1, totalSteps: 1,
                    stepDescription: $"Fix framework build errors for {cleanTitle}",
                    ct, cliEditMode: true);

                if (!buildFixed)
                {
                    _strategyStepBridge?.UnregisterTask(taskCtx.RunId, taskCtx.TaskId, succeeded: false);
                    Logger.LogWarning("Strategy framework: build-fix failed for PR #{PrNumber}; reverting + falling back to legacy", pr.Number);
                    await Workspace.RevertUncommittedChangesAsync(ct);
                    return false;
                }
                Logger.LogInformation("Strategy framework: build-fix succeeded for PR #{PrNumber}", pr.Number);
            }

            var trailers = new Dictionary<string, string>
            {
                [StrategyTrailers.StrategyKey] = winner.StrategyId,
                [StrategyTrailers.RunIdKey] = runId,
            };
            var subject = $"Implement {cleanTitle}";
            var commitBody = $"Generated by strategy '{winner.StrategyId}' (run {runId}).";
            var fullMessage = StrategyTrailers.Append($"{subject}\n\n{commitBody}\n", trailers);

            await Workspace.CommitAsync(fullMessage, ct);
            await Workspace.PushAsync(branchName, ct);

            _strategyStepBridge?.UnregisterTask(taskCtx.RunId, taskCtx.TaskId, succeeded: true, winnerStrategy: winner.StrategyId);

            // Mark the PR ready for review through the inherited finalization path.
            await MarkPrCompleteAsync(pr, issue, ct);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Strategy framework failed for PR #{PrNumber}; falling back to single-pass", pr.Number);
            return false;
        }
        finally
        {
            // Always release the strategy claim so other agents can evaluate if needed.
            ClaimRegistry?.ReleaseStrategy(issue.Number);
        }
    }

    /// <summary>
    /// Self-claim fallback: if this specialist has been idle for several loops and the leader
    /// hasn't assigned work via the bus, look for unassigned engineering-task issues on GitHub
    /// that match our capabilities and claim one directly.
    /// </summary>
    protected override async Task RunAdditionalLoopWorkAsync(CancellationToken ct)
    {
        // Only self-claim if we have no current work
        if (CurrentPrNumber is not null || AssignmentQueue.Count > 0)
        {
            _idleLoopCount = 0;
            return;
        }

        _idleLoopCount++;
        if (_idleLoopCount < SelfClaimAfterIdleLoops)
            return;

        try
        {
            // Find unassigned engineering tasks
            var allItems = await WorkItemService.ListByLabelAsync("engineering-task", "open", ct);
            var unassigned = allItems
                .Where(item => string.IsNullOrEmpty(item.AssignedAgent)
                    && !item.Labels.Contains(EngineeringTaskIssueManager.StatusImplementationComplete, StringComparer.OrdinalIgnoreCase)
                    && !item.Labels.Contains(EngineeringTaskIssueManager.StatusInProgress, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (unassigned.Count == 0)
            {
                Logger.LogDebug("{Role} {Name} self-claim: no unassigned engineering tasks available",
                    Identity.Role, Identity.DisplayName);
                return;
            }

            // Filter out tasks whose dependencies are not yet satisfied
            var ready = new List<PlatformWorkItem>();
            foreach (var item in unassigned)
            {
                if (await AreDependenciesSatisfiedAsync(item.Body, ct))
                    ready.Add(item);
            }

            // Wave-level gating: even if explicit "Depends On: #N" metadata is absent
            // (race with SE creating issues in two phases), block W1+ tasks until all
            // prior-wave tasks are closed. Fetch all engineering-task issues once and
            // build a wave→state map to avoid N+1 API calls.
            // IMPORTANT: ParseWave() defaults to "W1" when no metadata is found. To avoid
            // the race where no task has wave metadata yet, we treat tasks WITHOUT explicit
            // wave metadata in their body as ineligible for self-claim.
            if (ready.Count > 0)
            {
                var waveFiltered = new List<PlatformWorkItem>();
                foreach (var item in ready)
                {
                    // Check if wave metadata is explicitly present in the body
                    var hasExplicitWave = item.Body is not null
                        && item.Body.Contains("Wave:", StringComparison.OrdinalIgnoreCase);
                    if (!hasExplicitWave)
                    {
                        // No wave metadata yet — SE may still be updating issue bodies.
                        // Skip this task to avoid the creation-race.
                        Logger.LogDebug(
                            "{Role} {Name} self-claim: skipping {Title} — no wave metadata in body (SE may still be hydrating)",
                            Identity.Role, Identity.DisplayName, item.Title);
                        continue;
                    }

                    var itemWave = EngineeringTaskIssueManager.ParseWave(item.Body);
                    var waveNum = 0;
                    if (itemWave.StartsWith('W') && int.TryParse(itemWave.AsSpan(1), out var parsed))
                        waveNum = parsed;

                    if (waveNum <= 0)
                    {
                        waveFiltered.Add(item); // W0 is always eligible
                        continue;
                    }

                    // Check that all prior-wave tasks in allItems are closed
                    var priorWaveBlocking = allItems.Any(other =>
                    {
                        if (other.Body is null || !other.Body.Contains("Wave:", StringComparison.OrdinalIgnoreCase))
                            return true; // Task without wave metadata = assume prior-wave, block
                        var otherWave = EngineeringTaskIssueManager.ParseWave(other.Body);
                        var otherWaveNum = 0;
                        if (otherWave.StartsWith('W') && int.TryParse(otherWave.AsSpan(1), out var op))
                            otherWaveNum = op;
                        return otherWaveNum < waveNum
                            && otherWaveNum >= 0
                            && !other.State.Equals("closed", StringComparison.OrdinalIgnoreCase);
                    });

                    if (!priorWaveBlocking)
                        waveFiltered.Add(item);
                    else
                        Logger.LogDebug(
                            "{Role} {Name} self-claim: skipping {Title} ({Wave}) — prior wave tasks still open",
                            Identity.Role, Identity.DisplayName, item.Title, itemWave);
                }
                ready = waveFiltered;
            }

            if (ready.Count == 0)
            {
                Logger.LogDebug("{Role} {Name} self-claim: {Total} unassigned tasks but all have unmet dependencies",
                    Identity.Role, Identity.DisplayName, unassigned.Count);
                return;
            }

            // Prefer tasks matching our capabilities
            var capabilityKeywords = ExtractCapabilityKeywords(Definition.Capabilities);

            // Generalized peer-deferral: for each candidate task, compute MY match score AND
            // the BEST peer match score from all *currently-alive* specialist engineers in the team.
            // If a peer scores strictly higher, defer — they should claim it. If we tie or win,
            // we're eligible. This replaces project-specific keyword whitelists (art/sprite/etc)
            // with a fully data-driven rule that works for any specialization defined via
            // capabilities (security, database, frontend, art, compliance, whatever).
            //
            // Why this works:
            //  - SME capability keywords ARE what describe the role's domain
            //  - The same scoring function operates on every (agent, task) pair
            //  - Generalist SE workers have empty caps, score 0 on everything, defer to any
            //    specialist with score >= 1 — preserving "specialists first, generalists fill gaps"
            //  - Ties race naturally (first idle-loop to grab the GitHub label wins)
            //  - When the best-scoring peer is busy, they don't claim either — eventually a
            //    later iteration with stale "busy" state or an SE Leader LLM route picks it up
            var peerKeywordSets = CollectPeerCapabilityKeywords();

            int ScoreTask(IEnumerable<string> keywords, PlatformWorkItem item)
            {
                if (!keywords.Any()) return 0;
                var text = $"{item.Title} {item.Body}".ToLowerInvariant();
                return keywords.Count(kw => text.Contains(kw));
            }

            var rankedMatches = ready
                .Select(item =>
                {
                    var myScore = ScoreTask(capabilityKeywords, item);
                    var bestPeerScore = peerKeywordSets.Count == 0
                        ? 0
                        : peerKeywordSets.Max(peerKws => ScoreTask(peerKws, item));
                    return new
                    {
                        Item = item,
                        MyScore = myScore,
                        BestPeerScore = bestPeerScore,
                    };
                })
                .Where(m => capabilityKeywords.Count == 0 || m.MyScore > 0)       // skip if zero overlap
                .Where(m => m.MyScore >= m.BestPeerScore)                          // defer to strictly better peer
                .OrderByDescending(m => m.MyScore)
                .ToList();

            if (rankedMatches.Count == 0)
            {
                Logger.LogInformation(
                    "{Role} {Name} self-claim: no eligible unassigned tasks " +
                    "(capabilities=[{Caps}], peer-specialist-count={PeerCount}, available-tasks={Available}). " +
                    "Either zero keyword overlap, or a higher-scoring peer should claim instead.",
                    Identity.Role, Identity.DisplayName,
                    string.Join(", ", Definition.Capabilities),
                    peerKeywordSets.Count, ready.Count);
                return;
            }

            var bestMatch = rankedMatches.First().Item;

            // Atomic claim check — prevent duplicate claims across agents
            var cleanTitle = bestMatch.Title.Contains(':')
                ? bestMatch.Title[(bestMatch.Title.IndexOf(':') + 1)..].Trim()
                : bestMatch.Title;

            if (!TryClaimTask(bestMatch.Number, cleanTitle))
                return; // another agent already claimed this task

            // Self-assign: update the issue title to claim it
            // TODO(2.8): Route through EngineeringTaskIssueManager.AssignTaskAsync for safe label writes
            var newTitle = $"{Identity.DisplayName}: {cleanTitle}";
            var newLabels = bestMatch.Labels.ToList();
            if (!newLabels.Contains(EngineeringTaskIssueManager.StatusAssigned, StringComparer.OrdinalIgnoreCase))
                newLabels.Add(EngineeringTaskIssueManager.StatusAssigned);

            await WorkItemService.UpdateAsync(bestMatch.Number, title: newTitle, labels: newLabels, ct: ct);

            // Enqueue as an assignment so the base loop picks it up next iteration
            AssignmentQueue.Enqueue(new IssueAssignmentMessage
            {
                FromAgentId = Identity.Id, // Self-assigned
                ToAgentId = Identity.Id,
                IssueNumber = bestMatch.Number,
                IssueTitle = cleanTitle,
                Complexity = "Medium", // Default — exact complexity is in the issue body
                MessageType = "IssueAssignment"
            });

            _idleLoopCount = 0;
            Logger.LogInformation(
                "{Role} {Name} self-claimed task #{IssueNumber}: {Title} (idle for {Loops} loops)",
                Identity.Role, Identity.DisplayName, bestMatch.Number, cleanTitle, _idleLoopCount);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Role} {Name} self-claim failed", Identity.Role, Identity.DisplayName);
        }
    }
}
