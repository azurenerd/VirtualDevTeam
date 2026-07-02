using VirtualDevTeam.Core.Strategies;

namespace VirtualDevTeam.Core.Review;

/// <summary>
/// Unified CLI-native review service. Launches a Copilot CLI agentic session pointed at
/// a local directory (worktree) so the reviewer can browse files, run builds/tests, and
/// make its own assessment — eliminating truncation issues from serializing code into prompts.
///
/// Used by:
///  - <c>CliNativeJudge</c> (strategy evaluation scoring)
///  - Architect/PM/SE/TE peer review (Phase 2)
///  - Revision feedback generation (Phase 3)
/// </summary>
public interface ICliReviewService
{
    /// <summary>
    /// Run a CLI-native review session against the code in the given directory.
    /// The CLI is launched with <c>--allow-all</c> and working directory set to
    /// the review path, then given role-specific review instructions.
    /// </summary>
    Task<CliReviewResult> ReviewAsync(CliReviewRequest request, CancellationToken ct);
}

/// <summary>Specifies what kind of review to perform — determines prompt structure and output parsing.</summary>
public enum ReviewType
{
    /// <summary>Score code 0-10 on acceptance criteria, design, readability. Returns <see cref="CandidateScore"/>.</summary>
    Judge,
    /// <summary>Architecture alignment review. Returns review body + optional inline comments.</summary>
    ArchitectReview,
    /// <summary>PM business alignment review. Returns review body + approval decision.</summary>
    PMReview,
    /// <summary>Peer engineer code review. Returns review body + inline comments.</summary>
    PeerReview,
    /// <summary>Test engineer review for coverage gaps. Returns review body.</summary>
    TestReview,
    /// <summary>Adversarial critique for revision feedback. Returns targeted improvement suggestions.</summary>
    Rework,
}

/// <summary>Reviewer's decision after a peer review.</summary>
public enum ReviewDecision
{
    /// <summary>No decision (e.g. judge scoring only).</summary>
    None,
    Approve,
    RequestChanges,
    Comment,
}

/// <summary>Input for a CLI-native review session.</summary>
public record CliReviewRequest
{
    /// <summary>Absolute path to the directory containing the code to review.</summary>
    public required string WorktreePath { get; init; }

    /// <summary>What kind of review to perform.</summary>
    public required ReviewType ReviewType { get; init; }

    /// <summary>Task or PR title for context.</summary>
    public string TaskTitle { get; init; } = "";

    /// <summary>Full task/PR description including acceptance criteria.</summary>
    public string TaskDescription { get; init; } = "";

    /// <summary>
    /// Role-specific review instructions. Appended to the base review prompt.
    /// For judge: scoring criteria. For architect: Architecture.md alignment rules.
    /// For PM: business requirements. For rework: specific improvement targets.
    /// </summary>
    public string ReviewInstructions { get; init; } = "";

    /// <summary>
    /// Authoritative build/test context from the evaluator gates. Format:
    /// "Build: succeeded. Tests: 24/24 passed." The reviewer is told this is ground truth.
    /// Null when build hasn't been verified (e.g. peer review without local build).
    /// </summary>
    public string? BuildContext { get; init; }

    /// <summary>
    /// Additional context documents (Architecture.md, PMSpec.md, prior review comments, etc.).
    /// Kept short — the reviewer can read full documents from the repo via CLI tools.
    /// </summary>
    public string? AdditionalContext { get; init; }

    /// <summary>Model to use for the review (e.g. "claude-opus-4.7"). Null = use configured default.</summary>
    public string? ModelOverride { get; init; }

    /// <summary>Maximum wall-clock seconds for the review session. 0 = use configured default.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>Unique identifier for logging/tracking (e.g. "judge-T1-copilot-cli", "arch-review-PR-329").</summary>
    public string? ReviewId { get; init; }
}

/// <summary>Output from a CLI-native review session.</summary>
public record CliReviewResult
{
    public bool Succeeded { get; init; }

    /// <summary>Judge scores (populated only for <see cref="ReviewType.Judge"/>).</summary>
    public CandidateScore? Scores { get; init; }

    /// <summary>Review body text for peer reviews (markdown formatted).</summary>
    public string? ReviewBody { get; init; }

    /// <summary>File-specific inline comments for peer reviews.</summary>
    public IReadOnlyList<CliInlineComment>? InlineComments { get; init; }

    /// <summary>Reviewer's approval decision.</summary>
    public ReviewDecision Decision { get; init; } = ReviewDecision.None;

    /// <summary>Error message when review failed.</summary>
    public string? Error { get; init; }

    /// <summary>Full CLI output for debugging/logging.</summary>
    public string RawOutput { get; init; } = "";

    /// <summary>Number of tool calls the CLI made during review.</summary>
    public int ToolCallCount { get; init; }

    /// <summary>Wall-clock time for the review session.</summary>
    public TimeSpan WallClock { get; init; }

    public static CliReviewResult Failure(string error) => new()
    {
        Succeeded = false,
        Error = error,
    };
}

/// <summary>An inline review comment on a specific file/line.</summary>
public record CliInlineComment
{
    public required string FilePath { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public required string Body { get; init; }
}
