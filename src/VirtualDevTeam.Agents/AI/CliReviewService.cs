using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Review;
using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Agents.AI;

/// <summary>
/// CLI-native review service. Launches a Copilot CLI agentic session pointed at a local
/// directory so the reviewer can browse files, run builds/tests, and return structured
/// scores/comments — eliminating truncation issues from serializing code into prompts.
/// </summary>
public sealed class CliReviewService : ICliReviewService
{
    private readonly CopilotCliProcessManager _processManager;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly ILogger<CliReviewService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public CliReviewService(
        CopilotCliProcessManager processManager,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        ILogger<CliReviewService> logger)
    {
        _processManager = processManager;
        _cfg = cfg;
        _logger = logger;
    }

    public async Task<CliReviewResult> ReviewAsync(CliReviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorktreePath) || !Directory.Exists(request.WorktreePath))
            return CliReviewResult.Failure($"Worktree path does not exist: {request.WorktreePath}");

        var cfg = _cfg.CurrentValue;
        var timeoutSeconds = request.TimeoutSeconds > 0
            ? request.TimeoutSeconds
            : cfg.Timeouts.CliReviewSeconds;

        var prompt = BuildPrompt(request);

        var options = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Review,
            AllowAll = true,
            WorkingDirectory = request.WorktreePath,
            ModelOverride = request.ModelOverride,
            Timeout = TimeoutsConfig.ToTimeSpan(timeoutSeconds),
        };

        _logger.LogInformation(
            "Starting CLI-native {ReviewType} review for '{Title}' in {Path} (timeout {Timeout}s)",
            request.ReviewType, request.TaskTitle, request.WorktreePath, timeoutSeconds);

        var result = await _processManager.ExecuteAgenticSessionAsync(prompt, options, ct);

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "CLI-native review failed for '{Title}': {Reason} — {Error}",
                request.TaskTitle, result.FailureReason, result.ErrorMessage);
            return new CliReviewResult
            {
                Succeeded = false,
                Error = $"{result.FailureReason}: {result.ErrorMessage}",
                RawOutput = result.LogBuffer,
                ToolCallCount = result.ToolCallCount,
                WallClock = result.WallClock,
            };
        }

        // Extract the final assistant message from JSONL output
        var finalResponse = CliOutputParser.ParseJsonOutput(result.LogBuffer);
        if (string.IsNullOrWhiteSpace(finalResponse))
        {
            // Fallback: use parsed raw output (non-JSONL mode)
            finalResponse = CliOutputParser.Parse(result.LogBuffer);
        }

        _logger.LogInformation(
            "CLI-native {ReviewType} review completed for '{Title}' — {ToolCalls} tool calls, {WallClock:F1}s",
            request.ReviewType, request.TaskTitle, result.ToolCallCount, result.WallClock.TotalSeconds);

        // Validate worktree wasn't mutated (defense in depth)
        await ValidateWorktreeIntegrityAsync(request.WorktreePath, request.ReviewId, ct);

        return request.ReviewType switch
        {
            ReviewType.Judge => ParseJudgeResult(finalResponse, result),
            _ => ParsePeerReviewResult(finalResponse, result),
        };
    }

    // ---- Prompt builders -------------------------------------------------------

    private static string BuildPrompt(CliReviewRequest request)
    {
        return request.ReviewType switch
        {
            ReviewType.Judge => BuildJudgePrompt(request),
            ReviewType.Rework => BuildReworkPrompt(request),
            _ => BuildPeerReviewPrompt(request),
        };
    }

    private static string BuildJudgePrompt(CliReviewRequest request)
    {
        var sb = new System.Text.StringBuilder(4096);
        sb.AppendLine("You are an impartial code-review judge scoring an implementation against its acceptance criteria.");
        sb.AppendLine();
        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("1. Browse the code in your current working directory using your tools (view, grep, glob).");
        sb.AppendLine("2. Understand what was built and how well it matches the task requirements.");
        sb.AppendLine("3. DO NOT modify any files. This is a read-only review.");
        sb.AppendLine("4. Score the implementation on three axes (0-10 integer scale):");
        sb.AppendLine("   - ac: How well the code satisfies the stated acceptance criteria");
        sb.AppendLine("   - design: Code structure, separation of concerns, suitability of abstractions");
        sb.AppendLine("   - readability: Clarity, naming, comment quality, consistency");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.BuildContext))
        {
            sb.AppendLine("BUILD/TEST RESULTS (AUTHORITATIVE — do NOT contradict these):");
            sb.AppendLine(request.BuildContext);
            sb.AppendLine("If the build succeeded, do NOT claim the app won't build regardless of what you see in the code.");
            sb.AppendLine();
        }

        sb.AppendLine("## Task");
        sb.AppendLine($"**Title:** {request.TaskTitle}");
        if (!string.IsNullOrWhiteSpace(request.TaskDescription))
        {
            sb.AppendLine();
            sb.AppendLine("**Description & Acceptance Criteria:**");
            sb.AppendLine(request.TaskDescription);
        }

        if (!string.IsNullOrWhiteSpace(request.ReviewInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("**Additional Review Instructions:**");
            sb.AppendLine(request.ReviewInstructions);
        }

        if (!string.IsNullOrWhiteSpace(request.AdditionalContext))
        {
            sb.AppendLine();
            sb.AppendLine("**Additional Context:**");
            sb.AppendLine(request.AdditionalContext);
        }

        sb.AppendLine();
        sb.AppendLine("SCORING RULES:");
        sb.AppendLine("- If the implementation is complete and self-contained (all files needed to run are present), score ac ≥ 7.");
        sb.AppendLine("- If a web app references external data files not present in the directory, score ac ≤ 3.");
        sb.AppendLine("- Per-dimension feedback: explain WHY each dimension got its score (1-2 sentences each).");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — respond with ONLY this JSON (no markdown fences, no explanation, no other text):");
        sb.AppendLine("CRITICAL: Use EXACTLY these key names — \"ac\", \"design\", \"readability\" — not alternatives like \"acceptance_criteria\" or \"code_design\".");
        sb.AppendLine("""{"scores":[{"candidateId":"candidate","ac":7,"design":8,"readability":9,"feedback":"overall summary","ac_feedback":"why this AC score","design_feedback":"why this design score","readability_feedback":"why this readability score"}]}""");

        return sb.ToString();
    }

    private static string BuildPeerReviewPrompt(CliReviewRequest request)
    {
        var sb = new System.Text.StringBuilder(4096);
        sb.AppendLine($"You are a {request.ReviewType} reviewer examining code in your current working directory.");
        sb.AppendLine();
        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("1. Browse the code using your tools (view, grep, glob).");
        sb.AppendLine("2. Assess the implementation against the requirements below.");
        sb.AppendLine("3. DO NOT modify any files. This is a read-only review.");
        sb.AppendLine();

        sb.AppendLine($"## Task: {request.TaskTitle}");
        if (!string.IsNullOrWhiteSpace(request.TaskDescription))
            sb.AppendLine(request.TaskDescription);

        if (!string.IsNullOrWhiteSpace(request.ReviewInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("## Review Focus");
            sb.AppendLine(request.ReviewInstructions);
        }

        if (!string.IsNullOrWhiteSpace(request.AdditionalContext))
        {
            sb.AppendLine();
            sb.AppendLine("## Context");
            sb.AppendLine(request.AdditionalContext);
        }

        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — respond with ONLY this JSON:");
        sb.AppendLine("""{"decision":"approve|request_changes|comment","body":"markdown review body","inline_comments":[{"file":"path","start_line":1,"end_line":1,"body":"comment"}]}""");

        return sb.ToString();
    }

    private static string BuildReworkPrompt(CliReviewRequest request)
    {
        var sb = new System.Text.StringBuilder(4096);
        sb.AppendLine("You are a senior engineer providing targeted improvement feedback on an implementation.");
        sb.AppendLine();
        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("1. Browse the code in your current working directory.");
        sb.AppendLine("2. Identify the most impactful improvements that would raise quality scores.");
        sb.AppendLine("3. DO NOT modify any files. Provide written feedback only.");
        sb.AppendLine("4. Focus on concrete, actionable improvements — not vague suggestions.");
        sb.AppendLine();

        sb.AppendLine($"## Task: {request.TaskTitle}");
        if (!string.IsNullOrWhiteSpace(request.TaskDescription))
            sb.AppendLine(request.TaskDescription);

        if (!string.IsNullOrWhiteSpace(request.ReviewInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("## Specific Feedback Targets");
            sb.AppendLine(request.ReviewInstructions);
        }

        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — respond with ONLY this JSON:");
        sb.AppendLine("""{"feedback":"1-3 paragraphs of specific improvement suggestions","priority_files":["file paths that need the most attention"]}""");

        return sb.ToString();
    }

    // ---- Response parsers -------------------------------------------------------

    private CliReviewResult ParseJudgeResult(string response, AgenticSessionResult session)
    {
        if (string.IsNullOrWhiteSpace(response))
            return MakeResult(session, error: "empty-response");

        var stripped = StripCodeFences(response);
        JudgeResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<JudgeResponseDto>(stripped, JsonOpts);
        }
        catch (JsonException jex)
        {
            _logger.LogWarning(
                "CliReviewService JSON parse failed: {Err}. First 300 chars: {Snippet}",
                jex.Message, Truncate(stripped, 300));
            return MakeResult(session, error: $"invalid-json: {jex.Message}", rawOverride: response);
        }

        if (dto?.Scores is null || dto.Scores.Count == 0)
            return MakeResult(session, error: "invalid-schema: no scores", rawOverride: response);

        var entry = dto.Scores.FirstOrDefault(s => s is not null);
        if (entry is null)
            return MakeResult(session, error: "invalid-schema: null score entry", rawOverride: response);

        // Resolve scores from alternative key names the LLM might have used
        // (e.g., "acceptance_criteria" instead of "ac", "design_quality" instead of "design").
        entry.ResolveAlternativeKeys();

        // If all numeric scores are still null after alternative key resolution, the LLM
        // returned a schema we can't interpret. Treat as failure so the judge retries/falls back.
        if (entry.Ac is null && entry.Design is null && entry.Readability is null)
        {
            _logger.LogWarning(
                "CliReviewService: judge returned scores entry with all-null values (even after " +
                "alternative key resolution). First 300 chars: {Snippet}", Truncate(stripped, 300));
            return MakeResult(session, error: "invalid-schema: all score values null", rawOverride: response);
        }

        var score = new CandidateScore
        {
            AcceptanceCriteriaScore = Clamp(entry.Ac),
            DesignScore = Clamp(entry.Design),
            ReadabilityScore = Clamp(entry.Readability),
            Feedback = entry.Feedback ?? "",
            AcFeedback = entry.AcFeedback ?? "",
            DesignFeedback = entry.DesignFeedback ?? "",
            ReadabilityFeedback = entry.ReadabilityFeedback ?? "",
        };

        _logger.LogInformation(
            "CLI-native judge scored: AC={Ac}, Design={Design}, Readability={Read}",
            score.AcceptanceCriteriaScore, score.DesignScore, score.ReadabilityScore);

        return new CliReviewResult
        {
            Succeeded = true,
            Scores = score,
            RawOutput = response,
            ToolCallCount = session.ToolCallCount,
            WallClock = session.WallClock,
        };
    }

    private CliReviewResult ParsePeerReviewResult(string response, AgenticSessionResult session)
    {
        if (string.IsNullOrWhiteSpace(response))
            return MakeResult(session, error: "empty-response");

        var stripped = StripCodeFences(response);
        PeerReviewDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PeerReviewDto>(stripped, JsonOpts);
        }
        catch (JsonException jex)
        {
            _logger.LogWarning(
                "CliReviewService peer review JSON parse failed: {Err}",
                jex.Message);
            // Fallback: treat the entire response as the review body
            return new CliReviewResult
            {
                Succeeded = true,
                ReviewBody = response,
                Decision = ReviewDecision.Comment,
                RawOutput = response,
                ToolCallCount = session.ToolCallCount,
                WallClock = session.WallClock,
            };
        }

        var decision = (dto?.Decision?.ToLowerInvariant()) switch
        {
            "approve" => ReviewDecision.Approve,
            "request_changes" or "request changes" => ReviewDecision.RequestChanges,
            _ => ReviewDecision.Comment,
        };

        var inlineComments = dto?.InlineComments?
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.File) && !string.IsNullOrWhiteSpace(c.Body))
            .Select(c => new CliInlineComment
            {
                FilePath = c!.File!,
                StartLine = c.StartLine,
                EndLine = c.EndLine ?? c.StartLine,
                Body = c.Body!,
            })
            .ToList();

        return new CliReviewResult
        {
            Succeeded = true,
            ReviewBody = dto?.Body ?? response,
            InlineComments = inlineComments,
            Decision = decision,
            RawOutput = response,
            ToolCallCount = session.ToolCallCount,
            WallClock = session.WallClock,
        };
    }

    // ---- Integrity check --------------------------------------------------------

    private async Task ValidateWorktreeIntegrityAsync(string path, string? reviewId, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain");

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return;

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (!string.IsNullOrWhiteSpace(output))
            {
                _logger.LogWarning(
                    "CLI-native review {ReviewId} modified worktree files! Changes: {Changes}",
                    reviewId ?? "unknown", Truncate(output, 500));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Worktree integrity check failed for {ReviewId}", reviewId ?? "unknown");
        }
    }

    // ---- Helpers ----------------------------------------------------------------

    private static CliReviewResult MakeResult(AgenticSessionResult session, string error, string? rawOverride = null) =>
        new()
        {
            Succeeded = false,
            Error = error,
            RawOutput = rawOverride ?? session.LogBuffer,
            ToolCallCount = session.ToolCallCount,
            WallClock = session.WallClock,
        };

    private static string StripCodeFences(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```"))
        {
            var firstNewline = t.IndexOf('\n');
            if (firstNewline > 0) t = t[(firstNewline + 1)..];
            if (t.EndsWith("```")) t = t[..^3];
            t = t.Trim();
        }

        // If the result doesn't start with '{', try to extract JSON object from the text.
        // LLMs sometimes prefix their JSON with explanatory prose.
        if (!t.StartsWith('{'))
        {
            var jsonStart = t.IndexOf('{');
            if (jsonStart >= 0)
            {
                // Find the matching closing brace (simple heuristic: last '}' in the string)
                var jsonEnd = t.LastIndexOf('}');
                if (jsonEnd > jsonStart)
                    t = t[jsonStart..(jsonEnd + 1)];
            }
        }

        return t;
    }

    private static int Clamp(int? raw) => raw is null ? 0 : Math.Clamp(raw.Value, 0, 10);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---- DTOs -------------------------------------------------------------------

    private sealed class JudgeResponseDto
    {
        [JsonPropertyName("scores")]
        public List<JudgeScoreEntry?>? Scores { get; set; }
    }

    private sealed class JudgeScoreEntry
    {
        [JsonPropertyName("candidateId")]
        public string? CandidateId { get; set; }
        [JsonPropertyName("ac")]
        public int? Ac { get; set; }
        [JsonPropertyName("design")]
        public int? Design { get; set; }
        [JsonPropertyName("readability")]
        public int? Readability { get; set; }
        [JsonPropertyName("feedback")]
        public string? Feedback { get; set; }
        [JsonPropertyName("ac_feedback")]
        public string? AcFeedback { get; set; }
        [JsonPropertyName("design_feedback")]
        public string? DesignFeedback { get; set; }
        [JsonPropertyName("readability_feedback")]
        public string? ReadabilityFeedback { get; set; }

        /// <summary>Catches any unmapped properties — used to resolve scores from alternative key names.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        /// <summary>
        /// Resolves scores from extension data when the LLM uses alternative key names
        /// (e.g., "acceptance_criteria" instead of "ac").
        /// </summary>
        public void ResolveAlternativeKeys()
        {
            if (ExtensionData is null || ExtensionData.Count == 0) return;

            Ac ??= TryExtractInt("acceptance_criteria", "acceptance", "ac_score",
                "acceptance_criteria_score", "criteria");
            Design ??= TryExtractInt("design_score", "design_quality", "code_design",
                "architecture", "structure");
            Readability ??= TryExtractInt("readability_score", "code_readability",
                "clarity", "read", "readability_quality");
            Feedback ??= TryExtractString("overall_feedback", "summary", "overall");
            AcFeedback ??= TryExtractString("acceptance_criteria_feedback", "criteria_feedback");
            DesignFeedback ??= TryExtractString("design_feedback_detail", "design_reasoning");
            ReadabilityFeedback ??= TryExtractString("readability_feedback_detail", "readability_reasoning");
        }

        private int? TryExtractInt(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (ExtensionData!.TryGetValue(key, out var el))
                {
                    if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
                        return v;
                    // Handle string-encoded numbers: "8" → 8
                    if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var sv))
                        return sv;
                }
            }
            return null;
        }

        private string? TryExtractString(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (ExtensionData!.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
                    return el.GetString();
            }
            return null;
        }
    }

    private sealed class PeerReviewDto
    {
        [JsonPropertyName("decision")]
        public string? Decision { get; set; }
        [JsonPropertyName("body")]
        public string? Body { get; set; }
        [JsonPropertyName("inline_comments")]
        public List<InlineCommentDto?>? InlineComments { get; set; }
        [JsonPropertyName("feedback")]
        public string? Feedback { get; set; }
        [JsonPropertyName("priority_files")]
        public List<string>? PriorityFiles { get; set; }
    }

    private sealed class InlineCommentDto
    {
        [JsonPropertyName("file")]
        public string? File { get; set; }
        [JsonPropertyName("start_line")]
        public int? StartLine { get; set; }
        [JsonPropertyName("end_line")]
        public int? EndLine { get; set; }
        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }
}
