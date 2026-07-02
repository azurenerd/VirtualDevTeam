using System.Text;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Frameworks;
using VirtualDevTeam.Core.Prompts;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Agents.AI;

/// <summary>
/// Real <see cref="IBaselineCodeGenerator"/> implementation. Mirrors
/// <see cref="SoftwareEngineerAgent"/>'s single-pass code-gen path: build a system + user
/// prompt via <see cref="SinglePassPromptBuilder"/>, invoke the SE-tier kernel, parse FILE:
/// blocks with <see cref="CodeFileParser"/>, and write each parsed file into the supplied
/// worktree path.
///
/// Path containment: every output path is resolved with <see cref="Path.GetFullPath(string)"/>
/// and rejected if it escapes the worktree root. The generator never commits — the
/// orchestrator's <c>git diff</c>-based patch extraction picks up the untracked files
/// after a <c>git add -A</c> sweep.
///
/// No build verification here: <c>CandidateEvaluator</c> already runs a build gate against
/// every candidate, and the SE re-builds after applying the winner.
/// </summary>
public class BaselineCodeGenerator : IBaselineCodeGenerator
{
    private readonly ModelRegistry _models;
    private readonly IPromptTemplateService? _promptService;
    private readonly VirtualDevTeamConfig _config;
    private readonly ILogger<BaselineCodeGenerator> _logger;

    public BaselineCodeGenerator(
        ModelRegistry models,
        IOptions<VirtualDevTeamConfig> config,
        ILogger<BaselineCodeGenerator> logger,
        IPromptTemplateService? promptService = null)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _promptService = promptService;
    }

    public async Task<BaselineGenerationOutcome> GenerateAsync(
        string worktreePath, TaskContext task, CancellationToken ct,
        string strategyTag = "baseline-strategy",
        IProgress<FrameworkActivityEvent>? activitySink = null,
        RevisionContext? revision = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            return Fail("worktree path missing");
        if (!Directory.Exists(worktreePath))
            return Fail($"worktree path does not exist: {worktreePath}");

        var rootFull = NormalizeDir(Path.GetFullPath(worktreePath));
        var techStack = string.IsNullOrWhiteSpace(task.TechStack)
            ? (_config.Project?.TechStack ?? "")
            : task.TechStack;
        var tier = _config.Agents?.SoftwareEngineer?.ModelTier ?? "premium";

        activitySink?.Report(new FrameworkActivityEvent("init",
            $"Validating worktree: {Path.GetFileName(worktreePath.TrimEnd(Path.DirectorySeparatorChar))}"));
        activitySink?.Report(new FrameworkActivityEvent("init",
            $"Task: {task.TaskTitle} (id: {task.TaskId})"));
        activitySink?.Report(new FrameworkActivityEvent("config",
            $"Tech stack: {(string.IsNullOrWhiteSpace(techStack) ? "(default)" : techStack)}"));
        activitySink?.Report(new FrameworkActivityEvent("config",
            $"Model tier: {tier} — resolving kernel"));

        Kernel kernel;
        try
        {
            // Per-task agentId for traceability in copilot-cli session metadata. The cache
            // is keyed by tier under the hood, so this doesn't multiply kernel instances.
            kernel = _models.GetKernel(tier, $"{strategyTag}/{task.TaskId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BaselineCodeGenerator could not get kernel for tier {Tier}", tier);
            activitySink?.Report(new FrameworkActivityEvent("error", $"Kernel resolve failed: {ex.Message}"));
            return Fail($"kernel-resolve: {ex.Message}");
        }

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        activitySink?.Report(new FrameworkActivityEvent("init", "Kernel resolved, building prompts"));

        activitySink?.Report(new FrameworkActivityEvent("prompt", "Building system prompt (role definition + output format rules)"));
        var systemPrompt = await SinglePassPromptBuilder.BuildSystemPromptAsync(techStack, _promptService, ct);
        activitySink?.Report(new FrameworkActivityEvent("prompt",
            $"System prompt ready ({systemPrompt.Length:N0} chars)"));

        activitySink?.Report(new FrameworkActivityEvent("prompt", "Building user prompt with task context"));
        var userPrompt = await SinglePassPromptBuilder.BuildUserPromptAsync(
            new SinglePassPromptInputs
            {
                TaskName = task.TaskTitle,
                TaskDescription = task.TaskDescription,
                TechStack = techStack,
                PmSpec = task.PmSpec,
                Architecture = task.Architecture,
                IssueContext = task.IssueContext,
                DesignContext = task.DesignContext,
            }, _promptService, ct);

        // Report which context sections were included
        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.PmSpec)) contextParts.Add("PMSpec");
        if (!string.IsNullOrWhiteSpace(task.Architecture)) contextParts.Add("Architecture");
        if (!string.IsNullOrWhiteSpace(task.IssueContext)) contextParts.Add("Issue");
        if (!string.IsNullOrWhiteSpace(task.DesignContext)) contextParts.Add("Design");
        activitySink?.Report(new FrameworkActivityEvent("prompt",
            $"User prompt ready ({userPrompt.Length:N0} chars) — context: {(contextParts.Count > 0 ? string.Join(", ", contextParts) : "task only")}"));

        // ── Revision round: surgical SEARCH/REPLACE edits (not full file regeneration) ──
        if (revision is not null)
        {
            return await RunSurgicalRevisionAsync(
                rootFull, worktreePath, task, revision, chat, tier, activitySink, ct);
        }

        // ── Initial generation: full FILE: block output ──

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        activitySink?.Report(new FrameworkActivityEvent("llm",
            $"Sending to LLM ({tier}) — single-pass code generation"));
        activitySink?.Report(new FrameworkActivityEvent("llm",
            $"Total prompt: {systemPrompt.Length + userPrompt.Length:N0} chars (system + user)"));

        var llmStopwatch = System.Diagnostics.Stopwatch.StartNew();
        string responseText;
        try
        {
            var response = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            responseText = response.Content?.Trim() ?? "";
            llmStopwatch.Stop();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            llmStopwatch.Stop();
            _logger.LogWarning(ex, "BaselineCodeGenerator chat call threw for task {TaskId}", task.TaskId);
            activitySink?.Report(new FrameworkActivityEvent("error",
                $"LLM call failed after {llmStopwatch.Elapsed.TotalSeconds:F1}s: {ex.GetType().Name}"));
            return Fail($"chat-exception: {ex.GetType().Name}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            activitySink?.Report(new FrameworkActivityEvent("error", "LLM returned empty response"));
            return Fail("model returned empty response");
        }

        activitySink?.Report(new FrameworkActivityEvent("llm",
            $"Response received: {responseText.Length:N0} chars in {llmStopwatch.Elapsed.TotalSeconds:F1}s"));

        activitySink?.Report(new FrameworkActivityEvent("parse", "Parsing FILE: blocks from LLM response"));
        var parsedFiles = CodeFileParser.ParseFiles(responseText);
        if (parsedFiles.Count == 0)
        {
            _logger.LogWarning(
                "BaselineCodeGenerator parsed 0 FILE: blocks for task {TaskId} (response length: {Len})",
                task.TaskId, responseText.Length);
            activitySink?.Report(new FrameworkActivityEvent("error",
                $"No FILE: markers found in {responseText.Length:N0}-char response"));
            return Fail("parser produced 0 files (no FILE: markers in response)");
        }

        activitySink?.Report(new FrameworkActivityEvent("parse",
            $"Found {parsedFiles.Count} file(s) — writing to worktree"));

        var written = 0;
        var skipped = 0;
        foreach (var file in parsedFiles)
        {
            ct.ThrowIfCancellationRequested();

            var rel = (file.Path ?? "").Trim();
            if (string.IsNullOrEmpty(rel))
            {
                _logger.LogDebug("BaselineCodeGenerator skipping file with empty path");
                skipped++;
                continue;
            }

            // Reject absolute paths up front — Path.Combine would silently switch roots.
            if (Path.IsPathRooted(rel))
            {
                _logger.LogWarning(
                    "BaselineCodeGenerator rejecting absolute output path '{Path}' for task {TaskId}",
                    rel, task.TaskId);
                activitySink?.Report(new FrameworkActivityEvent("security",
                    $"Rejected absolute path: {rel}"));
                skipped++;
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(worktreePath, rel));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "BaselineCodeGenerator could not resolve path '{Path}' for task {TaskId}", rel, task.TaskId);
                skipped++;
                continue;
            }

            // Path-containment guard — defends against ".." segments, symlink-style escapes,
            // and any other input that resolves outside the candidate worktree.
            if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "BaselineCodeGenerator rejecting path-escape attempt: '{Path}' resolves to '{Full}' outside '{Root}'",
                    rel, fullPath, rootFull);
                activitySink?.Report(new FrameworkActivityEvent("security",
                    $"Rejected path escape: {rel}"));
                skipped++;
                continue;
            }

            // Reparse-point / symlink guard — Path.GetFullPath only proves LEXICAL
            // containment. A pre-existing symlink or junction on the filesystem chain
            // could still redirect our write outside the worktree. Walk the ancestor
            // chain (between rootFull and fullPath) and reject the write if any
            // component is a reparse point.
            if (ContainsReparsePoint(rootFull, fullPath))
            {
                _logger.LogWarning(
                    "BaselineCodeGenerator rejecting write through reparse point: '{Path}' (full '{Full}')",
                    rel, fullPath);
                activitySink?.Report(new FrameworkActivityEvent("security",
                    $"Rejected reparse-point path: {rel}"));
                skipped++;
                continue;
            }

            try
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(fullPath, file.Content ?? "", ct);
                written++;
                var lineCount = (file.Content ?? "").Split('\n').Length;
                activitySink?.Report(new FrameworkActivityEvent("write",
                    $"✓ {rel} ({lineCount} lines)"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "BaselineCodeGenerator failed to write '{Path}' for task {TaskId}", rel, task.TaskId);
                activitySink?.Report(new FrameworkActivityEvent("error",
                    $"Failed to write {rel}: {ex.Message}"));
                skipped++;
            }
        }

        if (written == 0)
        {
            activitySink?.Report(new FrameworkActivityEvent("error",
                $"All {parsedFiles.Count} file(s) rejected — 0 written"));
            return Fail($"all {parsedFiles.Count} parsed file(s) rejected by path containment or write errors");
        }

        _logger.LogInformation(
            "BaselineCodeGenerator wrote {Written}/{Total} file(s) for task {TaskId}",
            written, parsedFiles.Count, task.TaskId);

        activitySink?.Report(new FrameworkActivityEvent("complete",
            $"Done: {written}/{parsedFiles.Count} file(s) written" +
            $"{(skipped > 0 ? $", {skipped} skipped" : "")}"));

        return new BaselineGenerationOutcome
        {
            Succeeded = true,
            FilesWritten = written,
        };
    }

    /// <summary>
    /// Revision round: uses the Copilot CLI's native edit tools to make surgical changes
    /// to existing files in the worktree. Instead of regenerating everything via FILE: blocks,
    /// we enable AllowFileEdits in the invocation context so the CLI can use its built-in
    /// edit/create/view/grep tools — exactly like an interactive Copilot CLI session.
    /// </summary>
    private async Task<BaselineGenerationOutcome> RunSurgicalRevisionAsync(
        string rootFull, string worktreePath, TaskContext task, RevisionContext revision,
        IChatCompletionService chat, string tier,
        IProgress<FrameworkActivityEvent>? activitySink, CancellationToken ct)
    {
        activitySink?.Report(new FrameworkActivityEvent("revision",
            "Surgical revision mode — CLI will use native edit tools"));

        // Enumerate existing files so the CLI knows what to work with
        var existingFiles = Directory.EnumerateFiles(worktreePath, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
            .Select(f => Path.GetRelativePath(worktreePath, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        activitySink?.Report(new FrameworkActivityEvent("revision",
            $"Worktree has {existingFiles.Count} files to work with"));

        // Build a focused revision prompt with judge feedback
        var prompt = new StringBuilder();
        prompt.AppendLine("# Surgical Code Revision");
        prompt.AppendLine();
        prompt.AppendLine($"## Task: {task.TaskTitle}");
        prompt.AppendLine(task.TaskDescription);
        prompt.AppendLine();

        // Score feedback per dimension
        prompt.AppendLine("## Judge Feedback (fix these issues)");
        prompt.AppendLine();
        foreach (var (axis, score) in revision.InitialScores.OrderBy(kv => kv.Key))
        {
            prompt.AppendLine($"- **{axis}**: {score}/10");
        }
        prompt.AppendLine();

        if (!string.IsNullOrWhiteSpace(revision.AcFeedback))
            prompt.AppendLine($"### Acceptance Criteria Feedback\n{revision.AcFeedback}\n");
        if (!string.IsNullOrWhiteSpace(revision.DesignFeedback))
            prompt.AppendLine($"### Design Feedback\n{revision.DesignFeedback}\n");
        if (!string.IsNullOrWhiteSpace(revision.ReadabilityFeedback))
            prompt.AppendLine($"### Readability Feedback\n{revision.ReadabilityFeedback}\n");
        if (!string.IsNullOrWhiteSpace(revision.VisualsFeedback))
            prompt.AppendLine($"### Visuals Feedback\n{revision.VisualsFeedback}\n");

        prompt.AppendLine($"### Overall Judge Feedback\n{revision.JudgeFeedback}\n");
        prompt.AppendLine($"### Rubber-Duck Critique\n{revision.RubberDuckFeedback}\n");

        // File listing so the CLI knows what's available
        prompt.AppendLine("## Files in the project (use view/grep to read, edit to modify)");
        prompt.AppendLine("```");
        foreach (var file in existingFiles)
        {
            prompt.AppendLine(file);
        }
        prompt.AppendLine("```");
        prompt.AppendLine();

        prompt.AppendLine("## Instructions");
        prompt.AppendLine("1. Read the judge feedback carefully — it tells you exactly what to fix.");
        prompt.AppendLine("2. Use your view tool to read relevant files and understand the current code.");
        prompt.AppendLine("3. Use your edit tool to make ONLY the specific changes needed. Do NOT rewrite entire files.");
        prompt.AppendLine("4. Focus on the lowest-scoring dimensions first — those need the most improvement.");
        prompt.AppendLine("5. If a file needs a new import/using, add just that line.");
        prompt.AppendLine("6. If the judge says acceptance criteria are missing, add only the missing functionality.");
        prompt.AppendLine("7. After all edits, briefly summarize what you changed.");

        var history = new ChatHistory();
        history.AddUserMessage(prompt.ToString());

        activitySink?.Report(new FrameworkActivityEvent("llm",
            $"Sending revision prompt to LLM ({tier}) — CLI will edit files directly"));
        activitySink?.Report(new FrameworkActivityEvent("llm",
            $"Revision prompt: {prompt.Length:N0} chars"));

        // Push invocation context with file-edit permission and worktree CWD
        var invocationCtx = new CopilotCliInvocationContext(
            AllowFileEdits: true,
            OverrideWorkingDirectory: worktreePath);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string responseText;
        try
        {
            using var _ = AgentCallContext.PushInvocationContext(invocationCtx);
            var result = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            responseText = string.Join('\n', result.Select(r => r.Content ?? ""));
        }
        catch (OperationCanceledException)
        {
            activitySink?.Report(new FrameworkActivityEvent("error", "Revision cancelled"));
            return Fail("revision-cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Surgical revision LLM call failed");
            activitySink?.Report(new FrameworkActivityEvent("error", $"Revision LLM error: {ex.Message}"));
            return Fail($"revision-llm: {ex.Message}");
        }
        sw.Stop();

        activitySink?.Report(new FrameworkActivityEvent("revision",
            $"CLI revision completed in {sw.Elapsed.TotalSeconds:F1}s"));

        // The CLI edited files directly in the worktree via its native tools.
        // Count what changed by checking file modification times.
        var modifiedCount = 0;
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-5); // generous window
            modifiedCount = Directory.EnumerateFiles(worktreePath, "*", SearchOption.AllDirectories)
                .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                         && File.GetLastWriteTimeUtc(f) > cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count modified files after revision");
        }

        activitySink?.Report(new FrameworkActivityEvent("complete",
            $"Surgical revision done: ~{modifiedCount} file(s) touched"));

        _logger.LogInformation(
            "Surgical revision completed for {Task} — {Files} files touched in {Elapsed:F1}s",
            task.TaskId, modifiedCount, sw.Elapsed.TotalSeconds);

        return new BaselineGenerationOutcome
        {
            Succeeded = true,
            FilesWritten = modifiedCount,
        };
    }

    private static BaselineGenerationOutcome Fail(string reason) =>
        new() { Succeeded = false, FailureReason = reason };

    private static string NormalizeDir(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    // Walk from rootFull to (and including) fullPath's parent and return true if any
    // existing filesystem component is a reparse point (symlink, junction). The target
    // file itself hasn't been created yet, so we only need to check its ancestors.
    // Returns true on "unknown" (I/O error) to fail closed.
    private static bool ContainsReparsePoint(string rootFull, string fullPath)
    {
        try
        {
            var root = new DirectoryInfo(rootFull);
            var parent = new DirectoryInfo(Path.GetDirectoryName(fullPath) ?? rootFull);
            var cursor = parent;
            while (cursor != null)
            {
                if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                if (string.Equals(cursor.FullName.TrimEnd(Path.DirectorySeparatorChar),
                                  root.FullName.TrimEnd(Path.DirectorySeparatorChar),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                cursor = cursor.Parent;
            }
            // Never hit root — the path isn't under rootFull. Caller's lexical check
            // should have caught this; fail closed to be safe.
            return true;
        }
        catch
        {
            return true;
        }
    }
}
