using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Strategies.Preview;

/// <summary>
/// Orchestrates a chain of <see cref="ICandidatePreviewProducer"/>s, trying each in
/// ascending <see cref="ICandidatePreviewProducer.Priority"/> order and returning the
/// first non-null result. When no producer applies, returns a
/// <see cref="CandidatePreviewSource.NoVisualContent"/> placeholder so the dashboard's
/// existing screenshot rendering path doesn't blow up on a missing image.
/// </summary>
/// <remarks>
/// Stability note: when two producers have the same <c>Priority</c>, the relative
/// ordering is determined by the iteration order of the injected
/// <see cref="IEnumerable{T}"/> (i.e. DI registration order), which is itself the order
/// of <c>OrderBy</c>'s stable sort. Callers who care about a strict ordering between
/// equal-priority producers should choose distinct <c>Priority</c> values.
/// </remarks>
public sealed class CandidatePreviewService
{
    private readonly IEnumerable<ICandidatePreviewProducer> _producers;
    private readonly ILogger<CandidatePreviewService> _logger;

    public CandidatePreviewService(
        IEnumerable<ICandidatePreviewProducer> producers,
        ILogger<CandidatePreviewService> logger)
    {
        _producers = producers ?? throw new ArgumentNullException(nameof(producers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs producers in priority order; returns the first non-null result.
    /// Falls back to a <see cref="CandidatePreviewSource.NoVisualContent"/> placeholder
    /// when every producer declines (returns null) or throws.
    /// <para>
    /// <b>Mixed-content handling.</b> When the first non-null result is from the
    /// <see cref="CandidatePreviewSource.ImageAssets"/> producer AND the candidate's
    /// worktree also looks runnable (a <c>launchSettings.json</c> exists somewhere
    /// under it), the chain ALSO runs the Playwright producer. If Playwright also
    /// returns a result, the two are recombined: PRIMARY becomes the Playwright
    /// capture (the integrated running-app screenshot) and the original ImageAssets
    /// result is attached as <see cref="CandidatePreview.SecondaryPreview"/> (the
    /// "Assets used" strip rendered below the primary preview in the dashboard).
    /// When only the image producer applies, it stays as the primary; when only
    /// Playwright applies, today's behavior (Playwright as primary, no secondary)
    /// is preserved.
    /// </para>
    /// </summary>
    public async Task<CandidatePreview> ProduceAsync(CandidatePreviewContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        CandidatePreview? primary = null;
        ICandidatePreviewProducer? primaryProducer = null;
        var chainResults = new Dictionary<string, CandidatePreview?>(StringComparer.Ordinal);

        foreach (var p in _producers.OrderBy(x => x.Priority))
        {
            try
            {
                var result = await p.TryProduceAsync(context, ct).ConfigureAwait(false);
                chainResults[p.Id] = result;
                if (result is not null)
                {
                    _logger.LogDebug(
                        "Preview produced by '{Producer}' for task {TaskId}/{Strategy}",
                        p.Id, context.TaskId, context.StrategyId);
                    primary = result;
                    primaryProducer = p;
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Preview producer '{Producer}' threw for task {TaskId}/{Strategy} — moving to next.",
                    p.Id, context.TaskId, context.StrategyId);
                chainResults[p.Id] = null;
            }
        }

        if (primary is null)
        {
            // No producer applied — return a placeholder with a 1×1 transparent PNG so
            // existing UI doesn't blow up trying to render a missing screenshot.
            chainResults.TryGetValue(PlaywrightProducerId, out var playResult);
            chainResults.TryGetValue("image-assets", out var imgResult);
            chainResults.TryGetValue("diagrams", out var diagResult);

            _logger.LogInformation(
                "No preview producer produced output for {TaskId}/{Strategy} — Playwright={PlayResult}, ImageAssets={ImgResult}, Diagrams={DiagResult}",
                context.TaskId, context.StrategyId,
                playResult?.Source.ToString() ?? "null",
                imgResult?.Source.ToString() ?? "null",
                diagResult?.Source.ToString() ?? "null");

            return new CandidatePreview
            {
                SourceProducerId = "none",
                ScreenshotBase64 = OnePixelTransparentPng,
                Source = CandidatePreviewSource.NoVisualContent,
            };
        }

        // ── Mixed-content layering ──────────────────────────────────────────
        // When the winner is ImageAssets AND the worktree contains a runnable app
        // (launchSettings.json), also run the Playwright producer. If it produces a
        // result, swap so primary = Playwright, secondary = ImageAssets.
        if (primary.Source == CandidatePreviewSource.ImageAssets &&
            HasLaunchSettings(context.CandidateWorktreePath))
        {
            var playwrightProducer = _producers
                .Where(p => string.Equals(p.Id, PlaywrightProducerId, StringComparison.Ordinal) && !ReferenceEquals(p, primaryProducer))
                .OrderBy(p => p.Priority)
                .FirstOrDefault();

            if (playwrightProducer is not null)
            {
                CandidatePreview? secondary = null;
                try
                {
                    secondary = await playwrightProducer.TryProduceAsync(context, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Mixed-content second-pass producer '{Producer}' threw for task {TaskId}/{Strategy} — keeping single-source primary.",
                        playwrightProducer.Id, context.TaskId, context.StrategyId);
                }

                if (secondary is not null)
                {
                    _logger.LogInformation(
                        "Mixed-content preview composed for task {TaskId}/{Strategy}: primary='{Primary}' (was {OldSource}), secondary='{Secondary}'.",
                        context.TaskId, context.StrategyId,
                        secondary.SourceProducerId, primary.Source, primary.SourceProducerId);

                    return secondary with { SecondaryPreview = primary };
                }
            }
        }

        return primary;
    }

    /// <summary>
    /// Cheap launchSettings probe — walks up to 4 directory levels under the worktree
    /// looking for a <c>launchSettings.json</c> file. Used to gate the mixed-content
    /// second-pass that runs the Playwright producer in addition to the image producer.
    /// </summary>
    private static bool HasLaunchSettings(string worktree)
    {
        if (string.IsNullOrWhiteSpace(worktree) || !Directory.Exists(worktree)) return false;

        try
        {
            // Most .NET projects place launchSettings.json at <project>/Properties/launchSettings.json,
            // so 3-4 levels of recursion under the worktree root is enough to find them without
            // walking the entire tree (which can include node_modules, bin/, obj/, etc.).
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 4,
                AttributesToSkip = FileAttributes.System,
            };
            return Directory.EnumerateFiles(worktree, "launchSettings.json", opts).Any();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pre-encoded 1×1 transparent PNG. Used as the placeholder image when no producer
    /// applies, so callers that always expect a non-null <c>ScreenshotBase64</c> keep
    /// working without special-casing the placeholder branch.
    /// </summary>
    internal const string OnePixelTransparentPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    /// <summary>
    /// Stable <see cref="ICandidatePreviewProducer.Id"/> for the Playwright producer,
    /// used by the mixed-content layering pass to find the second producer to invoke.
    /// Matches <c>PlaywrightCandidatePreviewProducer.Id</c>.
    /// </summary>
    internal const string PlaywrightProducerId = "playwright";
}
