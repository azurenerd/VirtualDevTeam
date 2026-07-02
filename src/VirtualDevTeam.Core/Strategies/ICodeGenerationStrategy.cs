namespace VirtualDevTeam.Core.Strategies;

/// <summary>
/// A code-generation strategy (baseline / mcp-enhanced / agentic-delegation). Each
/// strategy receives a scoped worktree and returns a patch plus cost/timing metadata.
/// Strategies must never mutate files outside <see cref="StrategyInvocation.WorktreePath"/>.
/// </summary>
public interface ICodeGenerationStrategy
{
    /// <summary>Stable strategy identifier used in config, logs, experiment records, and commit trailers.</summary>
    string Id { get; }

    /// <summary>Generate code inside the worktree. Must be safe to cancel at any point.</summary>
    Task<StrategyExecutionResult> ExecuteAsync(StrategyInvocation invocation, CancellationToken ct);
}

/// <summary>Task-level context supplied to every candidate (shared, immutable).</summary>
public record TaskContext
{
    public required string TaskId { get; init; }
    public required string TaskTitle { get; init; }
    public required string TaskDescription { get; init; }
    public required string PrBranch { get; init; }
    public required string BaseSha { get; init; }
    public required string RunId { get; init; }
    /// <summary>Full repo path of the SE agent's LocalWorkspace (candidate root is derived from this).</summary>
    public required string AgentRepoPath { get; init; }
    /// <summary>Complexity hint from task metadata; influences SamplingPolicy evaluation.</summary>
    public int Complexity { get; init; } = 1;
    /// <summary>True when the task is web/UI — enables Gate3 (AppStarts).</summary>
    public bool IsWebTask { get; init; }
    /// <summary>
    /// Wave label from the engineering plan (e.g., "W0", "W1", "W2", or null for ad-hoc tasks).
    /// Surfaced on the Frameworks dashboard so users can see which wave a candidate ran in.
    /// </summary>
    public string? Wave { get; init; }

    // ── Optional code-gen context fields (populated by SE when invoking the orchestrator) ──
    // Strategies that do real LLM generation (BaselineStrategy + IBaselineCodeGenerator) read
    // these to build a single-pass prompt with parity to the SE agent's legacy code-gen path.
    // Null/empty means "not supplied" — the generator falls back to a minimal default.

    /// <summary>PM specification document body (Markdown). Optional context for code-gen prompts.</summary>
    public string? PmSpec { get; init; }
    /// <summary>Architecture document body (Markdown). Optional context for code-gen prompts.</summary>
    public string? Architecture { get; init; }
    /// <summary>Tech stack hint (e.g., "Blazor Server, .NET 8"). Used by prompts and design-context heuristics.</summary>
    public string? TechStack { get; init; }
    /// <summary>Source GitHub issue context, formatted as "## GitHub Issue #N: Title\nbody". Optional.</summary>
    public string? IssueContext { get; init; }
    /// <summary>UI design context (HTML mockups, design tokens) gated to UI tasks. Optional.</summary>
    public string? DesignContext { get; init; }
    /// <summary>
    /// Pre-gathered summary of the existing project (README, copilot-instructions, structure,
    /// patterns, dependencies). Populated from <see cref="Configuration.VirtualDevTeamConfig.ProjectConfig.ExistingProjectContext"/>.
    /// When present, strategy prompts include it so candidates understand the existing codebase
    /// before generating code, reducing blind-generation failures.
    /// </summary>
    public string? ExistingProjectContext { get; init; }

    /// <summary>
    /// Pre-computed list of files the strategy should focus on (e.g., cross-cutting files
    /// from merged PRs for T-FINAL). When populated, included in the agentic prompt to
    /// prevent unbounded codebase exploration. Null = no focus constraint.
    /// </summary>
    public IReadOnlyList<string>? FocusFiles { get; init; }

    /// <summary>
    /// Override the global <see cref="Configuration.AgenticConfig.ToolCallCap"/> for this
    /// specific task. T-FINAL validation needs higher headroom than normal code-gen tasks.
    /// Null = use the global config default.
    /// </summary>
    public int? ToolCallCapOverride { get; init; }
}

/// <summary>Per-candidate invocation handed to a strategy at run time.</summary>
public record StrategyInvocation
{
    public required TaskContext Task { get; init; }
    public required string WorktreePath { get; init; }
    public required string StrategyId { get; init; }
    /// <summary>Hard wall-clock timeout for this strategy.</summary>
    public required TimeSpan Timeout { get; init; }
    /// <summary>
    /// Optional progress callback for real-time activity streaming to the dashboard.
    /// Strategies report significant events (tool calls, decisions, file writes) via this sink.
    /// </summary>
    public IProgress<Frameworks.FrameworkActivityEvent>? ActivitySink { get; init; }
    /// <summary>
    /// Non-null during the revision round. Contains initial scores, judge feedback,
    /// rubber-duck critique, and the original patch for targeted fixes.
    /// Null on the initial (first) pass.
    /// </summary>
    public RevisionContext? Revision { get; init; }
    /// <summary>
    /// The commit SHA the worktree was created at. Used by post-execution diagnostics
    /// to accurately count changes even when the strategy commits mid-run (notably the
    /// agentic CLI which invokes <c>git add -A &amp;&amp; git commit</c> during tool use,
    /// making <c>git diff HEAD</c> return nothing).
    /// </summary>
    public string? BaseSha { get; init; }

    /// <summary>
    /// When true, the strategy should bypass the wrapper command (e.g., "agency") and call
    /// copilot directly. Used by the stuck-candidate retry escalation (rung 2) to work around
    /// wrapper-related startup hangs. Per-call, not global.
    /// </summary>
    public bool ForceNoWrapper { get; init; } = false;

    /// <summary>
    /// Which reset attempt this is (0 = first try, 1 = first retry, 2 = second retry).
    /// Used for logging and to inform the strategy about its retry context.
    /// </summary>
    public int AttemptNumber { get; init; } = 0;
}

/// <summary>
/// Context provided to strategies during the revision round. Contains initial scores,
/// judge feedback, rubber-duck critique, and the original patch so the strategy can
/// make targeted fixes rather than regenerating from scratch.
/// </summary>
public record RevisionContext
{
    /// <summary>Initial judge scores keyed by axis name (e.g., "ac", "design", "readability", "visuals").</summary>
    public required IReadOnlyDictionary<string, int> InitialScores { get; init; }
    /// <summary>Actionable feedback from the LLM judge (overall summary).</summary>
    public required string JudgeFeedback { get; init; }
    /// <summary>Per-dimension feedback: acceptance criteria.</summary>
    public string? AcFeedback { get; init; }
    /// <summary>Per-dimension feedback: design quality.</summary>
    public string? DesignFeedback { get; init; }
    /// <summary>Per-dimension feedback: code readability.</summary>
    public string? ReadabilityFeedback { get; init; }
    /// <summary>Per-dimension feedback: visual quality.</summary>
    public string? VisualsFeedback { get; init; }
    /// <summary>Adversarial critique from a different model tier (rubber-duck perspective).</summary>
    public required string RubberDuckFeedback { get; init; }
    /// <summary>The unified diff patch from the initial round. Strategies can read their own prior output.</summary>
    public required string OriginalPatch { get; init; }
}

/// <summary>What a strategy returns after executing inside its worktree.</summary>
public record StrategyExecutionResult
{
    public required string StrategyId { get; init; }
    public required bool Succeeded { get; init; }
    public string? FailureReason { get; init; }
    /// <summary>Wall-clock elapsed time.</summary>
    public required TimeSpan Elapsed { get; init; }
    /// <summary>Tokens consumed (input + output) if the strategy tracks them. Null when unknown.</summary>
    public long? TokensUsed { get; init; }
    /// <summary>Diagnostic log lines (truncated). Stored in experiment record.</summary>
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();
    /// <summary>
    /// True when the strategy succeeded but deliberately produced no file changes because the
    /// agent inspected the worktree and concluded the task was already complete (e.g. files
    /// from a previous merged PR are already present at <c>BaseSha</c>). Distinguishes a
    /// legitimate no-op from a transient CLI failure that swallowed its tool errors. When
    /// true, the orchestrator should NOT trigger the empty-patch retry loop.
    /// </summary>
    public bool NoOpAcknowledged { get; init; }
}

/// <summary>Evaluator output for a single candidate after hard-gate + LLM scoring.</summary>
public record CandidateResult
{
    public required string StrategyId { get; init; }
    public required bool Survived { get; init; }
    /// <summary>The first gate that failed (null when all passed).</summary>
    public string? FailedGate { get; init; }
    public string? FailureDetail { get; init; }
    /// <summary>Diff against base SHA (may be empty string when Gate1 failed).</summary>
    public string Patch { get; init; } = "";
    public int PatchSizeBytes { get; init; }
    public required StrategyExecutionResult Execution { get; init; }
    /// <summary>LLM scoring output (null when evaluator skipped or gate failed).</summary>
    public CandidateScore? Score { get; init; }
    /// <summary>PNG screenshot bytes captured after build gate passed (null if capture failed/skipped/non-web).</summary>
    public byte[]? ScreenshotBytes { get; init; }
    /// <summary>Paths to all screenshots captured during multi-page interaction (relative to workspace). Null if capture skipped.</summary>
    public IReadOnlyList<string>? ScreenshotPaths { get; init; }
    /// <summary>Path to trimmed interaction video (relative to workspace). Null if video capture skipped or FFmpeg unavailable.</summary>
    public string? VideoPath { get; init; }
    /// <summary>Path to animated GIF generated from the video. Null if FFmpeg unavailable or video not captured.</summary>
    public string? AnimatedGifPath { get; init; }
    /// <summary>
    /// Which preview producer chain produced <see cref="ScreenshotBytes"/>. Defaults to
    /// <see cref="Preview.CandidatePreviewSource.PlaywrightScreenshot"/> for backward
    /// compatibility with tests / call sites that don't set this. Drives the dashboard
    /// badge selection (📷 Playwright, 🎨 ImageAssets, 📐 Diagrams, none).
    /// </summary>
    public Preview.CandidatePreviewSource PreviewSource { get; init; } = Preview.CandidatePreviewSource.PlaywrightScreenshot;
    /// <summary>
    /// Source asset paths for non-Playwright previews (image-asset contact sheet or
    /// diagram set). Null for Playwright/NoVisualContent paths or when the producer
    /// didn't surface a per-asset list.
    /// </summary>
    public IReadOnlyList<string>? IncludedAssetPaths { get; init; }

    // ── Secondary preview (mixed-content PRs: code + committed assets) ──
    /// <summary>
    /// Base64-encoded PNG of a SECONDARY preview (e.g. an image-asset contact sheet)
    /// when the candidate worktree is "mixed-content": it has BOTH a runnable app
    /// (primary Playwright capture in <see cref="ScreenshotBytes"/>) AND committed art
    /// assets. The dashboard renders this below the primary preview as an "Assets used"
    /// strip. Null in the common single-source case.
    /// </summary>
    public string? SecondaryPreviewBase64 { get; init; }
    /// <summary>
    /// Source paths of the assets included in <see cref="SecondaryPreviewBase64"/>.
    /// Used by the dashboard to render clickable per-asset thumbnails (each opens in
    /// the lightbox). Null when no secondary preview applied.
    /// </summary>
    public IReadOnlyList<string>? SecondaryAssetPaths { get; init; }
    /// <summary>
    /// <see cref="Preview.CandidatePreviewSource"/> of the secondary preview (almost
    /// always <see cref="Preview.CandidatePreviewSource.ImageAssets"/> today, but
    /// kept extensible for future producer combinations). Null when no secondary
    /// preview applied.
    /// </summary>
    public Preview.CandidatePreviewSource? SecondaryPreviewSource { get; init; }

    /// <summary>
    /// Dual-capture metrics: per-source artifact counts, tool calls, pages discovered.
    /// Populated after parallel MCP + Direct capture completes.
    /// </summary>
    public ScreenshotCaptureSummary? CaptureMetrics { get; init; }

    /// <summary>
    /// CDP-derived page analysis: UI vs API detection, console errors, failed requests.
    /// Collected during C# Playwright capture.
    /// </summary>
    public PageAnalysis? PageAnalysis { get; init; }

    /// <summary>
    /// The base URL the app was started on (e.g., "http://localhost:5142").
    /// Null when the app didn't start or capture was skipped.
    /// </summary>
    public string? AppBaseUrl { get; init; }

    /// <summary>
    /// Aggregated runtime behavior context: console errors, failed requests,
    /// API smoke results, build/test output. Fed to judges and reviewers.
    /// </summary>
    public InteractionContext? InteractionContext { get; init; }
}

/// <summary>Scores from the LLM judge (0-10 integer scale per the doc).</summary>
public record CandidateScore
{
    public int AcceptanceCriteriaScore { get; init; }
    public int DesignScore { get; init; }
    public int ReadabilityScore { get; init; }
    /// <summary>Visual quality score from the vision judge. Null when visual scoring is not applicable (non-visual task).</summary>
    public int? VisualsScore { get; init; }
    public string Reasoning { get; init; } = "";
    /// <summary>
    /// Actionable improvement feedback from the judge. Distinct from Reasoning (which explains the score).
    /// Contains specific suggestions per scoring axis for how to improve. Empty when all scores >= 8.
    /// Used by the revision round to give frameworks a second chance.
    /// </summary>
    public string Feedback { get; init; } = "";
    /// <summary>Per-dimension feedback from the judge. Shown when user clicks a specific score bar.</summary>
    public string AcFeedback { get; init; } = "";
    /// <summary>Per-dimension feedback from the judge for design quality.</summary>
    public string DesignFeedback { get; init; } = "";
    /// <summary>Per-dimension feedback from the judge for readability.</summary>
    public string ReadabilityFeedback { get; init; } = "";
    /// <summary>Per-dimension feedback from the visual judge. Null when visual scoring is not applicable.</summary>
    public string? VisualsFeedback { get; init; }
}

/// <summary>Final evaluator verdict for a task: all candidates + the winner pick.</summary>
public record EvaluationResult
{
    public required IReadOnlyList<CandidateResult> Candidates { get; init; }
    public CandidateResult? Winner { get; init; }
    public string? TieBreakReason { get; init; }
    public TimeSpan EvaluationElapsed { get; init; }

    /// <summary>
    /// Absolute path to the winner's scratch worktree (kept alive after evaluation so callers
    /// can copy files directly instead of relying on <c>git apply</c>). Null when no winner
    /// was selected or the worktree was unavailable. Callers MUST dispose
    /// <see cref="WinnerWorktreeHandle"/> after use.
    /// </summary>
    public string? WinnerWorktreePath { get; init; }

    /// <summary>
    /// Ownership handle for the winner's worktree. The evaluator transfers ownership to the
    /// caller; dispose after <see cref="WinnerWorktreePath"/> is no longer needed.
    /// </summary>
    public IAsyncDisposable? WinnerWorktreeHandle { get; init; }
}
