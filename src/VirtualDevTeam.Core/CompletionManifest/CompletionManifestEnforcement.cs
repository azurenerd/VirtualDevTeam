namespace VirtualDevTeam.Core.CompletionManifest;

/// <summary>
/// Discriminated-union result returned by <see cref="CompletionManifestEnforcement.Check"/>.
/// </summary>
public abstract record EnforcementResult
{
    /// <summary>All exports are fully implemented or explicitly marked stub-ok. PR may proceed.</summary>
    public sealed record Ok : EnforcementResult;

    /// <summary>
    /// One or more exports have <c>fully_implemented=false</c> and <c>stub_ok=false</c>.
    /// The PR must not be marked ready-for-review until these are resolved or annotated.
    /// </summary>
    /// <param name="Offenders">
    /// The subset of <see cref="ManifestExport"/> entries that are blocking the PR.
    /// </param>
    public sealed record BlockedByStub(IReadOnlyList<ManifestExport> Offenders) : EnforcementResult;
}

/// <summary>
/// Stateless helper that evaluates a <see cref="CompletionManifest"/> and determines
/// whether the PR should be blocked from the ready-for-review state.
///
/// <para>Called by <c>EngineerAgentBase.MarkPrCompleteAsync</c> and
/// <c>SoftwareEngineerAgent.FinalizeReadyForReviewAsync</c> (wired by WP-J in Wave 2).
/// This class itself does NOT modify any labels or post any comments.</para>
///
/// <para>Block condition: any export in <see cref="CompletionManifest.Exports"/> where
/// <c>fully_implemented == false</c> AND <c>stub_ok == false</c>.</para>
/// </summary>
public static class CompletionManifestEnforcement
{
    /// <summary>
    /// Evaluates the manifest and returns the enforcement result.
    /// </summary>
    /// <param name="manifest">The manifest to evaluate. Must not be null.</param>
    /// <returns>
    /// <see cref="EnforcementResult.Ok"/> when all exports pass.
    /// <see cref="EnforcementResult.BlockedByStub"/> with the offending exports when blocked.
    /// </returns>
    public static EnforcementResult Check(CompletionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var offenders = manifest.Exports
            .Where(e => !e.FullyImplemented && !e.StubOk)
            .ToList();

        return offenders.Count == 0
            ? new EnforcementResult.Ok()
            : new EnforcementResult.BlockedByStub(offenders);
    }

    /// <summary>
    /// Convenience wrapper: returns <c>true</c> when the manifest has at least one export
    /// that should prevent the PR from being marked ready-for-review.
    /// Equivalent to <c>Check(manifest) is EnforcementResult.BlockedByStub</c>.
    /// </summary>
    public static bool ShouldBlockReady(CompletionManifest manifest)
        => Check(manifest) is EnforcementResult.BlockedByStub;
}
