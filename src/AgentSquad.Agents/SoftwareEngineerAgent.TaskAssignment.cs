using System.Collections.Concurrent;
using System.Text;
using AgentSquad.Core.Agents;
using AgentSquad.Core.Agents.Decisions;
using AgentSquad.Core.Agents.Reasoning;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Services;
using AgentSquad.Core.Strategies;
using AgentSquad.Core.Workspace;
using AgentSquad.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Agents;

public partial class SoftwareEngineerAgent
{
    #region AI-Assisted Methods

    private async Task<(bool Approved, string? ReviewBody, IReadOnlyList<PlatformInlineComment> InlineComments)> EvaluatePrQualityAsync(
        AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var pmSpec = await ProjectFiles.GetPMSpecAsync(ct);
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);
            var engineeringPlan = await ProjectFiles.GetEngineeringPlanAsync(ct);

            // Read the linked issue for acceptance criteria
            var issueContext = "";
            var issueNumber = PullRequestWorkflow.ParseLinkedIssueNumber(pr.Body);
            if (issueNumber.HasValue)
            {
                try
                {
                    var issue = await WorkItemService.GetAsync(issueNumber.Value, ct);
                    if (issue is not null)
                        issueContext = $"## Linked Issue #{issue.Number}: {issue.Title}\n{issue.Body}\n\n";
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Could not fetch linked issue #{Number} for PE review", issueNumber.Value);
                }
            }

            // Read actual code files from the PR branch
            var codeContext = await PrWorkflow.GetPRCodeContextAsync(pr.Number, pr.HeadBranch, ct: ct);

            // Tier 4: Get repo structure for duplicate detection during review
            var repoStructure = await GetRepoStructureForContextAsync(ct);

            var useInlineComments = Config.Review.EnableInlineComments;

            var history = CreateChatHistory();
            var reviewSys = PromptService is not null
                ? await PromptService.RenderAsync("software-engineer/code-review-system",
                    new Dictionary<string, string>(), ct)
                : null;

            // Build JSON-structured review system prompt (same pattern as Architect)
            var systemPrompt = reviewSys ??
                "You are a Software Engineer doing a technical code review.\n\n" +
                "SCOPE: You are reviewing EXACTLY ONE PR. Do NOT mention or review other PRs, " +
                "other tasks, or other engineers' work. Every issue you raise MUST reference a " +
                "file that appears in THIS PR's diff. If a file is not in the diff, do not comment on it.\n\n";

            // Append JSON format instructions
            systemPrompt +=
                "CHECK: architecture compliance, implementation completeness, code quality, " +
                "bugs/logic errors, missing validation, test coverage.\n\n" +
                "ACCEPTANCE CRITERIA FILE COMPLETENESS CHECK (critical):\n" +
                "- Compare the ACTUAL files in this PR against the acceptance criteria and file plan " +
                "in the linked issue and PR description.\n" +
                "- If the acceptance criteria specify files/components that should be created " +
                "and those files are MISSING from the PR, this is a REQUEST_CHANGES issue.\n" +
                "- List each missing file/component by name.\n\n" +
                "DUPLICATE/CONFLICT CHECKS (critical for multi-agent projects):\n" +
                "- Does this PR create types/classes that ALREADY EXIST in the main branch file listing?\n" +
                "- Does this PR use the CORRECT namespace consistent with existing code structure?\n" +
                "If you detect duplication or namespace conflicts, mark as REQUEST_CHANGES.\n\n" +
                "EXCESSIVE MODIFICATION CHECK:\n" +
                "- If this PR modifies an existing file, check whether the changes are SURGICAL or a FULL REWRITE.\n" +
                "- A PR that rewrites existing CSS/HTML structure beyond the task scope is REQUEST_CHANGES.\n\n" +
                "CRITICAL RULE: NEVER mention truncated code or inability to see full implementations. " +
                "If you cannot see a method body, ASSUME it is correctly implemented.\n\n" +
                "Only request changes for significant AND fixable issues. Minor style → APPROVE.\n\n" +
                "RESPONSE FORMAT — you MUST respond with ONLY a JSON object, nothing else.\n" +
                "Do NOT include any text before or after the JSON. Do NOT wrap in markdown fences.\n" +
                "The JSON schema is:\n" +
                "- \"verdict\": string, either \"APPROVE\" or \"REQUEST_CHANGES\"\n" +
                "- \"summary\": string, brief 1-2 sentence assessment\n" +
                (useInlineComments
                    ? "- \"comments\": array of objects with:\n" +
                      "  - \"file\": string, relative file path (e.g. \"ReportingDashboard/Services/MyService.cs\")\n" +
                      "  - \"line\": integer, line number in the new file where the comment applies\n" +
                      "  - \"priority\": string, one of \"🔴 Critical\", \"🟠 Important\", \"🟡 Suggestion\", \"🟢 Nit\"\n" +
                      "  - \"body\": string, description of the issue\n"
                    : "") +
                "\nExample response:\n" +
                "{\"verdict\":\"REQUEST_CHANGES\",\"summary\":\"Missing null validation in service layer.\"" +
                (useInlineComments ? ",\"comments\":[{\"file\":\"src/Services/MyService.cs\",\"line\":42,\"priority\":\"🔴 Critical\",\"body\":\"Missing null check on user parameter\"}]" : "") +
                "}\n\n" +
                "Your entire response must be parseable as JSON. Start with { and end with }.";

            history.AddSystemMessage(systemPrompt);

            var reviewContextBuilder = new System.Text.StringBuilder();
            reviewContextBuilder.AppendLine($"## Architecture\n{architectureDoc}\n");
            reviewContextBuilder.AppendLine($"## PM Specification\n{pmSpec}\n");

            // Filter engineering plan to focus on the linked task to prevent cross-task confusion
            var planContext = engineeringPlan;
            if (issueNumber.HasValue && !string.IsNullOrEmpty(engineeringPlan))
            {
                var taskSection = ExtractTaskSectionFromPlan(engineeringPlan, issueNumber.Value, pr.Title);
                if (!string.IsNullOrEmpty(taskSection))
                    planContext = $"(Filtered to task relevant to this PR)\n\n{taskSection}";
            }
            reviewContextBuilder.AppendLine($"## Engineering Plan\n{planContext}\n");

            if (!string.IsNullOrEmpty(repoStructure))
            {
                reviewContextBuilder.AppendLine("## Existing Repository Structure (main branch)");
                reviewContextBuilder.AppendLine(repoStructure);
                reviewContextBuilder.AppendLine();
            }

            // Get screenshot images for vision-based review
            var screenshotImages = new List<PullRequestWorkflow.ScreenshotImage>();
            try
            {
                screenshotImages = await PrWorkflow.GetPRScreenshotImagesAsync(pr.Number, ct: ct);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not fetch screenshots for PE review of PR #{Number}", pr.Number);
            }

            // Log AI description of each screenshot for dashboard visibility
            if (screenshotImages.Count > 0)
            {
                try
                {
                    var descKernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
                    var descChat = descKernel.GetRequiredService<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>();
                    foreach (var img in screenshotImages)
                    {
                        var desc = await PullRequestWorkflow.DescribeScreenshotAsync(img, descChat, ct);
                        LogActivity("screenshot", $"🖼️ SE reviewing screenshot (PR #{pr.Number}): {desc}");
                        Logger.LogInformation("SE peer screenshot description for PR #{PrNumber}: {Description}",
                            pr.Number, desc);
                    }
                }
                catch (Exception descEx)
                {
                    Logger.LogDebug(descEx, "Could not describe screenshots for SE review of PR #{Number}", pr.Number);
                }
            }

            // Update system prompt if screenshots available
            if (screenshotImages.Count > 0)
            {
                var visSys = PromptService is not null
                    ? await PromptService.RenderAsync("software-engineer/visual-validation-supplement",
                        new Dictionary<string, string>(), ct)
                    : null;
                history.AddSystemMessage(visSys ??
                    "VISUAL VALIDATION: Screenshots of the running application are included. " +
                    "LOOK at each screenshot carefully:\n" +
                    "- If the screenshot shows an error page, blank screen, JSON error, or unhandled exception, " +
                    "this is a REQUEST_CHANGES issue — the code does not work.\n" +
                    "- The visual output should match the PR's stated functionality.\n");
            }

            reviewContextBuilder.Append(issueContext);
            reviewContextBuilder.AppendLine($"## Pull Request #{pr.Number}: {pr.Title}\n{pr.Body}\n");

            // Hard scoping barrier — prevents AI from cross-reviewing other PRs
            reviewContextBuilder.AppendLine("---");
            reviewContextBuilder.AppendLine($"⚠️ SCOPE CONSTRAINT: You are reviewing ONLY PR #{pr.Number} (\"{pr.Title}\").");
            reviewContextBuilder.AppendLine("Do NOT comment on other PRs, other tasks, or other engineers' work.");
            reviewContextBuilder.AppendLine("Every review item must reference a file that is CHANGED IN THIS PR.");
            reviewContextBuilder.AppendLine("If you mention a file not in this PR's diff, your review is WRONG.");
            reviewContextBuilder.AppendLine("---\n");

            reviewContextBuilder.Append(codeContext);

            // Add screenshots as vision content if available
            if (screenshotImages.Count > 0)
            {
                var items = new ChatMessageContentItemCollection();
                var screenshotIntro = "\n\n## 📸 Application Screenshots\n" +
                    "LOOK AT EACH IMAGE for errors, blank screens, or broken UI.\n\n";
                for (var i = 0; i < screenshotImages.Count; i++)
                    screenshotIntro += $"Screenshot {i + 1}: {screenshotImages[i].Description}\n";

                items.Add(new TextContent(reviewContextBuilder.ToString() + screenshotIntro));

                foreach (var img in screenshotImages)
                {
                    items.Add(new ImageContent(img.ImageBytes, img.MimeType)
                    {
                        ModelId = $"screenshot: {img.Description}"
                    });
                }

                history.AddUserMessage(items);
            }
            else
            {
                history.AddUserMessage(reviewContextBuilder.ToString());
            }

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);

            var result = response.Content?.Trim() ?? "";

            // Detect garbage AI responses (model breaking character, meta-commentary)
            if (PullRequestWorkflow.IsGarbageAIResponse(result))
            {
                Logger.LogWarning("SE review of PR #{Number} returned garbage AI response, retrying once", pr.Number);

                history.AddAssistantMessage(result);
                history.AddUserMessage(
                    "That response was not valid JSON. Respond with ONLY a JSON object.\n" +
                    "Example: {\"verdict\":\"APPROVE\",\"summary\":\"Code looks good.\"}\n" +
                    "Or: {\"verdict\":\"REQUEST_CHANGES\",\"summary\":\"Issues found.\",\"comments\":[]}");

                response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
                result = response.Content?.Trim() ?? "";

                if (PullRequestWorkflow.IsGarbageAIResponse(result))
                {
                    Logger.LogWarning("SE review of PR #{Number} still garbage after retry — auto-approving", pr.Number);
                    return (true, "Code review passed. Implementation looks reasonable for the task scope.", []);
                }
            }

            // Try to parse as structured JSON (reuse Architect's pattern)
            var structured = TryParseStructuredSeReview(result);
            if (structured is not null)
            {
                var approved = structured.Value.Approved;
                var summary = structured.Value.Summary;
                var inlineComments = structured.Value.Comments;

                Logger.LogInformation(
                    "SE structured review of PR #{Number}: {Verdict}, {CommentCount} inline comments",
                    pr.Number, approved ? "APPROVE" : "REQUEST_CHANGES", inlineComments.Count);

                // Build a clean review body from the summary
                var reviewBody = summary;
                if (!approved && inlineComments.Count > 0)
                {
                    reviewBody += $"\n\n_{inlineComments.Count} inline comment(s) on specific files below._";
                }

                // Filter truncation complaints from inline comments
                var filteredComments = inlineComments
                    .Where(c => !IsTruncationComplaint(c.Body))
                    .ToList();

                // If all comments were truncation complaints, approve instead
                if (!approved && filteredComments.Count == 0 && string.IsNullOrWhiteSpace(summary))
                {
                    Logger.LogInformation("SE review of PR #{Number} only had truncation complaints — auto-approving", pr.Number);
                    return (true, "Code review passed. Implementation meets requirements for the task scope.", []);
                }

                return (approved, reviewBody, filteredComments);
            }

            // Fallback: plain text parsing (backward compat if JSON fails)
            Logger.LogDebug("SE review of PR #{Number} did not parse as JSON, falling back to text parsing", pr.Number);
            var textApproved = result.Contains("VERDICT: APPROVE", StringComparison.OrdinalIgnoreCase)
                || result.Contains("\"verdict\":\"APPROVE\"", StringComparison.OrdinalIgnoreCase);

            var fallbackBody = result
                .Replace("VERDICT: APPROVE", "", StringComparison.OrdinalIgnoreCase)
                .Replace("VERDICT: REQUEST_CHANGES", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            fallbackBody = PullRequestWorkflow.StripReviewPreamble(fallbackBody);
            fallbackBody = FilterTruncationComplaints(fallbackBody);

            // WS2 parser-hardening: extract inline comments from text-format reviews.
            // When the LLM prefixes items with "file:line:", synthesize PlatformInlineComments
            // so review feedback lands on the Files-changed tab instead of conversation-only.
            var extractedInline = ExtractInlineCommentsFromText(fallbackBody);
            if (extractedInline.Count > 0)
            {
                Logger.LogInformation(
                    "SE text-parse fallback extracted {Count} inline comments for PR #{Number}",
                    extractedInline.Count, pr.Number);
            }

            if (!textApproved && string.IsNullOrWhiteSpace(fallbackBody))
            {
                Logger.LogInformation("SE review of PR #{Number} only had truncation complaints — auto-approving", pr.Number);
                return (true, "Code review passed. Implementation meets requirements for the task scope.", []);
            }

            return (textApproved, fallbackBody, extractedInline);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to evaluate PR #{Number} quality with AI", pr.Number);
            return (false, null, []);
        }
    }

    #endregion

    #region SME Reactive Spawning

    /// <summary>
    /// Evaluates whether a task requires specialist expertise and, if so,
    /// generates an SME agent definition and requests spawning (human-gated).
    /// Returns the spawned agent's identity, or null if no SME was needed.
    /// </summary>
    protected async Task<AgentIdentity?> RequestSmeIfNeededAsync(
        string taskDescription, string? additionalContext, CancellationToken ct)
    {
        if (_smeGenerator is null || _spawnManager is null)
            return null;

        if (!Config.SmeAgents.Enabled || !Config.SmeAgents.AllowAgentCreatedDefinitions)
            return null;

        try
        {
            // Ask AI if this task needs specialist expertise
            var kernel = Models.GetKernel(Identity.ModelTier);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var assessPrompt = PromptService is not null
                ? await PromptService.RenderAsync("software-engineer/sme-assessment",
                    new Dictionary<string, string> { ["task_description"] = taskDescription }, ct)
                : null;
            assessPrompt ??= $"""
                Evaluate whether this engineering task requires specialist expertise beyond
                what a general Software Engineer can handle. Consider: security, databases,
                ML/AI, compliance, specific cloud services, accessibility, etc.

                Task: {taskDescription}

                Respond with ONLY "YES" or "NO" on the first line.
                If YES, on the second line list 2-3 required capability keywords (comma-separated).
                """;

            var history = CreateChatHistory();
            history.AddUserMessage(assessPrompt);

            var assessResponse = await chatService.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var assessment = assessResponse.LastOrDefault()?.Content?.Trim() ?? "NO";

            if (!assessment.StartsWith("YES", StringComparison.OrdinalIgnoreCase))
                return null;

            Logger.LogInformation("Task identified as needing SME expertise: {Task}",
                taskDescription[..Math.Min(100, taskDescription.Length)]);

            // Check for existing template match
            var capLines = assessment.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var capabilities = capLines.Length > 1
                ? capLines[1].Split(',', StringSplitOptions.TrimEntries).ToList()
                : new List<string>();

            var existingTemplate = await _smeGenerator.FindMatchingTemplateAsync(capabilities, ct);
            if (existingTemplate is not null)
            {
                Logger.LogInformation("Found existing SME template '{RoleName}' matching task",
                    existingTemplate.RoleName);
                return await _spawnManager.SpawnSmeAgentAsync(existingTemplate, ct: ct);
            }

            // Generate a new definition
            var genPrompt = _smeGenerator.BuildDefinitionGenerationPrompt(taskDescription, additionalContext);
            history = CreateChatHistory();
            history.AddUserMessage(genPrompt);

            var genResponse = await chatService.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var genContent = genResponse.LastOrDefault()?.Content;

            if (string.IsNullOrWhiteSpace(genContent))
                return null;

            var definition = _smeGenerator.ParseDefinition(genContent, Identity.Id);
            if (definition is null)
            {
                Logger.LogWarning("Failed to parse AI-generated SME definition");
                return null;
            }

            Logger.LogInformation("Generated SME definition: {RoleName} for task",
                definition.RoleName);
            LogActivity("task", $"🧠 Requesting SME agent: {definition.RoleName}");

            // Spawn (human-gated via SmeAgentSpawn gate)
            return await _spawnManager.SpawnSmeAgentAsync(definition, ct: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SME assessment/spawn failed for task — proceeding without specialist");
            return null;
        }
    }

    #region Skill-Based Assignment Helpers

    /// <summary>
    /// Uses a single LLM call to semantically match all assignable tasks to all free engineers.
    /// Returns a dictionary of engineerAgentId → assigned task. Engineers/tasks not in the result are unmatched.
    /// Falls back to null if the LLM call fails.
    /// </summary>
    private async Task<Dictionary<string, EngineeringTask>?> MatchTasksToEngineersWithLlmAsync(
        List<EngineeringTask> tasks, List<EngineerInfo> engineers, CancellationToken ct)
    {
        if (tasks.Count == 0 || engineers.Count == 0) return null;

        try
        {
            var stepId = TaskTracker.BeginStep(Identity.Id, "pe-orchestration", "LLM skill matching",
                $"Matching {tasks.Count} tasks to {engineers.Count} engineers using semantic skill analysis", Identity.ModelTier);

            var prompt = new StringBuilder();
            prompt.AppendLine("You are an engineering manager assigning tasks to the best-qualified engineers.");
            prompt.AppendLine("Match each task to the single best engineer based on SEMANTIC skill relevance.");
            prompt.AppendLine("Do NOT require exact keyword matches — use domain knowledge to infer skill fit.");
            prompt.AppendLine("For example: a Frontend Engineer should get UI/React/HTML tasks even if 'react' isn't in their capabilities.");
            prompt.AppendLine("A Cloud Engineer should get Azure/AWS/infrastructure tasks.");
            prompt.AppendLine("Generalist engineers (no specific capabilities) should get tasks that don't fit any specialist.");
            prompt.AppendLine();
            prompt.AppendLine("## Available Engineers");
            foreach (var eng in engineers)
            {
                var caps = eng.Capabilities.Count > 0
                    ? $"Capabilities: [{string.Join(", ", eng.Capabilities)}]"
                    : "Generalist (no specific capabilities)";
                prompt.AppendLine($"- **{eng.Name}** (ID: `{eng.AgentId}`) — {caps}");
            }
            prompt.AppendLine();
            prompt.AppendLine("## Tasks to Assign");
            foreach (var task in tasks)
            {
                var tags = task.SkillTags.Count > 0
                    ? $" | Tags: [{string.Join(", ", task.SkillTags)}]"
                    : "";
                prompt.AppendLine($"- **{task.Id}**: {task.Name} (Complexity: {task.Complexity}{tags})");
                if (!string.IsNullOrWhiteSpace(task.Description))
                    prompt.AppendLine($"  Description: {task.Description[..Math.Min(200, task.Description.Length)]}");
            }
            prompt.AppendLine();
            prompt.AppendLine("## Rules");
            prompt.AppendLine("1. Each engineer gets AT MOST one task");
            prompt.AppendLine("2. Each task is assigned to AT MOST one engineer");
            prompt.AppendLine("3. Prefer assigning specialists to tasks matching their domain");
            prompt.AppendLine("4. Assign higher-complexity tasks to more experienced/specialized engineers");
            prompt.AppendLine("5. If there are more tasks than engineers, leave extra tasks unassigned");
            prompt.AppendLine("6. If there are more engineers than tasks, leave extra engineers unassigned");
            prompt.AppendLine();
            prompt.AppendLine("Respond with ONLY a JSON object (no markdown fences):");
            prompt.AppendLine("{");
            prompt.AppendLine("  \"assignments\": [");
            prompt.AppendLine("    { \"taskId\": \"T1\", \"engineerAgentId\": \"agent-id\", \"reason\": \"Brief reason for this match\" }");
            prompt.AppendLine("  ]");
            prompt.AppendLine("}");

            // Use budget tier — this is structured matching, not code generation
            var kernel = Models.GetKernel("budget", Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = CreateChatHistory();
            history.AddUserMessage(prompt.ToString());

            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var content = response.FirstOrDefault()?.Content;

            TaskTracker.CompleteStep(stepId);

            if (string.IsNullOrWhiteSpace(content))
            {
                Logger.LogWarning("LLM skill matching returned empty response, falling back to exact match");
                return null;
            }

            // Parse JSON response
            var jsonContent = content.Trim();
            if (jsonContent.StartsWith("```")) // Strip markdown fences if present
            {
                var lines = jsonContent.Split('\n');
                jsonContent = string.Join('\n', lines.Skip(1).TakeWhile(l => !l.TrimStart().StartsWith("```")));
            }

            var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = System.Text.Json.JsonSerializer.Deserialize<LlmAssignmentResponse>(jsonContent, options);

            if (result?.Assignments is null || result.Assignments.Count == 0)
            {
                Logger.LogWarning("LLM skill matching returned no assignments, falling back to exact match");
                return null;
            }

            // Build validated assignment map
            var taskLookup = tasks.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var engineerIds = new HashSet<string>(engineers.Select(e => e.AgentId), StringComparer.OrdinalIgnoreCase);
            var assignments = new Dictionary<string, EngineeringTask>(StringComparer.OrdinalIgnoreCase);
            var assignedTaskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in result.Assignments)
            {
                if (string.IsNullOrEmpty(a.TaskId) || string.IsNullOrEmpty(a.EngineerAgentId))
                    continue;
                if (!taskLookup.TryGetValue(a.TaskId, out var task))
                {
                    Logger.LogDebug("LLM suggested unknown task ID {TaskId}, skipping", a.TaskId);
                    continue;
                }
                if (!engineerIds.Contains(a.EngineerAgentId))
                {
                    Logger.LogDebug("LLM suggested unknown engineer ID {EngineerId}, skipping", a.EngineerAgentId);
                    continue;
                }
                if (assignments.ContainsKey(a.EngineerAgentId) || assignedTaskIds.Contains(a.TaskId))
                    continue; // Duplicate — skip

                assignments[a.EngineerAgentId] = task;
                assignedTaskIds.Add(a.TaskId);

                Logger.LogInformation(
                    "LLM matched task {TaskId} ({TaskName}) → {Engineer}: {Reason}",
                    a.TaskId, task.Name, a.EngineerAgentId, a.Reason ?? "no reason given");
            }

            Logger.LogInformation("LLM skill matching produced {Count}/{Total} assignments",
                assignments.Count, Math.Min(tasks.Count, engineers.Count));

            return assignments.Count > 0 ? assignments : null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "LLM skill matching failed, falling back to exact match");
            return null;
        }
    }

    private sealed class LlmAssignmentResponse
    {
        public List<LlmAssignment> Assignments { get; set; } = [];
    }

    private sealed class LlmAssignment
    {
        public string TaskId { get; set; } = "";
        public string EngineerAgentId { get; set; } = "";
        public string? Reason { get; set; }
    }

    /// <summary>
    /// FALLBACK: Finds the task with the highest skill tag overlap with the given capabilities.
    /// Returns null if no tasks have any overlapping tags.
    /// </summary>
    private static EngineeringTask? FindBestMatchingTask(List<EngineeringTask> tasks, List<string> capabilities)
    {
        EngineeringTask? bestTask = null;
        var bestScore = 0;

        foreach (var task in tasks)
        {
            if (task.SkillTags.Count == 0) continue;
            var overlap = task.SkillTags.Count(tag =>
                capabilities.Any(cap => string.Equals(cap, tag, StringComparison.OrdinalIgnoreCase)));
            if (overlap > bestScore)
            {
                bestScore = overlap;
                bestTask = task;
            }
        }

        return bestTask;
    }

    /// <summary>
    /// For a generalist engineer, prefers tasks that no specialist would match well.
    /// Falls back to highest complexity unmatched task.
    /// </summary>
    private static EngineeringTask? FindBestTaskForGeneralist(
        List<EngineeringTask> tasks, List<EngineerInfo> allEngineers)
    {
        var specialists = allEngineers.Where(e => e.Capabilities.Count > 0).ToList();
        if (specialists.Count == 0)
        {
            // No specialists — just pick highest complexity
            return tasks.OrderByDescending(t => ComplexityRank(t.Complexity)).FirstOrDefault();
        }

        // Prefer tasks that no specialist has matching skills for
        var unmatchedTasks = tasks.Where(t =>
        {
            if (t.SkillTags.Count == 0) return true; // No tags = anyone can do it
            return !specialists.Any(s => t.SkillTags.Any(tag =>
                s.Capabilities.Any(cap => string.Equals(cap, tag, StringComparison.OrdinalIgnoreCase))));
        }).ToList();

        if (unmatchedTasks.Count > 0)
            return unmatchedTasks.OrderByDescending(t => ComplexityRank(t.Complexity)).FirstOrDefault();

        // All tasks have matching specialists — just pick highest complexity
        return tasks.OrderByDescending(t => ComplexityRank(t.Complexity)).FirstOrDefault();
    }

    private static int ComplexityRank(string complexity) => complexity.ToLowerInvariant() switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };

    #endregion

    #endregion
}

internal record EngineeringTask
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Complexity { get; init; } = "Medium";
    public string Status { get; init; } = "Pending";
    public string? AssignedTo { get; init; }
    public int? PullRequestNumber { get; init; }
    public int? IssueNumber { get; init; }
    /// <summary>GitHub internal ID (not the issue number). Required for sub-issue and dependency APIs.</summary>
    public long? GitHubId { get; init; }
    public string? IssueUrl { get; init; }
    public List<string> Dependencies { get; init; } = new();
    /// <summary>Issue numbers this task depends on (parsed from issue body "Depends On").</summary>
    public List<int> DependencyIssueNumbers { get; init; } = new();
    /// <summary>Parent PM issue number (parsed from issue body "Parent Issue").</summary>
    public int? ParentIssueNumber { get; init; }
    /// <summary>All enhancement issue numbers this task covers (used in SinglePRMode where one task spans many enhancements).</summary>
    public List<int> RelatedEnhancementNumbers { get; init; } = new();
    /// <summary>Current GitHub labels on this issue (for status label management).</summary>
    public List<string> Labels { get; init; } = new();

    /// <summary>Skill tags for capability-based task assignment (e.g., "frontend", "react", "database").</summary>
    public List<string> SkillTags { get; init; } = new();

    // ── PE Parallelism Enhancements ──

    /// <summary>Wave assignment for parallel scheduling (W1, W2, etc.). Default W1.</summary>
    public string Wave { get; set; } = "W1";
    /// <summary>Files this task owns (CREATE + MODIFY), extracted from FilePlan. Normalized paths.</summary>
    public List<string> OwnedFiles { get; init; } = new();
    /// <summary>Typed dependencies: taskId → dependency type (files, api, schema, etc.).</summary>
    public Dictionary<string, string> DependencyTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// BUG FIX: Added AgentId field. Previously only Name (DisplayName) was stored, but
// all message routing and _agentAssignments must use Identity.Id for correct delivery.
internal record EngineerInfo
{
    public string AgentId { get; init; } = "";
    public string Name { get; init; } = "";
    public AgentRole Role { get; init; }
    public List<string> Capabilities { get; init; } = [];
}