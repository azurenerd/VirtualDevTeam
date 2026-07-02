using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Review;
using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Agents.AI;

/// <summary>
/// CLI-native judge implementation. Points the Copilot CLI at each candidate's worktree
/// directory so it can browse files directly using its own tools (view, grep, glob) —
/// eliminating truncation issues from serializing code into prompts.
///
/// Retries on failure before falling back to text-based <see cref="LlmJudge"/>.
/// The text-based fallback is a last resort since it can only see patch diffs (not the
/// full file tree) and is subject to context limits.
/// </summary>
public sealed class CliNativeJudge : ILlmJudge
{
    private readonly ICliReviewService _reviewService;
    private readonly ILlmJudge _fallbackJudge;
    private readonly ILogger<CliNativeJudge> _logger;
    private readonly int _maxRetries;

    public CliNativeJudge(
        ICliReviewService reviewService,
        LlmJudge fallbackJudge,
        ILogger<CliNativeJudge> logger,
        int maxRetries = 1)
    {
        _reviewService = reviewService;
        _fallbackJudge = fallbackJudge;
        _logger = logger;
        _maxRetries = Math.Max(1, maxRetries);
    }

    public async Task<JudgeResult> ScoreAsync(JudgeInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // If no worktree paths available, fall back to text-based judge immediately.
        if (input.CandidateWorktreePaths is null || input.CandidateWorktreePaths.Count == 0)
        {
            _logger.LogWarning(
                "CliNativeJudge: no worktree paths for task {TaskId} — falling back to text-based judge. " +
                "This may produce inaccurate scores if patches are large.",
                input.TaskId);
            return await _fallbackJudge.ScoreAsync(input, ct);
        }

        var scores = new Dictionary<string, CandidateScore>(StringComparer.Ordinal);
        long totalTokens = 0;

        foreach (var (candidateId, worktreePath) in input.CandidateWorktreePaths)
        {
            // Only score candidates that are in the patches dictionary (i.e. survived gates).
            if (!input.CandidatePatches.ContainsKey(candidateId))
                continue;

            var buildContext = input.CandidateBuildContext?.GetValueOrDefault(candidateId);
            var score = await ScoreCandidateWithRetryAsync(
                input.TaskId, input.TaskTitle, input.TaskDescription,
                candidateId, worktreePath, buildContext, ct);

            if (score is not null)
            {
                scores[candidateId] = score;
            }
            else
            {
                _logger.LogWarning(
                    "CliNativeJudge: all retries exhausted for candidate {Candidate} task {TaskId}. " +
                    "Falling back to text-based judge for entire batch.",
                    candidateId, input.TaskId);
                return await _fallbackJudge.ScoreAsync(input, ct);
            }
        }

        if (scores.Count == 0)
        {
            _logger.LogWarning("CliNativeJudge produced no scores for task {TaskId}, falling back", input.TaskId);
            return await _fallbackJudge.ScoreAsync(input, ct);
        }

        return new JudgeResult
        {
            Scores = scores,
            TokensUsed = totalTokens,
        };
    }

    private async Task<CandidateScore?> ScoreCandidateWithRetryAsync(
        string taskId, string taskTitle, string taskDescription,
        string candidateId, string worktreePath, string? buildContext,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            var request = new CliReviewRequest
            {
                WorktreePath = worktreePath,
                ReviewType = ReviewType.Judge,
                TaskTitle = taskTitle,
                TaskDescription = taskDescription,
                BuildContext = buildContext,
                ReviewId = $"judge-{taskId}-{candidateId}-attempt{attempt}",
            };

            try
            {
                var result = await _reviewService.ReviewAsync(request, ct);

                if (result.Succeeded && result.Scores is not null)
                {
                    _logger.LogInformation(
                        "CliNativeJudge scored {Candidate} (attempt {Attempt}): AC={Ac}, Design={Design}, Read={Read}",
                        candidateId, attempt,
                        result.Scores.AcceptanceCriteriaScore,
                        result.Scores.DesignScore,
                        result.Scores.ReadabilityScore);
                    return result.Scores;
                }

                _logger.LogWarning(
                    "CliNativeJudge attempt {Attempt}/{Max} failed for candidate {Candidate} task {TaskId}: {Error}",
                    attempt, _maxRetries, candidateId, taskId, result.Error);

                // Don't retry non-transient errors — same prompt + model = same garbage.
                // invalid-json means the LLM returned unparseable output; retrying wastes
                // another 5+ min CLI session with the same result.
                if (result.Error?.Contains("invalid-json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogWarning(
                        "CliNativeJudge: non-retryable failure (invalid-json) for {Candidate} — skipping remaining retries",
                        candidateId);
                    return null; // fall to batch fallback immediately
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CliNativeJudge attempt {Attempt}/{Max} threw for candidate {Candidate} task {TaskId}",
                    attempt, _maxRetries, candidateId, taskId);
            }

            // Brief backoff before retry
            if (attempt < _maxRetries)
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        return null;
    }
}
