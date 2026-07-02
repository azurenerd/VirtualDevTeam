using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Persistence;

namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Singleton implementation of <see cref="IScenarioRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Loads scenarios from <c>Scenarios.md</c> (raw YAML) or falls back to the
/// <c># scenarios</c> block in <c>PMSpec.md</c>. Mirrors the canonical state to a
/// <c>scenarios.json</c> sidecar on every write.
/// </para>
/// <para>
/// On each load, if both a <c>scenarios.json</c> sidecar and a PMSpec YAML block exist,
/// their contents are compared. Any mismatch is logged at <c>Critical</c> level so that
/// a downstream FlowMonitor detector (WP-H, Wave 2) can surface it as a drift finding.
/// </para>
/// </remarks>
public sealed class ScenarioRegistry : IScenarioRegistry
{
    private const string ScenariosFile = "Scenarios.md";
    private const string SidecarFile = "scenarios.json";

    // Matches "Implements Scenarios: S01, S02" (case-insensitive, flexible spacing).
    private static readonly Regex ImplementsScenariosRegex = new(
        @"Implements\s+Scenarios?\s*:\s*([^\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches individual stable IDs like "S01", "S123" within the above match.
    private static readonly Regex ScenarioIdRegex = new(
        @"\bS\d+\b",
        RegexOptions.Compiled);

    private readonly ProjectFileManager _projectFiles;
    private readonly ILogger<ScenarioRegistry> _logger;

    private volatile IReadOnlyList<Scenario> _current = Array.Empty<Scenario>();
    private volatile bool _lastLoadHadDrift;

    /// <inheritdoc/>
    public event EventHandler<ScenarioRegistryChangedEventArgs>? Changed;

    /// <summary>
    /// Initializes a new instance of <see cref="ScenarioRegistry"/>.
    /// </summary>
    /// <param name="projectFileManager">File manager for reading/writing repo artifacts.</param>
    /// <param name="logger">Structured logger.</param>
    public ScenarioRegistry(
        ProjectFileManager projectFileManager,
        ILogger<ScenarioRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(projectFileManager);
        ArgumentNullException.ThrowIfNull(logger);
        _projectFiles = projectFileManager;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // IScenarioRegistry — data access
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public IReadOnlyList<Scenario> Current => _current;

    /// <inheritdoc/>
    public IReadOnlyList<Scenario> Critical =>
        _current.Where(s => s.Priority == ScenarioPriority.Critical).ToList().AsReadOnly();

    /// <inheritdoc/>
    public bool LastLoadHadDrift => _lastLoadHadDrift;

    /// <inheritdoc/>
    public Scenario? FindById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _current.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // IScenarioRegistry — I/O
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Scenario>> LoadAsync(CancellationToken ct = default)
    {
        // Reset drift flag so each load starts clean.
        _lastLoadHadDrift = false;

        // 1. Try Scenarios.md (standalone raw YAML file)
        IReadOnlyList<Scenario>? loaded = null;
        string? pmSpecContent = null;

        try
        {
            var scenariosPath = _projectFiles.ResolvePath(ScenariosFile);
            var scenariosContent = await _projectFiles.GetFileAsync(scenariosPath, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(scenariosContent))
            {
                _logger.LogDebug("Loading scenarios from {Path}", scenariosPath);
                loaded = ScenarioYamlExtractor.ExtractFromYamlString(scenariosContent, _logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read {File}; falling back to PMSpec", ScenariosFile);
        }

        // 2. Fall back to PMSpec.md # scenarios block
        if (loaded is null || loaded.Count == 0)
        {
            try
            {
                pmSpecContent = await _projectFiles.GetPMSpecAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(pmSpecContent))
                {
                    _logger.LogDebug("Loading scenarios from PMSpec.md # scenarios block");
                    loaded = ScenarioYamlExtractor.ExtractFromPmSpecMarkdown(pmSpecContent, _logger);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read PMSpec.md for scenario extraction");
            }
        }

        loaded ??= Array.Empty<Scenario>();

        // 3. Drift detection: if both sidecar JSON and PMSpec YAML block are available, compare.
        await DetectDriftAsync(loaded, pmSpecContent, ct).ConfigureAwait(false);

        // 4. Merge verification fields from sidecar (if present). PMSpec YAML is authoritative
        // for structure (title, steps, surfaces), but the sidecar carries runtime-only fields
        // (VerificationStatus, VerificationReason, VerificationEvidenceUrl, ImplementingTasks).
        loaded = await MergeSidecarVerificationFieldsAsync(loaded, ct);

        _current = loaded;
        _logger.LogInformation("ScenarioRegistry loaded {Count} scenarios", loaded.Count);
        OnChanged(loaded);
        return loaded;
    }

    /// <inheritdoc/>
    public async Task WriteSidecarAsync(IReadOnlyList<Scenario> scenarios, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        var json = ScenarioJsonSerializer.Serialize(scenarios);

        try
        {
            await _projectFiles.SaveScopedFileAsync(
                SidecarFile, json, "Update scenarios.json sidecar", ct)
                .ConfigureAwait(false);

            _logger.LogInformation("Wrote {Count} scenarios to {File}", scenarios.Count, SidecarFile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to write {File}", SidecarFile);
            throw;
        }

        _current = scenarios;
        OnChanged(scenarios);
    }

    private readonly SemaphoreSlim _updateLock = new(1, 1);

    /// <inheritdoc/>
    public async Task UpdateVerificationStatusAsync(
        string scenarioId,
        VerificationStatus status,
        string? reason = null,
        string? evidenceUrl = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        await _updateLock.WaitAsync(ct);
        try
        {
            var current = _current;
            var updated = current.Select(s =>
                string.Equals(s.Id, scenarioId, StringComparison.OrdinalIgnoreCase)
                    ? s with
                    {
                        VerificationStatus = status,
                        VerificationReason = reason ?? s.VerificationReason,
                        VerificationEvidenceUrl = evidenceUrl ?? s.VerificationEvidenceUrl,
                    }
                    : s
            ).ToList();

            if (!updated.Any(s => string.Equals(s.Id, scenarioId, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("UpdateVerificationStatus: scenario {Id} not found in registry", scenarioId);
                return;
            }

            await WriteSidecarAsync(updated, ct);
        }
        finally { _updateLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task UpdateImplementingTasksAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> scenarioIdToTasks,
        CancellationToken ct = default)
    {
        if (scenarioIdToTasks.Count == 0) return;
        await _updateLock.WaitAsync(ct);
        try
        {
            var current = _current;
            var updated = current.Select(s =>
            {
                if (scenarioIdToTasks.TryGetValue(s.Id, out var tasks))
                {
                    // Merge: keep existing + add new (deduplicated)
                    var merged = s.ImplementingTasks.Concat(tasks).Distinct().ToList();
                    return s with { ImplementingTasks = merged };
                }
                return s;
            }).ToList();

            await WriteSidecarAsync(updated, ct);
            _logger.LogInformation(
                "Updated ImplementingTasks for {Count} scenarios",
                scenarioIdToTasks.Count);
        }
        finally { _updateLock.Release(); }
    }

    // -------------------------------------------------------------------------
    // IScenarioRegistry — validation
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<bool> ValidateNoOrphans(CancellationToken ct = default)
    {
        if (_current.Count == 0)
        {
            _logger.LogDebug("ValidateNoOrphans: no scenarios loaded, skipping");
            return true;
        }

        string pmSpecContent;
        try
        {
            pmSpecContent = await _projectFiles.GetPMSpecAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ValidateNoOrphans: could not read PMSpec.md");
            return false;
        }

        // Collect all scenario IDs that appear in "Implements Scenarios: ..." lines.
        var cited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ImplementsScenariosRegex.Matches(pmSpecContent))
        {
            foreach (Match idMatch in ScenarioIdRegex.Matches(m.Groups[1].Value))
                cited.Add(idMatch.Value.ToUpperInvariant());
        }

        var valid = true;
        foreach (var scenario in _current)
        {
            if (scenario.Infrastructure)
                continue; // Infrastructure scenarios are exempt

            if (!cited.Contains(scenario.Id.ToUpperInvariant()))
            {
                _logger.LogWarning(
                    "Orphaned scenario {Id} ({Title}): not cited by any user story in PMSpec.md. " +
                    "Every user story must include 'Implements Scenarios: {Id}' if it satisfies this scenario.",
                    scenario.Id, scenario.Title, scenario.Id);
                valid = false;
            }
        }

        if (valid)
            _logger.LogDebug("ValidateNoOrphans: all {Count} non-infrastructure scenarios are cited", _current.Count);

        return valid;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Read the scenarios.json sidecar and merge runtime-only fields (verification status,
    /// reason, evidence URL, implementing tasks) onto the PMSpec-loaded scenarios. Structure
    /// (title, steps, etc.) always comes from PMSpec; runtime fields survive across restarts.
    /// </summary>
    private async Task<IReadOnlyList<Scenario>> MergeSidecarVerificationFieldsAsync(
        IReadOnlyList<Scenario> pmSpecScenarios, CancellationToken ct)
    {
        if (pmSpecScenarios.Count == 0) return pmSpecScenarios;

        try
        {
            var sidecarPath = _projectFiles.ResolvePath(SidecarFile);
            var sidecarJson = await _projectFiles.GetFileAsync(sidecarPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sidecarJson)) return pmSpecScenarios;

            var sidecarScenarios = ScenarioJsonSerializer.Deserialize(sidecarJson);
            if (sidecarScenarios.Count == 0) return pmSpecScenarios;

            var sidecarById = sidecarScenarios.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

            var merged = pmSpecScenarios.Select(pmSpec =>
            {
                if (!sidecarById.TryGetValue(pmSpec.Id, out var sidecar)) return pmSpec;

                // Only merge runtime fields; PMSpec structure is authoritative
                return pmSpec with
                {
                    VerificationStatus = sidecar.VerificationStatus != VerificationStatus.NotYetVerified
                        ? sidecar.VerificationStatus : pmSpec.VerificationStatus,
                    VerificationReason = sidecar.VerificationReason ?? pmSpec.VerificationReason,
                    VerificationEvidenceUrl = sidecar.VerificationEvidenceUrl ?? pmSpec.VerificationEvidenceUrl,
                    ImplementingTasks = sidecar.ImplementingTasks.Count > 0
                        ? sidecar.ImplementingTasks : pmSpec.ImplementingTasks,
                };
            }).ToList();

            var mergedCount = merged.Count(m =>
                sidecarById.ContainsKey(m.Id) &&
                m.VerificationStatus != VerificationStatus.NotYetVerified);

            if (mergedCount > 0)
                _logger.LogInformation(
                    "Merged {Count} verification field(s) from sidecar into PMSpec scenarios",
                    mergedCount);

            return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not merge sidecar verification fields — using PMSpec data only");
            return pmSpecScenarios;
        }
    }

    private async Task DetectDriftAsync(
        IReadOnlyList<Scenario> loadedScenarios,
        string? pmSpecContent,
        CancellationToken ct)
    {
        // Read the sidecar JSON.
        IReadOnlyList<Scenario>? sidecarScenarios = null;
        try
        {
            var sidecarPath = _projectFiles.ResolvePath(SidecarFile);
            var sidecarJson = await _projectFiles.GetFileAsync(sidecarPath, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(sidecarJson))
                sidecarScenarios = ScenarioJsonSerializer.Deserialize(sidecarJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "DetectDrift: could not read {File}", SidecarFile);
        }

        if (sidecarScenarios is null || sidecarScenarios.Count == 0)
            return; // No sidecar yet — nothing to compare.

        // Only flag drift when PMSpec also has a # scenarios block.
        if (string.IsNullOrWhiteSpace(pmSpecContent))
            return;

        IReadOnlyList<Scenario> pmSpecScenarios;
        try
        {
            pmSpecScenarios = ScenarioYamlExtractor.ExtractFromPmSpecMarkdown(pmSpecContent, _logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "DetectDrift: could not parse PMSpec # scenarios block");
            return;
        }

        if (pmSpecScenarios.Count == 0)
            return;

        // Compare ID sets as a fast structural check.
        var sidecarIds = new HashSet<string>(
            sidecarScenarios.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        var pmSpecIds = new HashSet<string>(
            pmSpecScenarios.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        if (!sidecarIds.SetEquals(pmSpecIds))
        {
            var onlyInSidecar = sidecarIds.Except(pmSpecIds, StringComparer.OrdinalIgnoreCase).ToList();
            var onlyInPmSpec = pmSpecIds.Except(sidecarIds, StringComparer.OrdinalIgnoreCase).ToList();

            _logger.LogCritical(
                "scenarios.json sidecar has DRIFTED from PMSpec.md # scenarios block. " +
                "IDs only in sidecar: [{SidecarOnly}]. IDs only in PMSpec: [{PmSpecOnly}]. " +
                "The PMSpec YAML block is authoritative; regenerate the sidecar via WriteSidecarAsync.",
                string.Join(", ", onlyInSidecar),
                string.Join(", ", onlyInPmSpec));

            // Surface drift to ScenariosDriftDetector via the Changed event (WP-H Wave 2).
            _lastLoadHadDrift = true;
        }
        else
        {
            _logger.LogDebug("DetectDrift: sidecar and PMSpec scenario ID sets match ({Count} IDs)", pmSpecIds.Count);
        }
    }

    private void OnChanged(IReadOnlyList<Scenario> scenarios) =>
        Changed?.Invoke(this, new ScenarioRegistryChangedEventArgs(scenarios));
}
