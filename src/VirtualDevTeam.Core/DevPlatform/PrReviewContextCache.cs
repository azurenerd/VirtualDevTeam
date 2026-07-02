using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.DevPlatform;

/// <summary>
/// Shared cache for PR review context (changed files + contents).
/// When an SE marks a PR ready-for-review, it publishes the review context
/// from its local worktree. PM/Architect/TE read from this cache instead
/// of making redundant GitHub API calls to fetch the same data.
///
/// Keyed by (prNumber, headSha) — auto-invalidated when SHA changes (new commits).
/// In-memory for speed; acceptable to lose on restart since reviewers will
/// fall back to the existing API path.
/// </summary>
public sealed class PrReviewContextCache
{
    private readonly ConcurrentDictionary<string, PrReviewContext> _cache = new();
    private readonly ILogger<PrReviewContextCache> _logger;

    public PrReviewContextCache(ILogger<PrReviewContextCache> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Store review context for a PR. Called by the SE when marking ready-for-review.
    /// </summary>
    public void Store(int prNumber, string headSha, string codeContext, IReadOnlyList<string> changedFiles)
    {
        var key = BuildKey(prNumber, headSha);
        var ctx = new PrReviewContext
        {
            PrNumber = prNumber,
            HeadSha = headSha,
            CodeContext = codeContext,
            ChangedFiles = changedFiles,
            StoredAtUtc = DateTimeOffset.UtcNow,
        };
        _cache[key] = ctx;
        _logger.LogInformation(
            "PR #{PrNumber} review context cached ({FileCount} files, {Size} chars, SHA {Sha})",
            prNumber, changedFiles.Count, codeContext.Length, headSha[..7]);
    }

    /// <summary>
    /// Try to get cached review context for a PR at a specific SHA.
    /// Returns null if not cached or SHA doesn't match (stale).
    /// </summary>
    public PrReviewContext? TryGet(int prNumber, string headSha)
    {
        var key = BuildKey(prNumber, headSha);
        if (_cache.TryGetValue(key, out var ctx))
        {
            _logger.LogDebug("PR #{PrNumber} review context cache HIT (SHA {Sha})", prNumber, headSha[..7]);
            return ctx;
        }
        _logger.LogDebug("PR #{PrNumber} review context cache MISS (SHA {Sha})", prNumber, headSha[..7]);
        return null;
    }

    /// <summary>
    /// Try to get cached review context for a PR at any SHA (latest available).
    /// Less strict than TryGet — useful when caller doesn't know the exact SHA.
    /// </summary>
    public PrReviewContext? TryGetLatest(int prNumber)
    {
        return _cache.Values
            .Where(c => c.PrNumber == prNumber)
            .OrderByDescending(c => c.StoredAtUtc)
            .FirstOrDefault();
    }

    /// <summary>Clear all cached contexts (e.g., on reset).</summary>
    public void Clear() => _cache.Clear();

    private static string BuildKey(int prNumber, string headSha) => $"{prNumber}:{headSha}";
}

/// <summary>Cached PR review context with file list and code content.</summary>
public sealed record PrReviewContext
{
    public required int PrNumber { get; init; }
    public required string HeadSha { get; init; }
    public required string CodeContext { get; init; }
    public required IReadOnlyList<string> ChangedFiles { get; init; }
    public required DateTimeOffset StoredAtUtc { get; init; }
}
