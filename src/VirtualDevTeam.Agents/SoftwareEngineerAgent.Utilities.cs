using System.Collections.Concurrent;
using System.Text;
using VirtualDevTeam.Core.Agents;
using VirtualDevTeam.Core.Agents.Decisions;
using VirtualDevTeam.Core.Agents.Reasoning;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.GitHub.Models;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Services;
using VirtualDevTeam.Core.Strategies;
using VirtualDevTeam.Core.Workspace;
using VirtualDevTeam.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Agents;

public partial class SoftwareEngineerAgent
{
    #region Helpers

    /// <summary>
    /// Extracts just the section of the engineering plan that corresponds to the given
    /// issue number or PR title. Prevents the review AI from confusing tasks when
    /// the full plan with all tasks is in context.
    /// </summary>
    private static string? ExtractTaskSectionFromPlan(string plan, int issueNumber, string prTitle)
    {
        var lines = plan.Split('\n');
        var result = new System.Text.StringBuilder();
        bool capturing = false;
        int headerLevel = 0;

        // Patterns to match: "T1", "T2", issue #number, or the task title
        var issueRef = $"#{issueNumber}";
        var taskTitle = PullRequestWorkflow.ParseTaskTitleFromTitle(prTitle);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Detect markdown headers (## Task, ### T1, etc.)
            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();

                if (capturing && level <= headerLevel)
                {
                    // Hit a same-level or higher header — stop capturing
                    break;
                }

                // Check if this header matches our task
                if (!capturing &&
                    (line.Contains(issueRef, StringComparison.OrdinalIgnoreCase) ||
                     (taskTitle is not null && line.Contains(taskTitle, StringComparison.OrdinalIgnoreCase))))
                {
                    capturing = true;
                    headerLevel = level;
                }
            }

            if (capturing)
                result.AppendLine(line);
        }

        var extracted = result.ToString().Trim();
        return string.IsNullOrEmpty(extracted) ? null : extracted;
    }

    /// <summary>
    /// Filters out numbered review items that complain about truncated or invisible code.
    /// Returns the remaining review body with items renumbered.
    /// </summary>
    private static string FilterTruncationComplaints(string reviewBody)
    {
        if (string.IsNullOrWhiteSpace(reviewBody))
            return reviewBody;

        string[] truncationKeywords =
        [
            "truncated",
            "cut off",
            "cannot verify",
            "cannot see",
            "can't verify",
            "can't see",
            "not visible",
            "not shown",
            "hidden",
            "implementation not visible",
            "implementations are cut",
            "code hides",
            "unable to verify",
            "unable to see"
        ];

        var lines = reviewBody.Split('\n');
        var filteredItems = new List<string>();
        var currentItem = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Check if this starts a new numbered item
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.\)]\s"))
            {
                // Flush previous item if it exists
                if (currentItem.Length > 0)
                {
                    var item = currentItem.ToString();
                    if (!truncationKeywords.Any(kw => item.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                        filteredItems.Add(item);
                    currentItem.Clear();
                }
                currentItem.AppendLine(line);
            }
            else
            {
                currentItem.AppendLine(line);
            }
        }

        // Flush last item
        if (currentItem.Length > 0)
        {
            var item = currentItem.ToString();
            if (!truncationKeywords.Any(kw => item.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                filteredItems.Add(item);
        }

        // Renumber remaining items
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < filteredItems.Count; i++)
        {
            var item = filteredItems[i].Trim();
            // Replace the leading number with the new index
            item = System.Text.RegularExpressions.Regex.Replace(item, @"^\d+[\.\)]", $"{i + 1}.");
            result.AppendLine(item);
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Checks if a single review comment body is a truncation complaint.
    /// </summary>
    private static bool IsTruncationComplaint(string body)
    {
        string[] truncationKeywords =
        [
            "truncated", "cut off", "cannot verify", "cannot see",
            "can't verify", "can't see", "not visible", "not shown",
            "implementation not visible", "unable to verify", "unable to see"
        ];
        return truncationKeywords.Any(kw => body.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// WS2 parser-hardening fallback: extract inline review comments from text-format reviews.
    /// Matches numbered-list items prefixed with "file.ext:line:" so review feedback lands on
    /// the Files-changed tab even when the LLM doesn't emit structured JSON.
    /// </summary>
    private static List<PlatformInlineComment> ExtractInlineCommentsFromText(string? text)
    {
        var results = new List<PlatformInlineComment>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        var pattern = @"(?m)^\s*(?:[-*]|\d+\.)?\s*[`""']?([\w./\\\-]+\.[a-zA-Z]{1,8})[`""']?:(\d+):\s*(.+?)(?:\r?\n(?=\s*(?:[-*]|\d+\.))|\r?\n\r?\n|\z)";
        var regex = new System.Text.RegularExpressions.Regex(
            pattern,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match match in regex.Matches(text))
        {
            var file = match.Groups[1].Value.Trim();
            if (!int.TryParse(match.Groups[2].Value, out var line) || line < 1) continue;
            var body = match.Groups[3].Value.Trim();
            if (string.IsNullOrWhiteSpace(body) || IsTruncationComplaint(body)) continue;

            file = file.Replace('\\', '/');

            results.Add(new PlatformInlineComment
            {
                FilePath = file,
                Line = line,
                Body = body
            });
        }

        return results;
    }

    /// <summary>
    /// Parses SE review JSON response into structured components.
    /// Returns null if the response isn't valid JSON.
    /// </summary>
    private static (bool Approved, string Summary, IReadOnlyList<PlatformInlineComment> Comments)? TryParseStructuredSeReview(string text)
    {
        try
        {
            var json = text.Trim();

            // Strip markdown fences if present
            if (json.Contains("```"))
            {
                var fenceStart = json.IndexOf("```");
                var afterFence = json.IndexOf('\n', fenceStart);
                if (afterFence >= 0)
                {
                    var fenceEnd = json.IndexOf("```", afterFence);
                    json = fenceEnd > afterFence
                        ? json[(afterFence + 1)..fenceEnd].Trim()
                        : json[(afterFence + 1)..].Trim();
                }
            }

            // Find JSON object boundaries with proper nesting
            var startBrace = json.IndexOf('{');
            if (startBrace < 0) return null;

            var depth = 0;
            var endBrace = -1;
            var inString = false;
            var escape = false;
            for (var i = startBrace; i < json.Length; i++)
            {
                var c = json[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { endBrace = i; break; } }
            }
            if (endBrace < 0) return null;
            json = json[startBrace..(endBrace + 1)];

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var verdict = root.TryGetProperty("verdict", out var v)
                ? v.GetString()?.Trim().ToUpperInvariant() ?? "REQUEST_CHANGES"
                : "REQUEST_CHANGES";

            var approved = verdict.Contains("APPROVE") && !verdict.Contains("REQUEST");

            var summary = root.TryGetProperty("summary", out var s)
                ? s.GetString()?.Trim() ?? ""
                : "";

            var comments = new List<PlatformInlineComment>();
            if (root.TryGetProperty("comments", out var commentsArr)
                && commentsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var c in commentsArr.EnumerateArray())
                {
                    var file = c.TryGetProperty("file", out var f) ? f.GetString() : null;
                    var line = c.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    var priority = c.TryGetProperty("priority", out var p) ? p.GetString() ?? "" : "";
                    var body = c.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(body) && line > 0)
                    {
                        comments.Add(new PlatformInlineComment
                        {
                            FilePath = file,
                            Line = line,
                            Body = $"{priority}: {body}".Trim()
                        });
                    }
                }
            }

            return (approved, summary, comments);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Submits inline review comments as a GitHub PR review.
    /// Tags each comment with this engineer's display name (e.g. "[SoftwareEngineer]"
    /// or "[SoftwareEngineer 2]") so peer reviews from different SE pool members
    /// are visibly distinct on GitHub — otherwise SE 1's PR + SE 2's review both
    /// render as plain "[SoftwareEngineer]" and look like a self-review.
    /// Falls back gracefully if the GitHub API call fails.
    /// </summary>
    private async Task SubmitPlatformInlineCommentsAsync(
        int prNumber, string summary, bool approved,
        IReadOnlyList<PlatformInlineComment> comments, CancellationToken ct)
    {
        try
        {
            var reviewerTag = Identity.DisplayName;
            var maxComments = Config.Review.MaxInlineCommentsPerReview;
            var toSubmit = comments.Take(maxComments)
                .Select(c => new PlatformInlineComment
                {
                    FilePath = c.FilePath,
                    Line = c.Line,
                    Body = $"**[{reviewerTag}]** {c.Body}"
                })
                .ToList();

            if (toSubmit.Count == 0) return;

            // Single-PAT setup: APPROVE/REQUEST_CHANGES are forbidden on own PRs.
            // Always use COMMENT so inline comments land on the Files-changed tab.
            var eventType = "COMMENT";
            var reviewBody = $"🔧 **[{reviewerTag}] Inline Review** — {(approved ? "APPROVED" : "CHANGES REQUESTED")}\n\n" +
                $"{summary}\n\n" +
                $"_{toSubmit.Count} inline comment(s) below_";

            await ReviewService.CreateReviewWithInlineCommentsAsync(
                prNumber, reviewBody, eventType, toSubmit, ct: ct);

            Logger.LogInformation(
                "Submitted {Count} SE inline review comments on PR #{Number}",
                toSubmit.Count, prNumber);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to submit SE inline review comments on PR #{Number} — review body was still posted as comment",
                prNumber);
        }
    }

    /// <summary>
    /// After approving a PR, resolve all open inline review threads left by THIS SE.
    /// Filters by `[{Identity.DisplayName}]` (e.g. "[SoftwareEngineer 2]") so a peer
    /// SE that approves doesn't accidentally close threads owned by a different SE.
    /// </summary>
    private async Task ResolveSEReviewThreadsAsync(int prNumber, CancellationToken ct)
    {
        try
        {
            var reviewerTag = $"[{Identity.DisplayName}]";
            var threads = await ReviewService.GetThreadsAsync(prNumber, ct);
            var ownThreads = threads
                .Where(t => !t.IsResolved && t.Body.Contains(reviewerTag, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (ownThreads.Count == 0)
            {
                Logger.LogDebug("No unresolved SE review threads on PR #{Number} for {Reviewer}", prNumber, reviewerTag);
                return;
            }

            Logger.LogInformation("Resolving {Count} SE review threads on PR #{Number} after approval ({Reviewer})",
                ownThreads.Count, prNumber, reviewerTag);

            foreach (var thread in ownThreads)
            {
                var replyBody = $"✅ **[{Identity.DisplayName}] Resolved** — Rework addressed this feedback. Approved.";
                await ReviewService.ResolveThreadAsync(
                    prNumber, thread.ThreadId, replyBody, ct);
            }

            LogActivity("review", $"🔒 Resolved {ownThreads.Count} SE inline review thread(s) on PR #{prNumber}");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to resolve SE review threads on PR #{Number} — approval still proceeds", prNumber);
        }
    }

    /// <summary>
    /// After merging an engineer's PR, find the corresponding task issue and close it.
    /// Searches by task name from PR title, then by issue number from agent assignments.
    /// </summary>
    private async Task MarkEngineerTaskDoneAsync(AgentPullRequest pr, CancellationToken ct)
    {
        // Search by task name from PR title (strip agent prefix, handle double-prefix)
        var taskName = PullRequestWorkflow.ParseTaskTitleFromTitle(pr.Title);
        EngineeringTask? task = null;

        if (taskName is not null)
        {
            var innerName = PullRequestWorkflow.ParseTaskTitleFromTitle(taskName);
            if (innerName is not null) taskName = innerName;
            task = _taskManager.FindByName(taskName);
        }

        // Fallback: match by issue number from the specific PR author's assignment
        if (task is null)
        {
            var agentName = PullRequestWorkflow.ParseAgentNameFromTitle(pr.Title);
            if (agentName is not null)
            {
                var authorEntry = _agentAssignments.FirstOrDefault(kv =>
                {
                    var agent = _registry.GetAgentsByRole(AgentRole.SoftwareEngineer)
                        .Where(a => a.Identity.Id != Identity.Id)
                        .FirstOrDefault(a => a.Identity.Id == kv.Key);
                    return agent is not null &&
                           string.Equals(agent.Identity.DisplayName, agentName, StringComparison.OrdinalIgnoreCase);
                });

                if (authorEntry.Key is not null)
                    task = _taskManager.FindByIssueNumber(authorEntry.Value);
            }
        }

        if (task?.IssueNumber.HasValue == true)
        {
            await _taskManager.MarkDoneAsync(task.IssueNumber.Value, pr.Number, ct);
            Logger.LogInformation("Marked engineer task {TaskId} '{TaskName}' as Done (PR #{PrNumber} merged)",
                task.Id, task.Name, pr.Number);
        }
        else
        {
            Logger.LogWarning("Could not find task issue for merged PR #{PrNumber} ({Title})",
                pr.Number, pr.Title);
        }
    }

    private async Task<string> GenerateTaskDescriptionAsync(
        EngineeringTask task, CancellationToken ct)
    {
        try
        {
            var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var pmSpec = await ProjectFiles.GetPMSpecAsync(ct);
            var architectureDoc = await ProjectFiles.GetArchitectureDocAsync(ct);

            var issueContext = "";
            if (task.IssueNumber.HasValue)
            {
                var issue = await WorkItemService.GetAsync(task.IssueNumber.Value, ct);
                if (issue is not null)
                    issueContext = $"\n\n## Source Issue #{issue.Number}: {issue.Title}\n{issue.Body}";
            }

            // In SinglePRMode, fetch ALL related user story bodies so the SE has full context
            if (task.RelatedEnhancementNumbers.Count > 0)
            {
                var storyDetails = new StringBuilder();
                storyDetails.AppendLine("\n\n## Related User Stories (Full Details)");
                foreach (var storyNum in task.RelatedEnhancementNumbers)
                {
                    if (storyNum == task.IssueNumber) continue; // Already fetched above
                    try
                    {
                        var story = await WorkItemService.GetAsync(storyNum, ct);
                        if (story is not null)
                        {
                            storyDetails.AppendLine($"\n### Issue #{story.Number}: {story.Title}");
                            storyDetails.AppendLine(story.Body ?? "(no description)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "Could not fetch related story #{Number} for task context", storyNum);
                    }
                }
                issueContext += storyDetails.ToString();
            }

            var history = CreateChatHistory();
            var descSys = PromptService is not null
                ? await PromptService.RenderAsync("software-engineer/pr-description-system",
                    new Dictionary<string, string>(), ct)
                : null;
            history.AddSystemMessage(descSys ??
                "You are a Software Engineer writing a detailed PR description for an engineering task. " +
                "The description should be clear enough for another engineer to implement the task. " +
                "Include:\n" +
                "1. **Summary**: What this PR implements\n" +
                "2. **Acceptance Criteria**: Specific, testable criteria\n" +
                "3. **Implementation Steps**: An ordered, numbered list of discrete implementation steps. " +
                "Step 1 MUST be scaffolding (folder structure, config, boilerplate). " +
                "All paths relative to repo root. Place .sln at root, project under ProjectName/. " +
                "NEVER create redundant same-named nested folders. Each subsequent step " +
                "builds on the previous. Each step should be a self-contained committable unit of work. " +
                "3-6 steps total. Be specific about what each step produces.\n" +
                "4. **Testing**: What tests should cover");

            var descUser = PromptService is not null
                ? await PromptService.RenderAsync("software-engineer/pr-description-user",
                    new Dictionary<string, string>
                    {
                        ["pm_spec"] = pmSpec,
                        ["architecture"] = architectureDoc,
                        ["issue_context"] = issueContext,
                        ["task_name"] = task.Name,
                        ["task_description"] = task.Description
                    }, ct)
                : null;
            history.AddUserMessage(descUser ??
                $"## PM Specification\n{pmSpec}\n\n" +
                $"## Architecture\n{architectureDoc}" +
                issueContext +
                $"\n\n## Task: {task.Name}\n{task.Description}\n\n" +
                "Write a detailed PR description with:\n" +
                "1. **Summary**: What this PR implements\n" +
                "2. **Acceptance Criteria**: Specific, testable criteria\n" +
                "3. **Implementation Steps**: Ordered, numbered list of discrete steps. " +
                "Step 1 = scaffolding. Each step is a committable unit. 3-6 steps.\n" +
                "4. **Testing**: What tests should cover");

            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            return response.Content?.Trim() ?? task.Description;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to generate task description with AI, using raw description");
            return task.Description;
        }
    }

    private static string NormalizeComplexity(string complexity)
    {
        return complexity.ToLowerInvariant() switch
        {
            "high" or "complex" or "hard" => "High",
            "medium" or "moderate" or "mid" => "Medium",
            "low" or "simple" or "easy" => "Low",
            _ => "Medium"
        };
    }

    /// <summary>
    /// Formats a semicolon-separated file plan string into readable markdown.
    /// Input: "CREATE:src/Services/Auth.cs(MyApp.Services);MODIFY:src/Program.cs;USE:User(MyApp.Models)"
    /// Output: markdown bullet list with file operations
    /// </summary>
    /// <summary>
    /// Enforces the foundation-first pattern: ensures the first task (T1) has no dependencies
    /// and all other tasks depend on T1. If the AI didn't produce a proper foundation task,
    /// this reorders tasks so the foundation-like one is first, or injects a synthetic one.
    /// </summary>
    /// Recomputes wave assignments from the dependency graph to eliminate gaps.
    /// Foundation tasks (first task, W0) keep their wave. Other tasks get:
    /// wave = max(dependency waves) + 1, minimum W1 for tasks with no non-foundation deps.
    private void RecomputeWavesFromDependencies(List<EngineeringTask> tasks)
    {
        if (tasks.Count <= 1) return;

        var taskById = new Dictionary<string, EngineeringTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
            taskById[t.Id] = t;
        var foundationId = tasks[0].Id;

        // Assign W0 to foundation
        tasks[0].Wave = "W0";

        // Compute waves via BFS from dependencies
        var changed = true;
        var iterations = 0;
        while (changed && iterations < 20)
        {
            changed = false;
            iterations++;
            foreach (var task in tasks.Skip(1))
            {
                if (task.Id == IntegrationTaskId) continue;

                var maxDepWave = 0;
                foreach (var depId in task.Dependencies)
                {
                    if (string.Equals(depId, foundationId, StringComparison.OrdinalIgnoreCase))
                    {
                        maxDepWave = Math.Max(maxDepWave, 0); // W0
                        continue;
                    }
                    if (taskById.TryGetValue(depId, out var dep))
                    {
                        var depWaveNum = ParseWaveNumber(dep.Wave);
                        maxDepWave = Math.Max(maxDepWave, depWaveNum);
                    }
                }

                var newWave = $"W{maxDepWave + 1}";
                // Tasks with only foundation dependency → W1
                if (task.Dependencies.Count == 0 ||
                    task.Dependencies.All(d => string.Equals(d, foundationId, StringComparison.OrdinalIgnoreCase)))
                    newWave = "W1";

                if (!string.Equals(task.Wave, newWave, StringComparison.OrdinalIgnoreCase))
                {
                    task.Wave = newWave;
                    changed = true;
                }
            }
        }

        // Integration task always gets the highest wave + 1
        var integrationTask = tasks.FirstOrDefault(t => t.Id == IntegrationTaskId);
        if (integrationTask is not null)
        {
            var maxWave = tasks.Where(t => t.Id != IntegrationTaskId)
                .Max(t => ParseWaveNumber(t.Wave));
            integrationTask.Wave = $"W{maxWave + 1}";
        }

        // Log the recomputed wave distribution
        var waveGroups = tasks.Where(t => t.Id != IntegrationTaskId)
            .GroupBy(t => t.Wave).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Count()}");
        Logger.LogInformation("Recomputed waves from dependency graph: {Distribution}",
            string.Join(", ", waveGroups));
    }

    private static int ParseWaveNumber(string? wave)
    {
        if (string.IsNullOrEmpty(wave)) return 1;
        if (wave.StartsWith('W') && int.TryParse(wave.AsSpan(1), out var num))
            return num;
        return 1;
    }

    /// <summary>
    /// For MultiPRSerial mode: enforce a strict linear dependency chain (T1 → T2 → T3 → ... → T-FINAL).
    /// Each task depends on the previous one, ensuring only one PR is active at a time.
    /// Assigns one task per wave (W0, W1, W2, ...).
    /// </summary>
    private void ApplySerialDependencyChain(List<EngineeringTask> tasks)
    {
        if (tasks.Count <= 1) return;

        // Separate integration task (always last)
        var integrationTask = tasks.FirstOrDefault(t => t.Id == IntegrationTaskId);
        var workTasks = tasks.Where(t => t.Id != IntegrationTaskId).ToList();

        // Build linear chain: each task depends on the previous
        for (int i = 0; i < workTasks.Count; i++)
        {
            workTasks[i].Wave = $"W{i}";
            if (i > 0)
            {
                // Ensure dependency on previous task
                var prevId = workTasks[i - 1].Id;
                if (!workTasks[i].Dependencies.Contains(prevId, StringComparer.OrdinalIgnoreCase))
                    workTasks[i].Dependencies.Add(prevId);
            }
        }

        // Integration task depends on ALL work tasks and gets the final wave
        if (integrationTask is not null)
        {
            integrationTask.Wave = $"W{workTasks.Count}";
            foreach (var wt in workTasks)
            {
                if (!integrationTask.Dependencies.Contains(wt.Id, StringComparer.OrdinalIgnoreCase))
                    integrationTask.Dependencies.Add(wt.Id);
            }
        }

        Logger.LogInformation(
            "Applied serial dependency chain: {Count} work tasks in linear order (one PR at a time)",
            workTasks.Count);
    }

    /// <summary>
    /// Registers human-friendly display names for engineering tasks in the task tracker.
    /// Called on both fresh plan creation and task restoration so the dashboard always
    /// shows meaningful names like "#2221: Implement entire project" instead of "T1".
    /// </summary>
    private void RegisterTaskDisplayNames(IEnumerable<EngineeringTask> tasks)
    {
        foreach (var task in tasks)
        {
            var displayName = task.IssueNumber.HasValue
                ? $"#{task.IssueNumber}: {task.Name}"
                : task.Name;
            TaskTracker.RegisterTaskDisplayName(task.Id, displayName);
        }
    }

    /// <summary>
    /// Determines if a task is a foundation/scaffolding task that the SE Lead should handle itself.
    /// Heuristic: task ID is "T1", wave is "W0", title contains foundation keywords,
    /// or the task has zero dependencies.
    /// </summary>
    private static bool IsFoundationTask(EngineeringTask task)
    {
        var foundationKeywords = new[] { "foundation", "scaffolding", "scaffold", "setup", "initial" };
        var hasFoundationTitle = foundationKeywords.Any(k =>
            task.Name.Contains(k, StringComparison.OrdinalIgnoreCase));

        return string.Equals(task.Id, "T1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Wave, "W0", StringComparison.OrdinalIgnoreCase)
            || (hasFoundationTitle && task.DependencyIssueNumbers.Count == 0);
    }

    /// <summary>
    /// Extract the task title from a string like "SoftwareEngineer 1: Agent Workflow Engine".
    /// Returns the part after the colon, or the whole string if no colon is found.
    /// </summary>
    private static string ExtractTaskTitle(string titleWithPrefix)
    {
        var colonIdx = titleWithPrefix.IndexOf(':');
        if (colonIdx >= 0 && colonIdx < titleWithPrefix.Length - 1)
            return titleWithPrefix[(colonIdx + 1)..].Trim();
        return titleWithPrefix.Trim();
    }

    /// <summary>
    /// Extract the agent name prefix from a string like "SoftwareEngineer 3: Agent Workflow Engine".
    /// Returns "SoftwareEngineer 3" or null if no colon found.
    /// </summary>
    private static string? ExtractAgentPrefix(string titleWithPrefix)
    {
        var colonIdx = titleWithPrefix.IndexOf(':');
        if (colonIdx > 0)
            return titleWithPrefix[..colonIdx].Trim();
        return null;
    }

    /// <summary>
    /// Returns true if a task's name/description keywords align with this agent's display name.
    /// Used for self-claim preference ordering — not strict filtering.
    /// </summary>
    private bool MatchesCapabilities(EngineeringTask task)
    {
        var name = Identity.DisplayName.ToLowerInvariant();
        var taskText = $"{task.Name} {task.Description}".ToLowerInvariant();

        // Generic SE workers match everything
        if (name.Contains("software engineer")) return true;

        // Check for keyword overlap between agent name and task text
        var nameWords = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .ToArray();
        return nameWords.Any(w => taskText.Contains(w));
    }

    private void EnsureFoundationFirstPattern(List<EngineeringTask> tasks, IReadOnlyList<PlatformWorkItem> enhancementIssues)
    {
        if (tasks.Count <= 1) return;

        var foundationKeywords = new[] { "foundation", "scaffold", "setup", "structure", "skeleton", "template", "infrastructure", "project setup" };
        var firstTask = tasks[0];
        var isFirstFoundation = firstTask.Dependencies.Count == 0 &&
            foundationKeywords.Any(k => firstTask.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                        firstTask.Description.Contains(k, StringComparison.OrdinalIgnoreCase));

        // If T1 isn't a foundation task, look for one elsewhere in the list and move it to front
        if (!isFirstFoundation)
        {
            var foundationIdx = tasks.FindIndex(t =>
                t.Dependencies.Count == 0 &&
                foundationKeywords.Any(k => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                            t.Description.Contains(k, StringComparison.OrdinalIgnoreCase)));

            if (foundationIdx > 0)
            {
                var foundationTask = tasks[foundationIdx];
                tasks.RemoveAt(foundationIdx);
                tasks.Insert(0, foundationTask);
                Logger.LogInformation("Reordered foundation task '{Name}' to first position", foundationTask.Name);
            }
            else
            {
                Logger.LogInformation("No explicit foundation task found — T1 ({Name}) will serve as foundation", firstTask.Name);
            }
        }

        // Ensure T1 has no dependencies
        var t1 = tasks[0];
        if (t1.Dependencies.Count > 0)
        {
            tasks[0] = t1 with { Dependencies = new List<string>() };
            Logger.LogInformation("Cleared dependencies from foundation task T1 ({Name})", t1.Name);
        }

        // Ensure T1 has a ParentIssueNumber so it survives the enhancement scope filter
        // in LoadTasksAsync. Foundation tasks are cross-cutting (support all enhancements),
        // so link to the first enhancement issue. Without this, T1 gets filtered out of the
        // cache on reload, breaking wave gating and dependency resolution for all W1+ tasks.
        t1 = tasks[0];
        if (!t1.ParentIssueNumber.HasValue && enhancementIssues.Count > 0)
        {
            tasks[0] = t1 with
            {
                ParentIssueNumber = enhancementIssues[0].Number,
                RelatedEnhancementNumbers = t1.RelatedEnhancementNumbers.Count > 0
                    ? t1.RelatedEnhancementNumbers
                    : enhancementIssues.Select(e => e.Number).ToList()
            };
            Logger.LogInformation(
                "Set foundation task T1 ParentIssueNumber to #{Parent} (first enhancement) for scope filter survival",
                enhancementIssues[0].Number);
        }

        // Ensure all other tasks depend on T1's ID
        var t1Id = tasks[0].Id;
        for (var i = 1; i < tasks.Count; i++)
        {
            var task = tasks[i];
            if (!task.Dependencies.Contains(t1Id, StringComparer.OrdinalIgnoreCase))
            {
                task.Dependencies.Insert(0, t1Id);
            }
        }
        // Note: File overlap detection is now handled by ValidateAndRepairTaskPlanAsync()
        // which runs after this method and uses AI-assisted repair.
    }

    /// <summary>
    /// Serializes tasks that touch the same files by chaining them with dependency links.
    /// If tasks T3, T5, T7 all touch the same file, this creates T5→T3, T7→T5 so they
    /// execute sequentially, preventing merge conflicts from parallel writes.
    /// Skips T1 (foundation) since all tasks already depend on it.
    /// </summary>
    private void SerializeOverlappingTasks(List<EngineeringTask> tasks)
    {
        if (tasks.Count <= 2) return;

        // Build a map of file → list of tasks (indices) that touch it
        var fileToTasks = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < tasks.Count; i++) // skip T1 (index 0)
        {
            var touchedFiles = ExtractTouchedFilesFromDescription(tasks[i].Description);
            foreach (var file in touchedFiles)
            {
                if (!fileToTasks.TryGetValue(file, out var list))
                {
                    list = new List<int>();
                    fileToTasks[file] = list;
                }
                list.Add(i);
            }
        }

        var chainsCreated = 0;
        foreach (var (file, taskIndices) in fileToTasks)
        {
            if (taskIndices.Count < 2) continue;

            // Chain tasks: each subsequent task depends on the previous one
            for (var j = 1; j < taskIndices.Count; j++)
            {
                var currentTask = tasks[taskIndices[j]];
                var previousTaskId = tasks[taskIndices[j - 1]].Id;

                if (!currentTask.Dependencies.Contains(previousTaskId, StringComparer.OrdinalIgnoreCase))
                {
                    currentTask.Dependencies.Add(previousTaskId);
                    chainsCreated++;
                }
            }
        }

        if (chainsCreated > 0)
        {
            Logger.LogInformation("SerializeOverlappingTasks: created {Count} dependency links to serialize file-overlapping tasks",
                chainsCreated);
        }
    }

    /// <summary>
    /// Extracts file paths referenced in a task description (both CREATE and MODIFY patterns).
    /// Parses markdown-style File Plan entries and raw "CREATE: path" / "MODIFY: path" patterns.
    /// </summary>
    private static List<string> ExtractTouchedFilesFromDescription(string description)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(description)) return files.ToList();

        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim();

            // Match "CREATE: path/to/file" or "MODIFY: path/to/file"
            if (trimmed.StartsWith("CREATE:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("MODIFY:", StringComparison.OrdinalIgnoreCase))
            {
                var path = trimmed[(trimmed.IndexOf(':') + 1)..].Trim().Trim('`', '"', '\'');
                if (!string.IsNullOrWhiteSpace(path))
                    files.Add(path);
            }
            // Match markdown "- ➕ **Create:** `path`" or "- ✏️ **Modify:** `path`"
            else if (trimmed.Contains("**Create:**") || trimmed.Contains("**Modify:**"))
            {
                var backtickStart = trimmed.IndexOf('`');
                var backtickEnd = trimmed.LastIndexOf('`');
                if (backtickStart >= 0 && backtickEnd > backtickStart)
                    files.Add(trimmed[(backtickStart + 1)..backtickEnd]);
            }
        }

        return files.ToList();
    }

    /// <summary>
    /// Extracts CREATE file paths from a task description's File Plan section.
    /// </summary>
    private static List<string> ExtractCreateFilesFromDescription(string description)
    {
        var files = new List<string>();
        // Match lines like "- ➕ **Create:** `path/to/file.cs`" or raw "CREATE:path/to/file.cs"
        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim();
            // Markdown format from FormatFilePlan
            if (trimmed.Contains("**Create:**"))
            {
                var backtickStart = trimmed.IndexOf('`');
                var backtickEnd = trimmed.LastIndexOf('`');
                if (backtickStart >= 0 && backtickEnd > backtickStart)
                    files.Add(trimmed[(backtickStart + 1)..backtickEnd]);
            }
        }
        return files;
    }

    private static string FormatFilePlan(string filePlan)
    {
        if (string.IsNullOrWhiteSpace(filePlan)) return "";

        var sb = new System.Text.StringBuilder();
        var ops = filePlan.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var op in ops)
        {
            var colonIdx = op.IndexOf(':');
            if (colonIdx <= 0) continue;

            var action = op[..colonIdx].Trim().ToUpperInvariant();
            var detail = op[(colonIdx + 1)..].Trim();

            var icon = action switch
            {
                "CREATE" => "➕ **Create:**",
                "MODIFY" => "✏️ **Modify:**",
                "USE" => "📎 **Reference (do not recreate):**",
                "SHARED" => "🔗 **Shared (multi-task):**",
                _ => $"**{action}:**"
            };

            sb.AppendLine($"- {icon} `{detail}`");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Assesses project complexity based on enhancement issue count and architecture document breadth.
    /// Returns a complexity label and recommended maximum task count.
    /// </summary>
    private (string Complexity, int TargetTaskCount) AssessProjectComplexity(
        int enhancementCount, string? architectureDoc)
    {
        // Secondary signal: architecture breadth (sections, components, endpoints mentioned)
        var archBreadth = 0;
        if (!string.IsNullOrWhiteSpace(architectureDoc))
        {
            // Count markdown headings as a proxy for architectural breadth
            archBreadth = architectureDoc.Split('\n')
                .Count(line => line.TrimStart().StartsWith('#'));
        }

        // Classification: primary = issue count, secondary = arch breadth
        if (enhancementCount <= 3 && archBreadth <= 10)
            return ("Small", 3);
        if (enhancementCount <= 7 && archBreadth <= 25)
            return ("Medium", 6);
        return ("Large", 10);
    }

    /// <summary>
    /// Post-parse normalization: merges undersized tasks, enforces task count cap,
    /// and ensures every task is a self-contained vertical slice.
    /// Must be called BEFORE CreateTaskIssuesAsync to prevent orphan issues.
    /// </summary>
    private void NormalizeTaskPlan(List<EngineeringTask> tasks, int targetTaskCount)
    {
        if (tasks.Count <= 2) return; // T1 + one task = nothing to normalize

        // Skip T1 (foundation) and T-FINAL (integration) — only normalize W1+ tasks
        var t1 = tasks.FirstOrDefault(t => t.Wave == "W0");
        var tFinal = tasks.FirstOrDefault(t =>
            string.Equals(t.Id, IntegrationTaskId, StringComparison.OrdinalIgnoreCase));
        var normalizableTasks = tasks
            .Where(t => t != t1 && t != tFinal)
            .ToList();

        if (normalizableTasks.Count <= targetTaskCount)
            return; // Already within budget

        // Strategy: merge the smallest tasks (by file count) into their closest dependency neighbor
        var mergeCount = normalizableTasks.Count - targetTaskCount;
        Logger.LogInformation(
            "NormalizeTaskPlan: {Current} tasks exceeds target {Target}, merging {MergeCount} smallest tasks",
            normalizableTasks.Count, targetTaskCount, mergeCount);

        for (var i = 0; i < mergeCount && normalizableTasks.Count > targetTaskCount; i++)
        {
            // Find the task with fewest owned files (smallest scope)
            var smallest = normalizableTasks
                .OrderBy(t => t.OwnedFiles.Count)
                .ThenBy(t => t.Description.Length)
                .First();

            // Find merge target: prefer a task in the same wave that shares a dependency
            var mergeTarget = FindMergeTarget(smallest, normalizableTasks, tasks);
            if (mergeTarget is null)
            {
                Logger.LogDebug("No suitable merge target for task {TaskId}, skipping", smallest.Id);
                normalizableTasks.Remove(smallest); // Don't retry this one
                continue;
            }

            // Perform the merge: absorb smallest into mergeTarget
            MergeTaskInto(smallest, mergeTarget, tasks);
            normalizableTasks.Remove(smallest);
        }

        LogActivity("planning",
            $"📐 Normalized task plan: merged {mergeCount} undersized tasks → {tasks.Count(t => t != tFinal)} total");
    }

    /// <summary>
    /// Finds the best merge target for a task being absorbed.
    /// Prefers: same wave > shared parent issue > first sibling in same wave.
    /// </summary>
    private static EngineeringTask? FindMergeTarget(
        EngineeringTask source, List<EngineeringTask> candidates, List<EngineeringTask> allTasks)
    {
        var others = candidates.Where(t => t != source).ToList();
        if (others.Count == 0) return null;

        // Prefer a task in the same wave that shares the same parent issue
        var sameWaveAndParent = others.FirstOrDefault(t =>
            t.Wave == source.Wave && t.ParentIssueNumber == source.ParentIssueNumber);
        if (sameWaveAndParent is not null) return sameWaveAndParent;

        // Next: same wave, any task
        var sameWave = others.FirstOrDefault(t => t.Wave == source.Wave);
        if (sameWave is not null) return sameWave;

        // Fallback: any task that this task depends on (absorb into dependency)
        foreach (var depId in source.Dependencies)
        {
            var dep = others.FirstOrDefault(t =>
                string.Equals(t.Id, depId, StringComparison.OrdinalIgnoreCase));
            if (dep is not null) return dep;
        }

        // Last resort: first available sibling
        return others.FirstOrDefault();
    }

    /// <summary>
    /// Merges source task into target: combines descriptions, files, skill tags, and
    /// rewires all dependency references from source.Id to target.Id throughout the plan.
    /// </summary>
    private void MergeTaskInto(EngineeringTask source, EngineeringTask target, List<EngineeringTask> allTasks)
    {
        Logger.LogInformation("Merging task {SourceId} ({SourceName}) into {TargetId} ({TargetName})",
            source.Id, source.Name, target.Id, target.Name);

        // Combine descriptions
        var mergedDesc = target.Description + $"\n\n---\n**Merged from {source.Id}: {source.Name}**\n{source.Description}";

        // Combine owned files
        var mergedFiles = target.OwnedFiles.Union(source.OwnedFiles).Distinct().ToList();

        // Combine skill tags
        var mergedTags = target.SkillTags.Union(source.SkillTags).Distinct().ToList();

        // Combine dependencies (target gains source's deps, minus self-references)
        var mergedDeps = target.Dependencies
            .Union(source.Dependencies)
            .Where(d => !string.Equals(d, target.Id, StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(d, source.Id, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Update target in-place (record with-expressions create copies, so update the list)
        var targetIdx = allTasks.IndexOf(target);
        if (targetIdx >= 0)
        {
            allTasks[targetIdx] = target with
            {
                Name = target.Name, // Keep target's name (it's the larger scope)
                Description = mergedDesc,
                OwnedFiles = mergedFiles,
                SkillTags = mergedTags,
                Dependencies = mergedDeps,
                // Elevate complexity if source was higher
                Complexity = ElevateComplexity(target.Complexity, source.Complexity)
            };
        }

        // Rewire: anywhere that depended on source now depends on target instead
        foreach (var task in allTasks)
        {
            if (task == source) continue;
            for (var d = 0; d < task.Dependencies.Count; d++)
            {
                if (string.Equals(task.Dependencies[d], source.Id, StringComparison.OrdinalIgnoreCase))
                {
                    task.Dependencies[d] = target.Id;
                }
            }
            // Also fix typed dependencies
            if (task.DependencyTypes.ContainsKey(source.Id))
            {
                var depType = task.DependencyTypes[source.Id];
                task.DependencyTypes.Remove(source.Id);
                task.DependencyTypes.TryAdd(target.Id, depType);
            }
        }

        // Remove source from the plan
        allTasks.Remove(source);
    }

    /// <summary>Returns the higher of two complexity levels.</summary>
    private static string ElevateComplexity(string a, string b)
    {
        var order = new[] { "Low", "Medium", "High" };
        var aIdx = Array.FindIndex(order, c => string.Equals(c, a, StringComparison.OrdinalIgnoreCase));
        var bIdx = Array.FindIndex(order, c => string.Equals(c, b, StringComparison.OrdinalIgnoreCase));
        return order[Math.Max(Math.Max(aIdx, 0), Math.Max(bIdx, 0))];
    }

    /// <summary>
    /// Verifies that all dependency PRs for a task have actually merged to main.
    /// Tasks with no dependencies or whose dependencies have no PR numbers pass automatically.
    /// This prevents assigning work before prerequisite code has landed.
    /// </summary>
    private bool AreDependencyPrsMerged(EngineeringTask task, HashSet<int> mergedPrNumbers)
    {
        if (task.DependencyIssueNumbers.Count == 0)
            return true;

        foreach (var depIssueNum in task.DependencyIssueNumbers)
        {
            var dep = _taskManager.FindByIssueNumber(depIssueNum);
            if (dep is null)
                continue; // Unknown dependency — allow (it may be external)

            if (!EngineeringTaskIssueManager.IsTaskDone(dep))
                continue; // Not done yet — AreDependenciesMet already filtered these

            // If the dependency has a known PR, verify it's actually merged
            if (dep.PullRequestNumber.HasValue && !mergedPrNumbers.Contains(dep.PullRequestNumber.Value))
            {
                Logger.LogDebug(
                    "{Role} {Name} task {TaskId} blocked: dependency PR #{DepPr} (issue #{DepIssue}) not yet merged",
                    Identity.Role, Identity.DisplayName, task.Id, dep.PullRequestNumber.Value, depIssueNum);
                return false;
            }
        }

        return true;
    }

    #endregion

    #region PE Parallelism Enhancements

    /// <summary>
    /// Normalizes a file path for consistent comparison:
    /// forward slashes, lowercase, no leading slash, no trailing slash.
    /// </summary>
    internal static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        // Strip namespace hint: "MyApp/File.cs(Namespace)" → "MyApp/File.cs"
        var parenIdx = path.IndexOf('(');
        if (parenIdx > 0) path = path[..parenIdx];
        return path.Trim().Replace('\\', '/').TrimStart('/').TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// Extracts all CREATE and MODIFY file paths from a raw FilePlan string (semicolon-separated ops).
    /// Returns normalized paths.
    /// </summary>
    internal static List<string> ExtractAllFilesFromFilePlan(string filePlan)
    {
        if (string.IsNullOrWhiteSpace(filePlan)) return new();
        var files = new List<string>();
        var ops = filePlan.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var op in ops)
        {
            var colonIdx = op.IndexOf(':');
            if (colonIdx <= 0) continue;
            var action = op[..colonIdx].Trim().ToUpperInvariant();
            if (action is "CREATE" or "MODIFY")
            {
                var file = NormalizeFilePath(op[(colonIdx + 1)..]);
                if (!string.IsNullOrEmpty(file))
                    files.Add(file);
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Extracts SHARED file declarations from T1's FilePlan.
    /// These are files explicitly allowed to be modified by multiple tasks.
    /// </summary>
    internal static HashSet<string> ExtractSharedFilesFromFilePlan(string filePlan)
    {
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(filePlan)) return shared;
        var ops = filePlan.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var op in ops)
        {
            var colonIdx = op.IndexOf(':');
            if (colonIdx <= 0) continue;
            var action = op[..colonIdx].Trim().ToUpperInvariant();
            if (action == "SHARED")
            {
                var file = NormalizeFilePath(op[(colonIdx + 1)..]);
                if (!string.IsNullOrEmpty(file))
                    shared.Add(file);
            }
        }
        return shared;
    }

    /// <summary>
    /// Well-known infrastructure files that are inherently shared and should not trigger overlap errors.
    /// These files are commonly modified by multiple tasks (build config, solution files, etc.).
    /// </summary>
    /// <summary>
    /// Well-known infrastructure files that are excluded from overlap detection.
    /// These are commonly modified by multiple tasks (build config, manifests, dependency files)
    /// and conflicts on them are typically resolvable additively. The list spans all major
    /// stacks the system supports so non-.NET projects don't generate false-positive overlaps.
    /// </summary>
    private static readonly HashSet<string> InfrastructureFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // Universal
        ".gitignore", ".gitattributes", ".editorconfig", "readme.md", "license", "license.md", "license.txt",
        // Container / CI
        "docker-compose.yml", "docker-compose.yaml", "dockerfile", ".dockerignore",
        // .NET
        "directory.build.props", "directory.build.targets", "directory.packages.props",
        "global.json", "nuget.config",
        // Node / TypeScript
        "package.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "tsconfig.json",
        ".npmrc", ".nvmrc", ".prettierrc", ".eslintrc", ".eslintrc.json", ".eslintrc.js",
        // Python
        "requirements.txt", "requirements-dev.txt", "setup.py", "setup.cfg", "pyproject.toml",
        "pipfile", "pipfile.lock", "poetry.lock",
        // Go
        "go.mod", "go.sum",
        // Rust
        "cargo.toml", "cargo.lock",
        // Java / JVM
        "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts",
        "gradle.properties",
        // Ruby
        "gemfile", "gemfile.lock", ".ruby-version",
        // PHP
        "composer.json", "composer.lock",
    };

    /// <summary>
    /// Detects file ownership overlaps between tasks.
    /// Returns a dictionary of file → list of task IDs that touch it.
    /// Excludes shared files (declared in T1's FilePlan) and well-known infrastructure files.
    /// </summary>
    internal static Dictionary<string, List<string>> DetectFileOverlaps(
        List<EngineeringTask> tasks, HashSet<string> sharedFiles)
    {
        var fileOwnership = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            foreach (var file in task.OwnedFiles)
            {
                // Skip shared files and infrastructure files
                if (sharedFiles.Contains(file)) continue;
                var fileName = Path.GetFileName(file);
                if (InfrastructureFiles.Contains(fileName)) continue;

                if (!fileOwnership.TryGetValue(file, out var owners))
                {
                    owners = new List<string>();
                    fileOwnership[file] = owners;
                }
                if (!owners.Contains(task.Id, StringComparer.OrdinalIgnoreCase))
                    owners.Add(task.Id);
            }
        }
        // Return only files with >1 owner
        return fileOwnership
            .Where(kv => kv.Value.Count > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates and repairs file overlaps in the task plan using AI-assisted fixing.
    /// If overlaps remain after AI retries, fails with a replan request rather than silently stealing files.
    /// </summary>
    private async Task ValidateAndRepairTaskPlanAsync(
        List<EngineeringTask> tasks,
        IChatCompletionService chat,
        CancellationToken ct)
    {
        // Extract shared files from T1 (foundation task)
        var t1 = tasks.FirstOrDefault();
        var t1FilePlan = ExtractRawFilePlanFromDescription(t1?.Description ?? "");
        var sharedFiles = ExtractSharedFilesFromFilePlan(t1FilePlan);

        if (sharedFiles.Count > 0)
            Logger.LogInformation("Shared file registry from T1: {SharedFiles}",
                string.Join(", ", sharedFiles));

        var overlaps = DetectFileOverlaps(tasks, sharedFiles);
        if (overlaps.Count == 0)
        {
            Logger.LogInformation("No file overlaps detected — plan is parallel-safe");
            return;
        }

        Logger.LogWarning("File overlaps detected: {Count} files shared across tasks. Attempting AI-assisted repair",
            overlaps.Count);

        const int maxRetries = 2;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var overlapSummary = string.Join("\n", overlaps.Select(kv =>
                $"  - `{kv.Key}` owned by: {string.Join(", ", kv.Value)}"));

            var taskSummary = string.Join("\n", tasks.Select(t =>
                $"  {t.Id} ({t.Wave}): {t.Name} — Files: [{string.Join(", ", t.OwnedFiles)}]"));

            var fixPrompt =
                "The following engineering tasks have FILE OVERLAPS — multiple tasks create/modify the same file. " +
                "This causes merge conflicts when engineers work in parallel.\n\n" +
                $"## Overlapping Files\n{overlapSummary}\n\n" +
                $"## Current Tasks\n{taskSummary}\n\n" +
                "Fix this by:\n" +
                "1. Reassigning the disputed file to ONE task only\n" +
                "2. If a task loses a file, update its description to use the OTHER task's output instead\n" +
                "3. If a file truly MUST be shared, add it as SHARED in T1's FilePlan\n\n" +
                "Output the CORRECTED task lines in TASK format (same as before):\n" +
                "TASK|<ID>|<IssueNumber>|<Name>|<Description>|<Complexity>|<Dependencies>|<FilePlan>|<Wave>\n\n" +
                "Only output corrected TASK lines for tasks that CHANGED. Keep unchanged tasks as-is.";

            var fixHistory = CreateChatHistory();
            fixHistory.AddSystemMessage("You are a Software Engineer fixing file ownership conflicts in an engineering plan. " +
                "Each file should be owned by exactly one task unless explicitly declared SHARED.");
            fixHistory.AddUserMessage(fixPrompt);

            try
            {
                var fixResponse = await chat.GetChatMessageContentAsync(fixHistory, cancellationToken: ct);
                var fixes = fixResponse.Content ?? "";

                // Parse corrected tasks and merge them into the existing list
                var fixedCount = ApplyTaskFixes(tasks, fixes);
                Logger.LogInformation("AI overlap repair attempt {Attempt}: {FixedCount} tasks updated",
                    attempt, fixedCount);

                // Re-detect overlaps
                sharedFiles = ExtractSharedFilesFromFilePlan(
                    ExtractRawFilePlanFromDescription(tasks.FirstOrDefault()?.Description ?? ""));
                overlaps = DetectFileOverlaps(tasks, sharedFiles);

                if (overlaps.Count == 0)
                {
                    Logger.LogInformation("File overlaps resolved after {Attempt} AI repair attempt(s)", attempt);
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "AI overlap repair attempt {Attempt} failed", attempt);
            }
        }

        // After max retries, log the remaining overlaps as warnings (don't silently reassign)
        Logger.LogWarning(
            "File overlaps still present after {MaxRetries} AI repair attempts. " +
            "Remaining overlaps: {Overlaps}. Proceeding with warning — engineers may encounter merge conflicts",
            maxRetries,
            string.Join("; ", overlaps.Select(kv => $"{kv.Key} → [{string.Join(",", kv.Value)}]")));
    }

    /// <summary>
    /// Applies AI-generated task fixes to the existing task list.
    /// Returns the number of tasks that were updated.
    /// </summary>
    private int ApplyTaskFixes(List<EngineeringTask> tasks, string aiResponse)
    {
        var fixedCount = 0;
        foreach (var line in aiResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("TASK|", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split('|');
            if (parts.Length < 7) continue;

            var taskId = parts[1].Trim();
            var existingIdx = tasks.FindIndex(t =>
                string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
            if (existingIdx < 0) continue;

            var filePlan = parts.Length >= 8 ? parts[7].Trim() : "";
            var wave = parts.Length >= 9 ? parts[8].Trim() : tasks[existingIdx].Wave;
            var ownedFiles = ExtractAllFilesFromFilePlan(filePlan);

            var deps = parts[6].Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? new List<string>()
                : parts[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var (plainDeps, depTypes) = ParseTypedDependencies(deps);

            tasks[existingIdx] = tasks[existingIdx] with
            {
                Name = parts[3].Trim(),
                Description = parts[4].Trim() + (string.IsNullOrEmpty(filePlan) ? "" :
                    $"\n\n### File Plan\n{FormatFilePlan(filePlan)}"),
                Complexity = NormalizeComplexity(parts[5].Trim()),
                Dependencies = plainDeps,
                DependencyTypes = depTypes,
                Wave = wave,
                OwnedFiles = ownedFiles
            };
            fixedCount++;
        }
        return fixedCount;
    }

    /// <summary>
    /// Extracts the raw FilePlan string from a task description that has already been formatted.
    /// Looks for the "### File Plan" section and reconstructs the semicolon-separated operations.
    /// </summary>
    private static string ExtractRawFilePlanFromDescription(string description)
    {
        var sb = new StringBuilder();
        var inFilePlan = false;
        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == "### File Plan")
            {
                inFilePlan = true;
                continue;
            }
            if (inFilePlan)
            {
                if (trimmed.StartsWith("###") || string.IsNullOrWhiteSpace(trimmed))
                    break;
                // Parse "- ➕ **Create:** `path`" back to "CREATE:path"
                if (trimmed.Contains("**Create:**"))
                    AppendExtractedOp(sb, trimmed, "CREATE");
                else if (trimmed.Contains("**Modify:**"))
                    AppendExtractedOp(sb, trimmed, "MODIFY");
                else if (trimmed.Contains("**Shared (multi-task):**"))
                    AppendExtractedOp(sb, trimmed, "SHARED");
                else if (trimmed.Contains("**Reference"))
                    AppendExtractedOp(sb, trimmed, "USE");
            }
        }
        return sb.ToString().TrimEnd(';');
    }

    private static void AppendExtractedOp(StringBuilder sb, string line, string action)
    {
        var backtickStart = line.IndexOf('`');
        var backtickEnd = line.LastIndexOf('`');
        if (backtickStart >= 0 && backtickEnd > backtickStart)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append($"{action}:{line[(backtickStart + 1)..backtickEnd]}");
        }
    }

    /// <summary>
    /// Validates wave assignments in the task plan.
    /// Checks that at least 60% of non-foundation tasks are in W1 (parallelizable).
    /// Logs warnings if validation fails but does not reject the plan.
    /// </summary>
    internal bool ValidateWaves(List<EngineeringTask> tasks)
    {
        if (tasks.Count <= 1) return true;

        // Skip foundation task (T1) from wave analysis
        var nonFoundationTasks = tasks.Skip(1).ToList();
        if (nonFoundationTasks.Count == 0) return true;

        var w1Count = nonFoundationTasks.Count(t =>
            string.Equals(t.Wave, "W1", StringComparison.OrdinalIgnoreCase));
        var w1Percentage = (double)w1Count / nonFoundationTasks.Count * 100;

        Logger.LogInformation(
            "Wave analysis: {W1Count}/{Total} non-foundation tasks in W1 ({Percentage:F0}%)",
            w1Count, nonFoundationTasks.Count, w1Percentage);

        // Log wave breakdown
        var waveGroups = nonFoundationTasks
            .GroupBy(t => t.Wave, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key);
        foreach (var group in waveGroups)
        {
            Logger.LogInformation("  Wave {Wave}: {Tasks}",
                group.Key,
                string.Join(", ", group.Select(t => $"{t.Id}:{t.Name}")));
        }

        // Validate: W2+ tasks should not depend on other W2+ tasks from the same wave
        var waveViolations = new List<string>();
        foreach (var task in nonFoundationTasks)
        {
            if (string.Equals(task.Wave, "W1", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var depId in task.Dependencies)
            {
                var depTask = tasks.FirstOrDefault(t =>
                    string.Equals(t.Id, depId, StringComparison.OrdinalIgnoreCase));
                if (depTask is not null &&
                    string.Equals(depTask.Wave, task.Wave, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(depTask.Id, tasks[0].Id, StringComparison.OrdinalIgnoreCase))
                {
                    waveViolations.Add($"{task.Id}({task.Wave}) depends on {depTask.Id}({depTask.Wave})");
                }
            }
        }

        if (waveViolations.Count > 0)
        {
            Logger.LogWarning(
                "Wave ordering violations — tasks in the same wave depend on each other: {Violations}",
                string.Join("; ", waveViolations));
        }

        if (w1Percentage < 60)
        {
            Logger.LogWarning(
                "Low W1 parallelism: only {Percentage:F0}% of tasks in W1 (target: 60%+). " +
                "Consider restructuring tasks to reduce inter-task dependencies",
                w1Percentage);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses typed dependencies like "T1(files),T3(api)" into plain dep IDs and a type map.
    /// Plain format "T1,T3" is also supported (no type annotation → "full" dependency).
    /// </summary>
    internal static (List<string> PlainDeps, Dictionary<string, string> DepTypes) ParseTypedDependencies(
        List<string> rawDeps)
    {
        var plainDeps = new List<string>();
        var depTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dep in rawDeps)
        {
            var trimmed = dep.Trim();
            var parenStart = trimmed.IndexOf('(');
            if (parenStart > 0 && trimmed.EndsWith(')'))
            {
                var taskId = trimmed[..parenStart].Trim();
                var depType = trimmed[(parenStart + 1)..^1].Trim().ToLowerInvariant();
                plainDeps.Add(taskId);
                depTypes[taskId] = depType;
            }
            else
            {
                plainDeps.Add(trimmed);
                // No type annotation = full dependency
            }
        }

        return (plainDeps, depTypes);
    }

    /// <summary>
    /// Determines if a typed dependency can be relaxed (i.e., the dependent task can proceed
    /// even though the dependency isn't fully complete).
    /// Currently supports: "api" deps can proceed if the interface is declared in T1's shared files.
    /// </summary>
    internal static bool CanRelaxDependency(string depType, EngineeringTask depTask, HashSet<string> sharedFiles)
    {
        return depType.ToLowerInvariant() switch
        {
            // API dependency: can proceed if we have the interface contract from T1
            "api" or "interface" => depTask.Wave == "W1" || sharedFiles.Count > 0,
            // Schema dependency: can proceed if the schema is defined in T1
            "schema" or "model" => string.Equals(depTask.Id, "T1", StringComparison.OrdinalIgnoreCase),
            // File dependency: cannot be relaxed — must wait for actual file
            "files" or "file" => false,
            // Unknown type: treat as full dependency (cannot relax)
            _ => false
        };
    }

    /// <summary>
    /// Logs parallelism metrics for dashboard/monitoring.
    /// </summary>
    private void LogParallelismMetrics(List<EngineeringTask> tasks, Dictionary<string, List<string>> overlaps)
    {
        var nonFoundation = tasks.Count > 1 ? tasks.Skip(1).ToList() : tasks;
        var w1Count = nonFoundation.Count(t =>
            string.Equals(t.Wave, "W1", StringComparison.OrdinalIgnoreCase));
        var totalFiles = tasks.Sum(t => t.OwnedFiles.Count);
        var sharedFileCount = tasks.Count > 0
            ? ExtractSharedFilesFromFilePlan(
                ExtractRawFilePlanFromDescription(tasks[0].Description)).Count
            : 0;

        Logger.LogInformation(
            "📊 Parallelism metrics: {TaskCount} tasks, {W1Count} in W1 ({W1Pct:F0}%), " +
            "{FileCount} total files, {SharedCount} shared files, {OverlapCount} remaining overlaps",
            tasks.Count, w1Count,
            nonFoundation.Count > 0 ? (double)w1Count / nonFoundation.Count * 100 : 100,
            totalFiles, sharedFileCount, overlaps.Count);

        LogActivity("task", $"📊 Plan parallelism: {w1Count}/{nonFoundation.Count} tasks in W1, " +
            $"{totalFiles} files planned, {overlaps.Count} overlaps");
    }
    private async Task<string?> ReadDesignReferencesAsync(CancellationToken ct)
    {
        // Delegates to the base implementation so HTML + PNG/JPG discovery stays in one place.
        // GetDesignContextAsync caches both the rendered markdown AND the binary image bytes so
        // the plan-generation call site can attach images via AddUserMessageWithDesignImages.
        return await GetDesignContextAsync(ct);
    }

    #endregion

    #region Stub Marker Scanning

    /// <summary>
    /// Scans the workspace for remaining AI_STUB and AI_TODO markers that should have been
    /// replaced by later tasks. Returns a formatted report, or null/empty if none found.
    /// </summary>
    private async Task<string?> ScanForRemainingStubMarkersAsync(CancellationToken ct)
    {
        if (Workspace == null) return null;

        var workspacePath = Workspace.RepoPath;
        if (string.IsNullOrEmpty(workspacePath) || !Directory.Exists(workspacePath))
            return null;

        var markers = new List<(string File, int Line, string Text)>();

        // Source file extensions to scan
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".java", ".rs",
            ".razor", ".cshtml", ".html", ".css", ".scss", ".json", ".yaml", ".yml",
            ".xml", ".csproj", ".fsproj", ".md"
        };

        // Directories to skip
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", ".agents", ".candidates",
            ".candidates-eval", ".squad", "dist", "build", "coverage", "wwwroot"
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(workspacePath, "*.*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var ext = Path.GetExtension(file);
                if (!extensions.Contains(ext)) continue;

                // Skip excluded directories
                var relativePath = Path.GetRelativePath(workspacePath, file);
                var pathParts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (pathParts.Any(p => skipDirs.Contains(p))) continue;

                var lines = await File.ReadAllLinesAsync(file, ct);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.Contains("AI_STUB", StringComparison.Ordinal) ||
                        line.Contains("AI_TODO", StringComparison.Ordinal))
                    {
                        markers.Add((relativePath.Replace('\\', '/'), i + 1, line.Trim()));
                    }
                }

                // Cap to avoid scanning enormous codebases forever
                if (markers.Count >= 200) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Stub marker scan encountered an error");
        }

        if (markers.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found **{markers.Count}** remaining stub marker(s):");
        sb.AppendLine();
        sb.AppendLine("| File | Line | Marker |");
        sb.AppendLine("|------|------|--------|");
        foreach (var (file, line, text) in markers.Take(100))
        {
            // Truncate long marker text for readability
            var displayText = text.Length > 120 ? text[..117] + "..." : text;
            sb.AppendLine($"| `{file}` | {line} | `{displayText}` |");
        }
        if (markers.Count > 100)
            sb.AppendLine($"| ... | ... | *{markers.Count - 100} more markers not shown* |");

        return sb.ToString();
    }

    #endregion
}