using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// Dedicated JSON parser for LLM assessment output. Handles common LLM quirks:
/// markdown code fences, prose preamble before JSON, and partial/malformed responses.
/// </summary>
public sealed class PipelineAssessmentResultParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<PipelineAssessmentResultParser> _logger;

    public PipelineAssessmentResultParser(ILogger<PipelineAssessmentResultParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parse LLM output into a typed assessment result.
    /// Attempts multiple strategies: raw JSON, code-fence stripping, first-object extraction.
    /// </summary>
    public ParseResult<AssessmentResult> Parse(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return ParseResult<AssessmentResult>.Fail("Empty response");

        // Strategy 1: try raw deserialize
        var attempt = TryDeserialize(rawResponse);
        if (attempt is not null)
            return Validate(attempt, "raw");

        // Strategy 2: strip markdown code fences
        var stripped = StripCodeFences(rawResponse);
        if (stripped != rawResponse)
        {
            attempt = TryDeserialize(stripped);
            if (attempt is not null)
                return Validate(attempt, "fence-stripped");
        }

        // Strategy 3: extract first {...} block
        var extracted = ExtractFirstJsonObject(rawResponse);
        if (extracted is not null)
        {
            attempt = TryDeserialize(extracted);
            if (attempt is not null)
                return Validate(attempt, "extracted-object");
        }

        _logger.LogWarning("PipelineAssessmentResultParser: all parse strategies failed. Response length={Length}", rawResponse.Length);
        return ParseResult<AssessmentResult>.Fail("All parse strategies failed");
    }

    private AssessmentResult? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AssessmentResult>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ParseResult<AssessmentResult> Validate(AssessmentResult result, string strategy)
    {
        // Schema validation
        if (result.HealthScore < 1 || result.HealthScore > 10)
        {
            _logger.LogWarning("PipelineAssessmentResultParser: health_score {Score} out of range, clamping", result.HealthScore);
            result = result with { HealthScore = Math.Clamp(result.HealthScore, 1, 10) };
        }

        var validStatuses = new[] { "healthy", "warning", "critical" };
        if (!validStatuses.Contains(result.Status?.ToLowerInvariant()))
        {
            // Derive from score
            result = result with
            {
                Status = result.HealthScore >= 7 ? "healthy"
                    : result.HealthScore >= 4 ? "warning"
                    : "critical"
            };
        }

        // Validate issues have required fields
        var validIssues = new List<AssessmentIssue>();
        var invalidCount = 0;
        foreach (var issue in result.Issues ?? Array.Empty<AssessmentIssue>())
        {
            if (string.IsNullOrWhiteSpace(issue.Description))
            {
                invalidCount++;
                continue;
            }
            // Ensure dedup_key
            var dedupKey = issue.DedupKey ?? $"{issue.Category}:{issue.TargetType}:{issue.TargetId}";
            validIssues.Add(issue with { DedupKey = dedupKey });
        }

        if (invalidCount > 0)
        {
            _logger.LogWarning("PipelineAssessmentResultParser: dropped {Count} issues missing description", invalidCount);
        }

        result = result with { Issues = validIssues.ToArray() };

        var hasIssues = result.Issues.Length > 0;
        var parseStatus = hasIssues || string.IsNullOrWhiteSpace(result.Summary) is false
            ? "success"
            : "partial";

        return new ParseResult<AssessmentResult>(result, parseStatus, strategy, null);
    }

    private static string StripCodeFences(string input)
    {
        // Match ```json ... ``` or ``` ... ```
        var match = Regex.Match(input, @"```(?:json)?\s*\n?(.*?)\n?\s*```", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : input;
    }

    private static string? ExtractFirstJsonObject(string input)
    {
        var start = input.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < input.Length; i++)
        {
            var c = input[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return input[start..(i + 1)]; }
        }
        return null;
    }
}

/// <summary>Typed parse result with metadata.</summary>
public sealed record ParseResult<T>
{
    public T? Value { get; }
    /// <summary>"success", "partial", "failed"</summary>
    public string Status { get; }
    public string? Strategy { get; }
    public string? Error { get; }
    public bool IsSuccess => Status is "success" or "partial";

    public ParseResult(T value, string status, string strategy, string? error)
    {
        Value = value;
        Status = status;
        Strategy = strategy;
        Error = error;
    }

    public static ParseResult<T> Fail(string error) => new(default!, "failed", null, error);
}

/// <summary>Structured assessment result from the LLM.</summary>
public sealed record AssessmentResult
{
    public int HealthScore { get; init; }
    public string? Status { get; init; }
    public string? Summary { get; init; }
    public AssessmentIssue[]? Issues { get; init; }
    public string[]? Recommendations { get; init; }
    public string? ForwardLook { get; init; }
}

/// <summary>A single issue identified by the AI assessment.</summary>
public sealed record AssessmentIssue
{
    public string? Category { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? Description { get; init; }
    /// <summary>"info" or "warning" (never "critical" — hard-capped per lesson #21).</summary>
    public string? Severity { get; init; }
    public double Confidence { get; init; }
    public string? RecommendedAction { get; init; }
    public string[]? Evidence { get; init; }
    /// <summary>Stable key for dedup: {category}:{target_type}:{target_id}</summary>
    public string? DedupKey { get; init; }
    /// <summary>Set by <see cref="AssessmentGrounder"/>. True if all evidence resolved.</summary>
    public bool? GroundingPassed { get; init; }
}
