using System.Collections.Concurrent;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.Agents.Reasoning;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub.Models;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Agents;

public class ResearcherAgent : AgentBase
{
    private readonly AgentPlatformServices _platform;
    private readonly AgentWorkspaceServices _workspace;
    private readonly DecisionGateService? _decisionGate;
    private readonly IDecisionLog? _decisionLog;

    private readonly Queue<ResearchDirective> _researchQueue = new();
    private string? _lastDesignSection;

    public ResearcherAgent(
        AgentIdentity identity,
        AgentCoreServices core,
        AgentPlatformServices platform,
        AgentWorkspaceServices workspace,
        ILogger<ResearcherAgent> logger,
        DecisionGateService? decisionGate = null,
        IDecisionLog? decisionLog = null)
        : base(identity, core, logger)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _decisionGate = decisionGate;
        _decisionLog = decisionLog;
    }

    private string EffectiveBranch => _platform.BranchProvider?.EffectiveBranch ?? Core!.Config.Project.DefaultBranch;

    protected override Task OnInitializeAsync(CancellationToken ct)
    {
        Subscribe<TaskAssignmentMessage>(HandleTaskAssignmentAsync);

        Logger.LogInformation("Researcher agent initialized, awaiting research directives");
        return Task.CompletedTask;
    }

    protected override async Task RunAgentLoopAsync(CancellationToken ct)
    {
        UpdateStatus(AgentStatus.Idle, "Waiting for research directives from PM");

        // B1 mini-reset bootstrap: even if no research directive ever arrives (because
        // PM considers research "already done" on a mini-reset with preserved Research.md),
        // ensure design screenshots exist so PM's visual-diff review has an anchor.
        // Safe no-op when screenshots are already present.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(5000, ct); // let repo clone finish
                await EnsureDesignScreenshotsPresentAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Startup EnsureDesignScreenshotsPresentAsync failed (non-fatal)");
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            await WaitIfPausedAsync(ct);
            ResearchDirective? currentDirective = null;
            try
            {
                if (_researchQueue.TryDequeue(out var directive))
                {
                    currentDirective = directive;

                    // Idempotency: check if this topic was already researched
                    var existingDoc = await Core.ProjectFiles.GetResearchDocAsync(ct);
                    if (existingDoc.Contains($"## {directive.Topic}", StringComparison.OrdinalIgnoreCase) ||
                        existingDoc.Contains($"# {directive.Topic}", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogInformation(
                            "Research for '{Topic}' already exists in Research.md, skipping",
                            directive.Topic);
                        currentDirective = null; // Don't re-enqueue on success

                        Core.ReasoningLog!.Log(new AgentReasoningEvent
                        {
                            AgentId = Identity.Id,
                            AgentDisplayName = Identity.DisplayName,
                            EventType = AgentReasoningEventType.Decision,
                            Phase = "Research",
                            Summary = $"Research for '{directive.Topic}' already exists — skipping",
                            Detail = "Found existing research section in Research.md matching this topic. Ensuring design screenshots and signaling completion."
                        });

                        // B1: even when Research.md already exists (e.g., preserved across mini-resets),
                        // ensure design screenshots are present in the repo — downstream PM review depends on them.
                        try { await EnsureDesignScreenshotsPresentAsync(ct); }
                        catch (Exception ex) { Logger.LogDebug(ex, "EnsureDesignScreenshotsPresentAsync failed (non-fatal)"); }

                        // Still signal completion so downstream agents aren't stuck
                        await PublishStatusAsync("ResearchComplete", AgentStatus.Idle,
                            details: $"Research already complete for: {directive.Topic}", ct: ct);
                    }
                    else
                    {
                        // Use the issue number passed directly from the PM's TaskAssignment
                        // instead of fragile title-based searching that could match wrong issues
                        int? relatedIssue = directive.IssueNumber;
                        if (!relatedIssue.HasValue)
                        {
                            // Fallback: search by title if PM didn't pass the number
                            try
                            {
                                var issues = await _platform.WorkItemService.ListOpenAsync(ct);
                                var matchingIssue = issues.FirstOrDefault(i =>
                                    i.Title.Contains("Research", StringComparison.OrdinalIgnoreCase) &&
                                    i.Title.Contains(directive.Topic, StringComparison.OrdinalIgnoreCase));
                                relatedIssue = matchingIssue?.Number;
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning(ex, "Could not find related issue for research topic");
                            }
                        }

                        // Create the PR upfront so it's visible immediately
                        UpdateStatus(AgentStatus.Working, "📝 Creating research PR");
                        string? createPrStepId = null;
                        try { createPrStepId = Core.TaskTracker!.BeginStep(Identity.Id, directive.TaskId, "Create research PR", "Opening PR for Research.md", Identity.ModelTier); } catch { }
                        var researchPath = Core.ProjectFiles.ResolvePath("Research.md");
                        var pr = await _platform.PrWorkflow.OpenDocumentPRAsync(
                            Identity.DisplayName,
                            researchPath,
                            $"Research findings for {directive.Topic}",
                            $"Research findings covering: {directive.Topic}",
                            relatedIssue,
                            ct);
                        // Surface the doc PR on the dashboard (agent-card-show-pr todo).
                        CurrentPrNumber = pr.Number;
                        try { if (createPrStepId is not null) Core.TaskTracker!.CompleteStep(createPrStepId); } catch { }

                        // Resume-aware: check if gate is already pending/approved from a prior run
                        var gateStatus = await Core.GateCheck.GetGateStatusAsync(
                            GateIds.ResearchFindings, pr.Number, ct);

                        string? updatedDoc = null;

                        if (gateStatus == GateStatus.Approved)
                        {
                            // Gate was already approved (PR may already be merged)
                            Logger.LogInformation("Research gate already approved on PR #{Number}, skipping research", pr.Number);
                            LogActivity("task", $"⏩ Research gate already approved on PR #{pr.Number}, resuming");
                        }
                        else if (gateStatus == GateStatus.AwaitingApproval)
                        {
                            // Gate is waiting for human — skip AI work, go straight to waiting
                            Logger.LogInformation("Research gate already pending on PR #{Number}, skipping to gate wait", pr.Number);
                            LogActivity("task", $"⏩ Research gate already pending on PR #{pr.Number}, resuming wait");
                        }
                        else
                        {
                            // Normal path: do the AI research work
                            UpdateStatus(AgentStatus.Working, $"Researching: {directive.Topic}");
                            AgentCallContext.CurrentCallContext = $"Researching: {directive.Topic}";
                            Logger.LogInformation("Starting research on: {Topic}", directive.Topic);
                            LogActivity("task", $"🔬 Starting research on: {directive.Topic}");

                            string? researchStepId = null;
        try { researchStepId = Core.TaskTracker!.BeginStep(Identity.Id, directive.TaskId, "AI research", $"Conducting research on: {directive.Topic}", Identity.ModelTier); } catch { }
                            var research = await ConductResearchAsync(directive, researchStepId, ct);
                            try { if (researchStepId is not null) Core.TaskTracker!.CompleteStep(researchStepId); } catch { }

                            // Build the full Research.md content (design section was cached during research)
                            var existingContent = await Core.ProjectFiles.GetResearchDocAsync(ct);
                            var newSection = FormatResearchSection(directive.Topic, research);
                            updatedDoc = existingContent.TrimEnd() + "\n\n" + newSection;
                            if (!string.IsNullOrWhiteSpace(_lastDesignSection))
                                updatedDoc += "\n\n" + _lastDesignSection;
                            updatedDoc += "\n";
                        }

                        // Commit document to PR so reviewers can see it before the gate
                        if (updatedDoc is not null && !pr.IsMerged)
                        {
                            LogActivity("task", "📝 Committing Research.md to PR");
                            UpdateStatus(AgentStatus.Working, "Committing Research.md for review");
                            string? commitStepId = null;
                            try { commitStepId = Core.TaskTracker!.BeginStep(Identity.Id, directive.TaskId, "Commit Research.md", "Committing research findings to PR"); } catch { }
                            await _platform.PrWorkflow.CommitDocumentToPRAsync(
                                pr, researchPath, updatedDoc,
                                $"Add research findings: {directive.Topic}", ct);
                            try { if (commitStepId is not null) Core.TaskTracker!.CompleteStep(commitStepId); } catch { }
                        }

                        // === Gate: ResearchFindings — human reviews before merge ===
                        if (gateStatus != GateStatus.Approved)
                        {
                            string? gateStepId = null;
                            try { gateStepId = Core.TaskTracker!.BeginStep(Identity.Id, directive.TaskId, "Human gate review", $"Awaiting human approval on PR #{pr.Number}"); } catch { }
                            try { if (gateStepId is not null) Core.TaskTracker!.SetStepWaiting(gateStepId); } catch { }
                            var maxRevisions = 3;
                            for (var revision = 0; revision < maxRevisions; revision++)
                            {
                                var gateWait = await WaitForHumanGateAsync(
                                    GateIds.ResearchFindings,
                                    $"Research findings for '{directive.Topic}' ready for review",
                                    pr.Number, ct: ct);

                                if (!gateWait.WasRejected)
                                    break;

                                // Human requested changes — revise the research
                                Logger.LogInformation(
                                    "Research gate rejected on PR #{Number}, revision {Rev}: {Feedback}",
                                    pr.Number, revision + 1, gateWait.Feedback);
                                LogActivity("task", $"📝 Revising research based on feedback: {gateWait.Feedback}");
                                UpdateStatus(AgentStatus.Working, $"Revising research (attempt {revision + 2})");

                                var revisedDoc = await ReviseResearchAsync(
                                    directive, gateWait.Feedback!, ct);

                                if (revisedDoc is not null && !pr.IsMerged)
                                {
                                    await _platform.PrWorkflow.CommitDocumentToPRAsync(
                                        pr, researchPath, revisedDoc,
                                        $"Revise research based on reviewer feedback (attempt {revision + 2})", ct);
                                }

                                // Remove human-approved label if present (reset the gate)
                                var currentPr = await _platform.PrService.GetAsync(pr.Number, ct);
                                if (currentPr is not null)
                                {
                                    await _platform.PrService.RemoveLabelAsync(pr.Number, "human-approved", ct);
                                    await _platform.PrService.AddLabelAsync(pr.Number, "awaiting-human-review", ct);
                                }

                                await _platform.ReviewService.AddCommentAsync(pr.Number,
                                    $"📝 **Revised** based on your feedback:\n\n> {gateWait.Feedback}\n\nPlease review the updated Research.md.", ct);
                            }
                            try { if (gateStepId is not null) Core.TaskTracker!.CompleteStep(gateStepId); } catch { }
                        }

                        // Merge after gate approval (skip if PR already merged)
                        if (!pr.IsMerged)
                        {
                            LogActivity("task", "🔗 Merging Research.md PR");
                            UpdateStatus(AgentStatus.Working, "Merging Research.md PR");
                            await _platform.PrWorkflow.MergeDocumentPRAsync(
                                pr, Identity.DisplayName, researchPath, ct);
                        }

                        Logger.LogInformation("Research.md PR created and merged for '{Topic}'", directive.Topic);
                        LogActivity("task", $"✅ Research.md merged: {directive.Topic}");
                        // Doc PR finished — clear so the dashboard stops showing it as active.
                        CurrentPrNumber = null;
                        await RememberAsync(MemoryType.Action,
                            $"Completed research and merged Research.md for '{directive.Topic}'",
                            $"Research on '{directive.Topic}' completed and merged", ct);
                        currentDirective = null; // Don't re-enqueue on success

                        // Explicitly close the related issue (don't rely on "Closes #X" in PR body)
                        if (relatedIssue.HasValue)
                        {
                            UpdateStatus(AgentStatus.Working, "✅ Closing research issue");
                            try
                            {
                                await _platform.WorkItemService.CloseAsync(relatedIssue.Value, ct);
                                Logger.LogInformation("Closed related issue #{IssueNumber}", relatedIssue.Value);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning(ex, "Failed to close issue #{IssueNumber}", relatedIssue.Value);
                            }
                        }

                        string? signalStepId = null;
                        try { signalStepId = Core.TaskTracker!.BeginStep(Identity.Id, directive.TaskId, "Signal PM", "Broadcasting ResearchComplete to all agents"); } catch { }
                        await PublishStatusAsync("ResearchComplete", AgentStatus.Online,
                            details: $"Research complete: {directive.Topic}",
                            currentTask: directive.TaskId, ct: ct);
                        try { if (signalStepId is not null) Core.TaskTracker!.CompleteStep(signalStepId); } catch { }

                        Logger.LogInformation(
                            "Research complete for task {TaskId}: {Topic}",
                            directive.TaskId, directive.Topic);
                    }
                }
                else
                {
                    UpdateStatus(AgentStatus.Idle, "Waiting for research directives");
                    await RefreshDiagnosticWithMemoryAsync(ct);
                    await WaitForWakeOrTimeoutAsync(TimeSpan.FromSeconds(5), ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Research loop error, will retry after delay");
                RecordError($"Research error: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, ex);
                // Don't leave the dashboard showing a stale doc PR while we're in error recovery.
                CurrentPrNumber = null;
                if (currentDirective is not null)
                {
                    const int maxRetries = 3;
                    if (currentDirective.RetryCount < maxRetries)
                    {
                        Logger.LogInformation("Re-enqueueing failed research directive (attempt {Attempt}/{Max}): {Topic}",
                            currentDirective.RetryCount + 1, maxRetries, currentDirective.Topic);
                        _researchQueue.Enqueue(currentDirective with { RetryCount = currentDirective.RetryCount + 1 });
                    }
                    else
                    {
                        Logger.LogError("Research directive '{Topic}' failed after {Max} retries, giving up", currentDirective.Topic, maxRetries);
                        RecordError($"Research gave up after {maxRetries} retries: {currentDirective.Topic}");
                    }
                }
                UpdateStatus(AgentStatus.Working, $"Recovering from error, will retry");
                try { await Task.Delay(15000, ct); } // Wait 15s before retry
                catch (OperationCanceledException) { break; }
            }
        }

        UpdateStatus(AgentStatus.Offline, "Researcher loop exited");
    }

    #region Message Handlers

    private Task HandleTaskAssignmentAsync(TaskAssignmentMessage message, CancellationToken ct)
    {
        Logger.LogInformation(
            "Received research directive from {From}: {Title}",
            message.FromAgentId, message.Title);

        _researchQueue.Enqueue(new ResearchDirective
        {
            TaskId = message.TaskId,
            Topic = message.Title,
            Description = message.Description,
            IssueNumber = message.IssueNumber
        });

        return Task.CompletedTask;
    }

    #endregion

    #region Research Logic

    private async Task<ResearchResult> ConductResearchAsync(
        ResearchDirective directive, string? trackingStepId, CancellationToken ct)
    {
        // Quick mode: produce a minimal 1-paragraph research summary for fast testing
        if (Core.Config.Project.QuickDocumentCreation)
        {
            LogActivity("research", "🤖 Quick-mode research generation");
            Logger.LogInformation("QuickDocumentCreation: producing minimal Research.md for '{Topic}'", directive.Topic);
            var qKernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var qChat = qKernel.GetRequiredService<IChatCompletionService>();
            var qHistory = CreateChatHistory();

            var quickVars = new Dictionary<string, string>
            {
                ["project_description"] = Core.Config.Project.Description,
                ["tech_stack"] = Core.Config.Project.TechStack,
                ["topic"] = directive.Topic
            };

            var quickSys = await Core.PromptService!.RenderAsync("researcher/quick-system", quickVars, ct)
                ?? "You are a technical researcher. Produce a brief, 1-paragraph research summary.";
            qHistory.AddSystemMessage(quickSys);

            var quickUser = await Core.PromptService!.RenderAsync("researcher/quick-user", quickVars, ct)
                ?? $"Project: {Core.Config.Project.Description}\nTech Stack: {Core.Config.Project.TechStack}\n" +
                   $"Topic: {directive.Topic}\n\n" +
                   "Write ONE concise paragraph summarizing the key technology recommendations for this project. " +
                   "Be specific about libraries and patterns. Keep it under 150 words.";
            qHistory.AddUserMessage(quickUser);
            var qResponse = await qChat.GetChatMessageContentsAsync(qHistory, cancellationToken: ct);
            var quickText = string.Join("", qResponse.Select(r => r.Content ?? ""));
            return new ResearchResult
            {
                Summary = quickText,
                DetailedAnalysis = quickText,
                KeyFindings = new List<string> { quickText }
            };
        }

        var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        // Enable agentic mode so the Researcher can explore the actual project codebase.
        // DocumentGenerationMode ensures the response is the full Research.md, not a brief summary.
        // NOTE: Manually disposed before self-assessment to prevent the assessment from
        // inheriting AgenticAllowAll+DocumentGenerationMode (which causes 30min+ stuck exploration).
        var projectPath = Core.Config.Workspace.LocalCheckoutPath;
        var _agenticScope = !string.IsNullOrEmpty(projectPath) && Directory.Exists(projectPath)
            ? AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                AgenticAllowAll: true,
                DocumentGenerationMode: true,
                OverrideWorkingDirectory: projectPath))
            : null;

        // Scan for design reference files FIRST so we can include them in research context
        UpdateStatus(AgentStatus.Working, "📁 Scanning repository for design references");
        LogActivity("research", "🔍 Scanning for design reference files");
        var designContext = await ScanForDesignReferencesAsync(ct);
        _lastDesignSection = designContext; // Cache for appending to Research.md later

        UpdateStatus(AgentStatus.Working, "📋 Gathering repository context for research");
        var history = CreateChatHistory();
        var memoryContext = await GetMemoryContextAsync(ct: ct);

        // Build system prompt from template with fallback
        var sysVars = new Dictionary<string, string>
        {
            ["tech_stack"] = Core.Config.Project.TechStack,
            ["memory_context"] = string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}",
            ["design_context"] = "",
            ["unanswered_decisions"] = DecisionContextBuilder.BuildUnansweredDecisionsContext(
                Core.Config.UnansweredDecisionQuestions)
        };

        // If we found design files, add them via the design-reference template
        if (!string.IsNullOrWhiteSpace(designContext))
        {
            var designPrompt = await Core.PromptService!.RenderAsync("researcher/design-reference",
                new Dictionary<string, string> { ["design_context"] = designContext }, ct);
            sysVars["design_context"] = designPrompt ?? $"\n\n## VISUAL DESIGN REFERENCE\n{designContext}";
        }

        var systemPrompt = await Core.PromptService!.RenderAsync("researcher/full-system", sysVars, ct);
        if (systemPrompt is null)
        {
            // Hardcoded fallback during migration
            systemPrompt = "You are a senior technical researcher on a software development team. " +
                "Your job is to perform deep, thorough research on assigned topics and produce structured, " +
                "actionable findings that architects and engineers can build from directly. " +
                "Go beyond surface-level recommendations — provide specific tools, version numbers, " +
                "architecture patterns, trade-offs, and real-world considerations. " +
                "Focus on practical, opinionated recommendations backed by reasoning.\n\n" +
                $"IMPORTANT: The project's technology stack has already been decided: **{Core.Config.Project.TechStack}**. " +
                "Your research MUST target this stack. Recommend libraries, patterns, and tools that are " +
                "native to or compatible with this stack. Do NOT recommend alternative tech stacks — " +
                "the decision is final." +
                (string.IsNullOrEmpty(memoryContext) ? "" : $"\n\n{memoryContext}");
            if (!string.IsNullOrWhiteSpace(designContext))
            {
                systemPrompt += "\n\n## VISUAL DESIGN REFERENCE\n" +
                    "The repository contains visual design reference files that define the exact UI to be built. " +
                    "Your research MUST include technology recommendations for implementing this specific design. " +
                    "Consider: CSS layout techniques needed (Grid, Flexbox), SVG/charting libraries for any " +
                    "visualizations, color theming approaches, responsive design strategies, and component " +
                    "architecture that maps to the design's visual sections.\n\n" +
                    designContext;
            }
            Logger.LogWarning("Using hardcoded fallback for researcher/full-system template");
        }

        history.AddSystemMessage(systemPrompt);

        var useSinglePass = Core.Config.CopilotCli.SinglePassMode;
        string synthesisContent;
        string detailedAnalysis;

        var researchVars = new Dictionary<string, string>
        {
            ["topic"] = directive.Topic,
            ["topic_description"] = directive.Description
        };

        if (useSinglePass)
        {
            // Single-pass: one comprehensive prompt instead of 3 turns
            LogActivity("research", "🤖 Calling AI for research (single-pass)");
            UpdateStatus(AgentStatus.Working, "Researching (single-pass)");
            var singlePassPrompt = await Core.PromptService!.RenderAsync("researcher/single-pass-research", researchVars, ct)
                ?? $"Research the following topic for our software project.\n\n" +
                   $"**Topic:** {directive.Topic}\n\n" +
                   $"**Context:**\n{directive.Description}\n\n" +
                   "Produce a comprehensive, structured research document with these sections:\n\n" +
                   "1. **Executive Summary** — Concise overview of findings and primary recommendation.\n" +
                   "2. **Key Findings** — Most important discoveries, one per bullet (prefixed with '- ').\n" +
                   "3. **Recommended Technology Stack** — Specific tools, frameworks, libraries with versions. " +
                   "Organize by layer (frontend, backend, database, infrastructure, testing).\n" +
                   "4. **Architecture Recommendations** — Patterns, data flow, structural decisions.\n" +
                   "5. **Security & Infrastructure** — Auth, hosting, deployment, operational concerns.\n" +
                   "6. **Risks & Trade-offs** — Technical risks, bottlenecks, mitigation strategies.\n" +
                   "7. **Open Questions** — Decisions needing stakeholder input.\n" +
                   "8. **Implementation Recommendations** — Phasing, MVP scope, quick wins.\n\n" +
                   "Use these exact section headers. Be specific, opinionated, and actionable. " +
                   "Include version numbers, compatibility notes, and real-world considerations.";
            history.AddUserMessage(singlePassPrompt);

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            try { if (trackingStepId is not null) { Core.TaskTracker!.RecordLlmCall(trackingStepId); Core.TaskTracker!.RecordSubStep(trackingStepId, "Single-pass research"); } } catch { }
            synthesisContent = response.Content ?? "";
            detailedAnalysis = synthesisContent;
        }
        else
        {
        // Turn 1: Break down the research topic into sub-questions
        LogActivity("research", "🤖 AI turn 1/3: Identifying sub-questions");
        UpdateStatus(AgentStatus.Working, "Researching (1/3): Identifying sub-questions");
        var turn1Prompt = await Core.PromptService!.RenderAsync("researcher/multi-turn-subquestions", researchVars, ct)
            ?? $"I need you to research the following topic for our software project.\n\n" +
               $"**Topic:** {directive.Topic}\n\n" +
               $"**Context:**\n{directive.Description}\n\n" +
               "Based on the context and any research guidance provided above, break this topic down " +
               "into 5-8 key sub-questions that need thorough investigation. " +
               "Prioritize them by impact on the project. " +
               "List them clearly, one per line, prefixed with a number.";
        history.AddUserMessage(turn1Prompt);

        var subQuestionsResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(subQuestionsResponse.Content ?? "");
        try { if (trackingStepId is not null) { Core.TaskTracker!.RecordLlmCall(trackingStepId); Core.TaskTracker!.RecordSubStep(trackingStepId, "Turn 1/3: Identifying sub-questions"); } } catch { }

        Logger.LogDebug("Research sub-questions identified for {Topic}", directive.Topic);

        // Turn 2: Deep-dive analysis of each sub-question
        LogActivity("research", "🤖 AI turn 2/3: Deep-dive analysis");
        UpdateStatus(AgentStatus.Working, "Researching (2/3): Deep-dive analysis");
        var turn2Prompt = await Core.PromptService!.RenderAsync("researcher/multi-turn-deepdive", new Dictionary<string, string>(), ct)
            ?? "Now provide a detailed, in-depth analysis for each sub-question you identified. " +
               "For each one, cover:\n" +
               "- **Key findings** — What did you discover? Be specific.\n" +
               "- **Tools, libraries, or technologies** — Name specific packages with version numbers.\n" +
               "- **Trade-offs and alternatives** — What are the pros/cons? What did you consider and reject?\n" +
               "- **Concrete recommendations** — What should the team use and why?\n" +
               "- **Evidence and reasoning** — Why is this the right choice for this specific project?\n\n" +
               "Be thorough and specific. Include version numbers, compatibility notes, " +
               "community health indicators, and real-world considerations. " +
               "If relevant, mention what similar projects in the industry have chosen.";
        history.AddUserMessage(turn2Prompt);

        var analysisResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        history.AddAssistantMessage(analysisResponse.Content ?? "");
        try { if (trackingStepId is not null) { Core.TaskTracker!.RecordLlmCall(trackingStepId); Core.TaskTracker!.RecordSubStep(trackingStepId, "Turn 2/3: Deep-dive analysis"); } } catch { }

        Logger.LogDebug("Detailed analysis complete for {Topic}", directive.Topic);

        // Turn 3: Synthesize into structured Research.md output
        LogActivity("research", "🤖 AI turn 3/3: Synthesizing findings");
        UpdateStatus(AgentStatus.Working, "Researching (3/3): Synthesizing findings");
        var turn3Prompt = await Core.PromptService!.RenderAsync("researcher/multi-turn-synthesis", new Dictionary<string, string>(), ct)
            ?? "Now synthesize all your research into a comprehensive, structured document with these sections:\n\n" +
               "1. **Executive Summary** — A concise overview of findings and primary recommendation (3-5 sentences).\n" +
               "2. **Key Findings** — The most important discoveries, one per bullet (prefixed with '- ').\n" +
               "3. **Recommended Technology Stack** — Specific tools, frameworks, and libraries with version numbers. " +
               "Organize by layer (frontend, backend, database, infrastructure, testing, etc.).\n" +
               "4. **Architecture Recommendations** — Patterns, data flow, and structural decisions.\n" +
               "5. **Security & Infrastructure** — Auth, hosting, deployment, and operational concerns.\n" +
               "6. **Risks & Trade-offs** — Technical risks, potential bottlenecks, and mitigation strategies.\n" +
               "7. **Open Questions** — Decisions that need stakeholder input or further investigation.\n" +
               "8. **Implementation Recommendations** — Phasing, MVP scope, and quick wins.\n\n" +
               "Use these exact section headers. Be specific, opinionated, and actionable. " +
               "The Architect and Engineers will build directly from this document.";
        history.AddUserMessage(turn3Prompt);

        var synthesisResponse = await chat.GetChatMessageContentAsync(
            history, cancellationToken: ct);
        synthesisContent = synthesisResponse.Content ?? "";
        detailedAnalysis = analysisResponse.Content ?? "";
        try { if (trackingStepId is not null) { Core.TaskTracker!.RecordLlmCall(trackingStepId); Core.TaskTracker!.RecordSubStep(trackingStepId, "Turn 3/3: Synthesizing findings"); } } catch { }

        } // end else (multi-turn)

        // Self-assessment: assess and refine the research document
        // Dispose agentic scope BEFORE self-assessment — assessment should NOT explore the
        // codebase or run in DocumentGenerationMode (it just evaluates the document text).
        _agenticScope?.Dispose();
        _agenticScope = null;

        UpdateStatus(AgentStatus.Working, "🤖 Self-assessing research quality");
        string? assessStepId = null;
        try { assessStepId = Core.TaskTracker!.BeginStep(Identity.Id, directive.TaskId, "Self-assessment & refinement", "Assessing and refining research output", Identity.ModelTier); } catch { }
        Core.ReasoningLog!.Log(new AgentReasoningEvent
        {
            AgentId = Identity.Id,
            AgentDisplayName = Identity.DisplayName,
            EventType = AgentReasoningEventType.Generating,
            Phase = "Research",
            Summary = $"Research document generated for '{directive.Topic}'",
            Iteration = 0,
        });

        var criteria = AssessmentCriteria.GetForRole(Identity.Role);
        if (criteria is not null)
        {
            synthesisContent = await Core.SelfAssessment!.AssessAndRefineAsync(
                Identity.Id,
                Identity.DisplayName,
                Identity.Role,
                "Research",
                synthesisContent,
                criteria,
                $"Project: {Core.Config.Project.Description}\nTech Stack: {Core.Config.Project.TechStack}\nTopic: {directive.Topic}",
                chat,
                ct);
        }
        try { if (assessStepId is not null) Core.TaskTracker!.CompleteStep(assessStepId); } catch { }

        Logger.LogDebug("Research synthesis complete for {Topic}", directive.Topic);
        UpdateStatus(AgentStatus.Working, "💾 Saving research findings");

        // Extract and log any DECISION blocks from the research output
        if (_decisionLog is not null)
        {
            var decisions = DecisionBlockParser.ExtractDecisions(synthesisContent);
            foreach (var d in decisions)
            {
                if (_decisionGate is not null)
                {
                    await _decisionGate.ClassifyAndGateDecisionAsync(
                        Identity.Id, Identity.DisplayName,
                        "Research", d.Title,
                        $"Choice: {d.Choice}\nRationale: {d.Rationale}",
                        category: d.SourceQuestion is not null ? "WizardQuestion" : "TechnologyDecision",
                        modelTier: Identity.ModelTier, ct: ct);
                }
                else
                {
                    _decisionLog.Log(new AgentDecision
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        AgentId = Identity.Id,
                        AgentDisplayName = Identity.DisplayName,
                        Phase = "Research",
                        ImpactLevel = d.Impact,
                        Title = d.Title,
                        Rationale = $"Choice: {d.Choice}\nRationale: {d.Rationale}",
                        SourceQuestion = d.SourceQuestion,
                        Status = DecisionStatus.AutoApproved
                    });
                }
            }
            if (decisions.Count > 0)
            {
                Logger.LogInformation("Researcher logged {Count} decisions from research", decisions.Count);
                synthesisContent = DecisionBlockParser.StripDecisionBlocks(synthesisContent);
            }
        }

        await RememberAsync(MemoryType.Decision,
            $"Technology evaluation decisions for '{directive.Topic}'",
            TruncateForMemory(synthesisContent), ct);

        return ParseResearchResult(synthesisContent, detailedAnalysis);
    }

    internal static ResearchResult ParseResearchResult(string synthesis, string detailedAnalysis)
    {
        var summary = "";
        var keyFindings = new List<string>();
        var recommendedTools = new List<string>();
        var considerations = new List<string>();

        var currentSection = "";
        var lines = synthesis.Split('\n');
        var inCodeBlock = false;
        var prevLineBlank = false;

        foreach (var rawLine in lines)
        {
            // Track fenced code blocks — don't parse lines inside them as structure
            if (rawLine.TrimStart().StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                // Append the fence line to the last item in the current list so code blocks stay intact
                AppendToLastItem(currentSection, rawLine, keyFindings, recommendedTools, considerations, ref summary);
                prevLineBlank = false;
                continue;
            }

            if (inCodeBlock)
            {
                // Inside a code block — append raw line (preserve indentation) to current item
                AppendToLastItem(currentSection, rawLine, keyFindings, recommendedTools, considerations, ref summary);
                prevLineBlank = false;
                continue;
            }

            var line = rawLine.Trim();
            var isHeader = line.StartsWith('#') || (line.StartsWith("**") && line.EndsWith("**") && !line.StartsWith("**-"));

            if (isHeader)
            {
                var lowerLine = line.ToLowerInvariant();
                if (lowerLine.Contains("summary") || lowerLine.Contains("executive summary"))
                {
                    currentSection = "summary";
                    prevLineBlank = false;
                    continue;
                }
                if (lowerLine.Contains("key findings") || lowerLine.Contains("findings"))
                {
                    currentSection = "findings";
                    prevLineBlank = false;
                    continue;
                }
                if (lowerLine.Contains("recommended tool") || lowerLine.Contains("technology stack")
                    || lowerLine.Contains("recommended tech"))
                {
                    currentSection = "tools";
                    prevLineBlank = false;
                    continue;
                }
                if (lowerLine.Contains("architecture") && lowerLine.Contains("recommend"))
                {
                    currentSection = "tools"; // group with tech recommendations
                    prevLineBlank = false;
                    continue;
                }
                if (lowerLine.Contains("risk") || lowerLine.Contains("trade-off")
                    || lowerLine.Contains("consideration") || lowerLine.Contains("security")
                    || lowerLine.Contains("open question"))
                {
                    currentSection = "considerations";
                    prevLineBlank = false;
                    continue;
                }
                if (lowerLine.Contains("implementation") || lowerLine.Contains("phasing")
                    || lowerLine.Contains("mvp") || lowerLine.Contains("quick win"))
                {
                    currentSection = "findings"; // group implementation notes with findings
                    prevLineBlank = false;
                    continue;
                }
                // Unknown header — keep current section
                prevLineBlank = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                // Remember the blank line so the next continuation can be promoted
                // to a paragraph break rather than a single newline.
                prevLineBlank = true;
                continue;
            }

            // Lines starting with bullet markers are new items; continuation lines append
            var isBulletStart = line.StartsWith("- ") || line.StartsWith("* ")
                || (line.Length > 2 && char.IsDigit(line[0]) && line[1] == '.')
                || (line.Length > 3 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && line[2] == '.');
            var bulletContent = StripBulletPrefix(line);
            // Use \n\n when a blank line separated us from the previous content (paragraph break),
            // otherwise \n so multi-line bullet bodies, table rows, and sub-bullets keep their
            // line structure instead of being concatenated with spaces into one giant line.
            var continuationSeparator = prevLineBlank ? "\n\n" : "\n";
            prevLineBlank = false;

            switch (currentSection)
            {
                case "summary":
                    summary = string.IsNullOrEmpty(summary)
                        ? bulletContent
                        : $"{summary}{continuationSeparator}{bulletContent}";
                    break;
                case "findings":
                    if (isBulletStart || keyFindings.Count == 0)
                        keyFindings.Add(bulletContent);
                    else
                        keyFindings[^1] += continuationSeparator + bulletContent;
                    break;
                case "tools":
                    if (isBulletStart || recommendedTools.Count == 0)
                        recommendedTools.Add(bulletContent);
                    else
                        recommendedTools[^1] += continuationSeparator + bulletContent;
                    break;
                case "considerations":
                    if (isBulletStart || considerations.Count == 0)
                        considerations.Add(bulletContent);
                    else
                        considerations[^1] += continuationSeparator + bulletContent;
                    break;
            }
        }

        return new ResearchResult
        {
            Summary = summary,
            KeyFindings = keyFindings,
            RecommendedTools = recommendedTools,
            Considerations = considerations,
            DetailedAnalysis = detailedAnalysis
        };
    }

    /// <summary>
    /// Appends a raw line to the last item in the current section's list.
    /// Used for code block lines that should stay attached to the preceding bullet.
    /// </summary>
    private static void AppendToLastItem(
        string section, string rawLine,
        List<string> findings, List<string> tools, List<string> considerations,
        ref string summary)
    {
        switch (section)
        {
            case "summary":
                summary += "\n" + rawLine;
                break;
            case "findings":
                if (findings.Count > 0) findings[^1] += "\n" + rawLine;
                break;
            case "tools":
                if (tools.Count > 0) tools[^1] += "\n" + rawLine;
                break;
            case "considerations":
                if (considerations.Count > 0) considerations[^1] += "\n" + rawLine;
                break;
        }
    }

    private static string StripBulletPrefix(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- "))
            return trimmed[2..].Trim();
        if (trimmed.StartsWith("* "))
            return trimmed[2..].Trim();
        if (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.')
            return trimmed[2..].Trim();
        if (trimmed.Length > 3 && char.IsDigit(trimmed[0]) && char.IsDigit(trimmed[1]) && trimmed[2] == '.')
            return trimmed[3..].Trim();
        return trimmed;
    }

    #endregion

    #region Document Management

    private async Task AppendToResearchDocAsync(
        string topic, ResearchResult result, CancellationToken ct)
    {
        try
        {
            var existingDoc = await Core.ProjectFiles.GetResearchDocAsync(ct);

            var newSection = FormatResearchSection(topic, result);
            var updatedDoc = existingDoc.TrimEnd() + "\n\n" + newSection + "\n";

            await Core.ProjectFiles.UpdateResearchDocAsync(updatedDoc, ct);

            Logger.LogInformation("Appended research section for '{Topic}' to Research.md", topic);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to append research for '{Topic}' to Research.md", topic);
            throw;
        }
    }

    /// <summary>
    /// Revise research based on human reviewer feedback. Reads the current Research.md,
    /// sends it along with the feedback to the AI, and returns the revised document.
    /// </summary>
    private async Task<string?> ReviseResearchAsync(
        ResearchDirective directive, string feedback, CancellationToken ct)
    {
        var currentDoc = await Core.ProjectFiles.GetResearchDocAsync(ct);
        if (string.IsNullOrWhiteSpace(currentDoc))
        {
            Logger.LogWarning("No existing Research.md to revise");
            return null;
        }

        // CLI Edit Mode: write file locally, let Copilot CLI edit it surgically
        var tempDir = Path.Combine(Path.GetTempPath(), $"vdt-research-revision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "Research.md");
        await File.WriteAllTextAsync(filePath, currentDoc, ct);

        try
        {
            var kernel = Core.ModelRegistry.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = CreateChatHistory();

            // Include project description + wizard Q&A as reference context
            var projectDescription = Core.Config.Project.Description ?? "";

            history.AddSystemMessage(
                $"""
                You are a senior technical researcher revising Research.md based on human reviewer feedback.
                The project's technology stack is: **{Core.Config.Project.TechStack}**.

                ## Project Context (READ-ONLY reference — do NOT copy this into the document verbatim):
                {projectDescription}

                CRITICAL RULES:
                1. Use the file editing tools to make ONLY the changes the feedback requests.
                2. Do NOT rewrite or reorganize sections that the feedback does not mention.
                3. Do NOT remove existing content unless the feedback explicitly asks for removal.
                4. Preserve the tone, structure, and level of detail of the original document.
                5. Make surgical, minimal edits — change only what is necessary to address the feedback.
                6. The file Research.md is in your working directory. Edit it directly.
                7. Use the Project Context above to ensure technical accuracy of research findings, but do NOT restructure the document around it.
                """);
            history.AddUserMessage(
                $"""
                ## Reviewer Feedback:

                {feedback}

                Edit the file `Research.md` in your working directory to address ONLY the feedback above.
                Make minimal, surgical changes. Do not rewrite the whole file.
                """);

            using var scope = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
                AllowFileEdits: true,
                OverrideWorkingDirectory: tempDir));

            await chat.GetChatMessageContentAsync(history, cancellationToken: ct);

            if (!File.Exists(filePath))
            {
                Logger.LogWarning("CLI edit mode deleted Research.md — rejecting revision");
                return null;
            }

            var revised = await File.ReadAllTextAsync(filePath, ct);

            if (revised.TrimEnd() == currentDoc.TrimEnd())
            {
                Logger.LogInformation("CLI edit made no changes to Research.md");
                return null;
            }

            Logger.LogInformation("CLI edit revision of Research.md: {Original} → {Revised} chars",
                currentDoc.Length, revised.Length);
            return revised;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private static string FormatResearchSection(string topic, ResearchResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {topic}");
        sb.AppendLine();
        sb.AppendLine($"_Researched on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC_");
        sb.AppendLine();

        sb.AppendLine("### Summary");
        sb.AppendLine();
        sb.AppendLine(result.Summary);
        sb.AppendLine();

        if (result.KeyFindings.Count > 0)
        {
            sb.AppendLine("### Key Findings");
            sb.AppendLine();
            foreach (var finding in result.KeyFindings)
                sb.AppendLine($"- {finding}");
            sb.AppendLine();
        }

        if (result.RecommendedTools.Count > 0)
        {
            sb.AppendLine("### Recommended Tools & Technologies");
            sb.AppendLine();
            foreach (var tool in result.RecommendedTools)
                sb.AppendLine($"- {tool}");
            sb.AppendLine();
        }

        if (result.Considerations.Count > 0)
        {
            sb.AppendLine("### Considerations & Risks");
            sb.AppendLine();
            foreach (var consideration in result.Considerations)
                sb.AppendLine($"- {consideration}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(result.DetailedAnalysis))
        {
            sb.AppendLine("### Detailed Analysis");
            sb.AppendLine();
            sb.AppendLine(result.DetailedAnalysis);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string TruncateForMemory(string text, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxLength) return text;
        var cut = text[..maxLength];
        var lastPeriod = cut.LastIndexOf('.');
        return lastPeriod > maxLength / 2 ? cut[..(lastPeriod + 1)] : cut + "…";
    }

    /// <summary>
    /// Scan the repository for visual design reference files (.html, .htm, .png, .fig, .sketch)
    /// and return a formatted section describing them for Research.md.
    /// </summary>
    private async Task<string?> ScanForDesignReferencesAsync(CancellationToken ct)
    {
        try
        {
            var tree = await _platform.RepoContent.GetRepositoryTreeAsync(EffectiveBranch, ct);
            var designExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".html", ".htm"
            };
            var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp"
            };
            var designKeywords = new[] { "design", "mockup", "mock", "wireframe", "prototype", "concept", "reference" };

            var designFiles = new List<(string path, string type)>();

            foreach (var filePath in tree)
            {
                var fileName = Path.GetFileName(filePath).ToLowerInvariant();
                var ext = Path.GetExtension(filePath).ToLowerInvariant();

                // Skip files deep in src/ or node_modules/
                if (filePath.Contains("node_modules") || filePath.Contains("wwwroot/lib"))
                    continue;

                var nameHasDesignKeyword = designKeywords.Any(k => fileName.Contains(k));

                if (designExtensions.Contains(ext) && nameHasDesignKeyword)
                    designFiles.Add((filePath, "html-design"));
                else if (imageExtensions.Contains(ext) && nameHasDesignKeyword)
                    designFiles.Add((filePath, "image-design"));
                else if (ext == ".html" && !filePath.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
                    designFiles.Add((filePath, "html-root")); // HTML in root is likely a design reference
            }

            if (designFiles.Count == 0)
                return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## Visual Design References");
            sb.AppendLine();
            sb.AppendLine("The following design reference files were found in the repository. " +
                "These MUST be used as the canonical visual specification when building UI components.");
            sb.AppendLine();

            foreach (var (path, type) in designFiles)
            {
                sb.AppendLine($"### `{path}`");
                sb.AppendLine();

                if (type.StartsWith("html"))
                {
                    // Read HTML files to extract design details
                    var content = await _platform.RepoContent.GetFileContentAsync(path, EffectiveBranch, ct);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        // Extract key CSS patterns and layout structure
                        var cssClasses = ExtractCssPatterns(content);
                        var layoutDescription = ExtractLayoutStructure(content);

                        sb.AppendLine("**Type:** HTML Design Template");
                        sb.AppendLine();
                        if (!string.IsNullOrWhiteSpace(layoutDescription))
                        {
                            sb.AppendLine("**Layout Structure:**");
                            sb.AppendLine(layoutDescription);
                            sb.AppendLine();
                        }
                        if (!string.IsNullOrWhiteSpace(cssClasses))
                        {
                            sb.AppendLine("**Key CSS Patterns:**");
                            sb.AppendLine(cssClasses);
                            sb.AppendLine();
                        }
                        sb.AppendLine("<details><summary>Full HTML Source</summary>");
                        sb.AppendLine();
                        sb.AppendLine("```html");
                        sb.AppendLine(content.Length > 8000 ? content[..8000] + "\n<!-- truncated -->" : content);
                        sb.AppendLine("```");
                        sb.AppendLine("</details>");
                    }
                }
                else
                {
                    sb.AppendLine($"**Type:** Design Image — engineers should reference this file visually");
                }
                sb.AppendLine();
            }

            Logger.LogInformation("Found {Count} visual design reference files in repository", designFiles.Count);

            // Capture screenshots of HTML design files and commit to repo
            await CaptureDesignScreenshotsAsync(designFiles, sb, ct);

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to scan for design reference files");
            return null;
        }
    }

    /// <summary>
    /// B1: Ensure design screenshots exist in repo under docs/design-screenshots/.
    /// Called even when Research.md is already present (preserved across mini-resets),
    /// because without the rendered screenshots PM review cannot compare actual vs. target.
    /// Safe to call repeatedly; only renders/commits when missing.
    /// </summary>
    private async Task EnsureDesignScreenshotsPresentAsync(CancellationToken ct)
    {
        if (_workspace.PlaywrightRunner is null) return;

        IReadOnlyList<string> tree;
        try { tree = await _platform.RepoContent.GetRepositoryTreeAsync(EffectiveBranch, ct); }
        catch (Exception ex) { Logger.LogDebug(ex, "Could not read repo tree for design screenshot check"); return; }

        var htmlDesignFiles = tree
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".html" && ext != ".htm") return false;
                var lower = f.ToLowerInvariant();
                if (lower.StartsWith("src/")) return false;
                var name = Path.GetFileName(lower);
                return name.Contains("design") || name.Contains("mock") ||
                       name.Contains("wireframe") || name.Contains("concept") ||
                       name.Contains("prototype") || name.Contains("reference");
            })
            .ToList();

        if (htmlDesignFiles.Count == 0) return;

        var existingScreenshots = new HashSet<string>(
            tree.Where(f => f.StartsWith(Core.ProjectFiles.DesignScreenshotsPrefix, StringComparison.OrdinalIgnoreCase) &&
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileNameWithoutExtension(f)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var htmlPath in htmlDesignFiles)
        {
            var stem = Path.GetFileNameWithoutExtension(htmlPath);
            if (existingScreenshots.Contains(stem)) continue;

            try
            {
                var htmlContent = await _platform.RepoContent.GetFileContentAsync(htmlPath, EffectiveBranch, ct);
                if (string.IsNullOrWhiteSpace(htmlContent)) continue;

                var screenshotBytes = await _workspace.PlaywrightRunner.CaptureHtmlScreenshotAsync(
                    htmlContent, Core.Config.Workspace, ct: ct);
                if (screenshotBytes is null || screenshotBytes.Length == 0)
                {
                    Logger.LogWarning("B1: design screenshot capture returned empty for {Path}", htmlPath);
                    continue;
                }

                var screenshotPath = $"{Core.ProjectFiles.DesignScreenshotsPrefix}{stem}.png";
                var imageUrl = await _platform.RepoContent.CommitBinaryFileAsync(
                    screenshotPath, screenshotBytes,
                    $"Add design screenshot: {stem}.png (rendered from {htmlPath})",
                    EffectiveBranch, ct);

                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    Logger.LogInformation("B1: committed design screenshot {Path} from {Source}", screenshotPath, htmlPath);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "B1: failed to ensure design screenshot for {Path}", htmlPath);
            }
        }
    }

    /// <summary>
    /// Capture PNG screenshots of HTML design files and commit them to the repo.
    /// These screenshots are embedded in Research.md, PMSpec, Architecture, and issues
    /// so all agents (and the human reviewer) see the exact intended visual output.
    /// </summary>
    private async Task CaptureDesignScreenshotsAsync(
        List<(string path, string type)> designFiles,
        System.Text.StringBuilder sb,
        CancellationToken ct)
    {
        if (_workspace.PlaywrightRunner is null)
        {
            Logger.LogDebug("PlaywrightRunner not available, skipping design screenshots");
            return;
        }

        var htmlDesignFiles = designFiles.Where(f => f.type.StartsWith("html")).ToList();
        if (htmlDesignFiles.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("## Design Visual Previews");
        sb.AppendLine();
        sb.AppendLine("The following screenshots were rendered from the HTML design reference files. " +
            "Engineers MUST match these visuals exactly.");
        sb.AppendLine();

        var screenshotCount = 0;
        foreach (var (path, _) in htmlDesignFiles)
        {
            try
            {
                var htmlContent = await _platform.RepoContent.GetFileContentAsync(path, EffectiveBranch, ct);
                if (string.IsNullOrWhiteSpace(htmlContent)) continue;

                var screenshotBytes = await _workspace.PlaywrightRunner.CaptureHtmlScreenshotAsync(
                    htmlContent, Core.Config.Workspace, ct: ct);
                if (screenshotBytes is null || screenshotBytes.Length == 0) continue;

                // Commit the screenshot to the repo
                var fileName = Path.GetFileNameWithoutExtension(path);
                var screenshotPath = $"{Core.ProjectFiles.DesignScreenshotsPrefix}{fileName}.png";
                var imageUrl = await _platform.RepoContent.CommitBinaryFileAsync(
                    screenshotPath, screenshotBytes,
                    $"Add design screenshot: {fileName}.png (rendered from {path})",
                    EffectiveBranch, ct);

                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    sb.AppendLine($"### {Path.GetFileName(path)}");
                    sb.AppendLine();
                    sb.AppendLine($"![{fileName} design]({imageUrl})");
                    sb.AppendLine();
                    sb.AppendLine($"*Rendered from `{path}` at 1920×1080*");
                    sb.AppendLine();
                    screenshotCount++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to capture design screenshot for {Path}", path);
            }
        }

        if (screenshotCount > 0)
        {
            Logger.LogInformation("Captured and committed {Count} design screenshots", screenshotCount);
        }
    }

    /// <summary>
    /// Extract key CSS patterns from HTML design file (grid layouts, color schemes, font families).
    /// </summary>
    private static string ExtractCssPatterns(string html)
    {
        var patterns = new List<string>();

        // Extract grid layouts
        if (html.Contains("display:grid") || html.Contains("display: grid"))
            patterns.Add("- Uses CSS Grid layout");
        if (html.Contains("display:flex") || html.Contains("display: flex"))
            patterns.Add("- Uses Flexbox layout");

        // Extract color scheme from CSS
        var colorMatches = System.Text.RegularExpressions.Regex.Matches(html, @"(?:color|background|border-color|fill)\s*:\s*(#[0-9A-Fa-f]{3,8})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var colors = colorMatches.Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .Distinct()
            .Take(15)
            .ToList();
        if (colors.Count > 0)
            patterns.Add($"- Color palette: {string.Join(", ", colors)}");

        // Extract font families
        var fontMatch = System.Text.RegularExpressions.Regex.Match(html, @"font-family\s*:\s*'?([^;'""]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fontMatch.Success)
            patterns.Add($"- Font: {fontMatch.Groups[1].Value.Trim()}");

        // Extract grid template columns
        var gridColMatch = System.Text.RegularExpressions.Regex.Match(html, @"grid-template-columns\s*:\s*([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (gridColMatch.Success)
            patterns.Add($"- Grid columns: `{gridColMatch.Groups[1].Value.Trim()}`");

        // Extract viewport sizing
        if (html.Contains("1920px") || html.Contains("1080px"))
            patterns.Add("- Designed for 1920×1080 screenshot resolution");

        return patterns.Count > 0 ? string.Join("\n", patterns) : "";
    }

    /// <summary>
    /// Extract high-level layout structure from HTML by analyzing major div classes and sections.
    /// </summary>
    private static string ExtractLayoutStructure(string html)
    {
        var sections = new List<string>();

        // Look for semantic class names that describe layout sections
        var classMatches = System.Text.RegularExpressions.Regex.Matches(html,
            @"class=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var majorClasses = classMatches.Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .Where(c => !c.Contains("it") || c.Length > 4) // Skip tiny utility classes
            .Distinct()
            .Take(20)
            .ToList();

        if (majorClasses.Any(c => c.Contains("hdr") || c.Contains("header")))
            sections.Add("- **Header section** with title, subtitle, and legend");
        if (majorClasses.Any(c => c.Contains("tl-") || c.Contains("timeline")))
            sections.Add("- **Timeline/Gantt section** with SVG milestone visualization");
        if (majorClasses.Any(c => c.Contains("hm-") || c.Contains("heatmap")))
            sections.Add("- **Heatmap grid** — status rows × month columns, color-coded by category");
        if (majorClasses.Any(c => c.Contains("ship")))
            sections.Add("  - Shipped row (green tones)");
        if (majorClasses.Any(c => c.Contains("prog")))
            sections.Add("  - In Progress row (blue tones)");
        if (majorClasses.Any(c => c.Contains("carry")))
            sections.Add("  - Carryover row (yellow/amber tones)");
        if (majorClasses.Any(c => c.Contains("block")))
            sections.Add("  - Blockers row (red tones)");

        return sections.Count > 0 ? string.Join("\n", sections) : "";
    }

    #endregion
}

internal record ResearchDirective
{
    public string TaskId { get; init; } = "";
    public string Topic { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Issue number passed from PM's TaskAssignment for direct linking.</summary>
    public int? IssueNumber { get; init; }
    /// <summary>Number of times this directive has been retried after failure.</summary>
    public int RetryCount { get; init; }
}

internal record ResearchResult
{
    public string Summary { get; init; } = "";
    public List<string> KeyFindings { get; init; } = new();
    public List<string> RecommendedTools { get; init; } = new();
    public List<string> Considerations { get; init; } = new();
    public string DetailedAnalysis { get; init; } = "";
}
