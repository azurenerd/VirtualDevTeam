namespace VirtualDevTeam.Core.Strategies.Preview;

/// <summary>
/// Produces a visual preview for a strategy candidate. Implementations are tried in
/// priority order (lowest <see cref="Priority"/> value first) by
/// <see cref="CandidatePreviewService"/>; the first to return a non-null result wins.
/// Existing Playwright capture is one such producer; new producers will handle PRs
/// whose deliverable is image content (sprites, diagrams, generated art) rather than a
/// runnable app.
/// </summary>
public interface ICandidatePreviewProducer
{
    /// <summary>
    /// Priority — lower runs first. <c>PlaywrightCandidatePreviewProducer</c> should be
    /// HIGHEST (runs last) so image/diagram producers win when applicable.
    /// </summary>
    int Priority { get; }

    /// <summary>Stable identifier for diagnostics + dashboard badge mapping.</summary>
    string Id { get; }

    /// <summary>
    /// Attempt to produce a preview. Return <c>null</c> when this producer doesn't apply
    /// (e.g. no matching artifacts in the candidate's worktree).
    /// </summary>
    Task<CandidatePreview?> TryProduceAsync(CandidatePreviewContext context, CancellationToken ct);
}

/// <summary>
/// Inputs the producer needs: candidate identity + paths + per-strategy artifact dir.
/// </summary>
/// <param name="RunId">Strategy run identifier.</param>
/// <param name="TaskId">Engineering task identifier.</param>
/// <param name="StrategyId">Strategy identifier within the run (e.g. <c>baseline</c>, <c>agentic-1</c>).</param>
/// <param name="CandidateWorktreePath">Absolute path to the candidate's scratch worktree (where the patch was applied + built).</param>
/// <param name="ArtifactOutputDir">Absolute path to a durable directory where producers may write preview artifacts (created by caller).</param>
/// <param name="PrBranchName">Optional source branch for the candidate.</param>
/// <param name="PrTitle">Optional PR title (when known).</param>
/// <param name="PrBody">Optional PR body (when known).</param>
public sealed record CandidatePreviewContext(
    string RunId,
    string TaskId,
    string StrategyId,
    string CandidateWorktreePath,
    string ArtifactOutputDir,
    string? PrBranchName,
    string? PrTitle,
    string? PrBody);

/// <summary>What a producer returns when it has something to show.</summary>
public sealed record CandidatePreview
{
    /// <summary>Which producer produced this (matches <see cref="ICandidatePreviewProducer.Id"/>).</summary>
    public required string SourceProducerId { get; init; }

    /// <summary>
    /// Base64-encoded preview image (PNG), populated into
    /// <c>CandidateSnapshot.ScreenshotBase64</c>.
    /// </summary>
    public required string ScreenshotBase64 { get; init; }

    /// <summary>Optional video path (existing Playwright capture populates this).</summary>
    public string? VideoPath { get; init; }

    /// <summary>Optional animated GIF path.</summary>
    public string? AnimatedGifPath { get; init; }

    /// <summary>
    /// Categorization for UI: which kind of preview this is. The dashboard chooses
    /// which tab/badge to show based on this value.
    /// </summary>
    public required CandidatePreviewSource Source { get; init; }

    /// <summary>
    /// Optional list of source asset paths included in a contact-sheet preview
    /// (image-asset producer only).
    /// </summary>
    public IReadOnlyList<string>? IncludedAssetPaths { get; init; }

    /// <summary>
    /// Optional secondary preview attached to this primary preview, used for
    /// "mixed-content" PRs that have BOTH runnable code (a Playwright capture) AND
    /// committed image assets. The chain orchestrator (<see cref="CandidatePreviewService"/>)
    /// populates this when both an image-asset producer and a Playwright producer
    /// applied to the same candidate worktree:
    /// <list type="bullet">
    ///   <item><description><b>Primary</b> = the integrated running-app capture (Playwright).</description></item>
    ///   <item><description><b>Secondary</b> = the contact-sheet of committed art assets, rendered as a
    ///     small "Assets used" strip below the primary preview in the dashboard.</description></item>
    /// </list>
    /// Null in the common single-source case (the chain stopped at the first matching
    /// producer with nothing else to combine). Producers themselves do NOT populate this
    /// field — only <see cref="CandidatePreviewService.ProduceAsync"/> does, after running
    /// the chain a second time when the conditions for mixed-content are met.
    /// </summary>
    public CandidatePreview? SecondaryPreview { get; init; }
}

/// <summary>
/// Categorization of which kind of preview a producer emitted. Used by the
/// dashboard to choose which tab/badge to show for the candidate.
/// </summary>
public enum CandidatePreviewSource
{
    /// <summary>Captured by Playwright from a running app (current behavior).</summary>
    PlaywrightScreenshot = 0,

    /// <summary>Contact sheet of image assets committed in the PR.</summary>
    ImageAssets = 1,

    /// <summary>Rendered from Mermaid/PlantUML/SVG/drawio diagram sources.</summary>
    Diagrams = 2,

    /// <summary>No producer applied — placeholder image returned.</summary>
    NoVisualContent = 3,

    /// <summary>Capture environment unavailable (e.g. Playwright not ready) — no candidate penalty.</summary>
    CaptureUnavailable = 4,

    /// <summary>Capture attempted but the candidate app failed to start or only produced blank screenshots.</summary>
    CaptureFailed = 5,
}
