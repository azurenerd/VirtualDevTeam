using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Scenarios;

namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Concrete implementation of <see cref="IAppPlaytester"/>.
/// Iterates each approved scenario, obtains a deterministic action plan from the LLM,
/// dispatches actions via the appropriate <see cref="IPlaytestAdapter"/>, and aggregates
/// evidence through the three-layer judge.
/// </summary>
public sealed class AppPlaytester : IAppPlaytester
{
    private readonly IScenarioRegistry _scenarioRegistry;
    private readonly IChatCompletionRunner _chatRunner;
    private readonly IEnumerable<IPlaytestAdapter> _adapters;
    private readonly ILogger<AppPlaytester> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public AppPlaytester(
        IScenarioRegistry scenarioRegistry,
        IChatCompletionRunner chatRunner,
        IEnumerable<IPlaytestAdapter> adapters,
        ILogger<AppPlaytester> logger)
    {
        ArgumentNullException.ThrowIfNull(scenarioRegistry);
        ArgumentNullException.ThrowIfNull(chatRunner);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(logger);

        _scenarioRegistry = scenarioRegistry;
        _chatRunner = chatRunner;
        _adapters = adapters;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<PlaytestReport[]> RunAsync(
        AppHandle handle,
        IReadOnlyList<Scenario>? scenarios = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var scenariosToRun = (scenarios
            ?? _scenarioRegistry.Current
                .Where(s => s.Status == ScenarioStatus.Approved)
                .ToList())
            .ToList();

        if (scenariosToRun.Count == 0)
        {
            _logger.LogWarning("AppPlaytester: no approved scenarios to run");
            return [];
        }

        _logger.LogInformation("AppPlaytester: starting playtest run — {Count} scenario(s)", scenariosToRun.Count);

        var reports = new List<PlaytestReport>(scenariosToRun.Count);
        var priorTraceJson = "[]";

        foreach (var scenario in scenariosToRun)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("AppPlaytester: verifying scenario {Id} — {Title}", scenario.Id, scenario.Title);

            var report = await RunScenarioAsync(scenario, handle, priorTraceJson, ct);
            reports.Add(report);

            // Update prior trace for the next scenario (state carry-forward context)
            priorTraceJson = BuildPriorTraceJson(reports);
        }

        _logger.LogInformation(
            "AppPlaytester: run complete — {Verified} verified, {Broken} broken, {Inconclusive} inconclusive",
            reports.Count(r => r.Verdict == VerificationStatus.Verified),
            reports.Count(r => r.Verdict == VerificationStatus.Broken),
            reports.Count(r => r.Verdict == VerificationStatus.Inconclusive));

        return [.. reports];
    }

    // ─── Per-scenario execution ───────────────────────────────────────────────

    private async Task<PlaytestReport> RunScenarioAsync(
        Scenario scenario,
        AppHandle handle,
        string priorTraceJson,
        CancellationToken ct)
    {
        // 1. Obtain action plan from LLM
        PlaytestActionPlan? plan = null;
        string? planError = null;
        try
        {
            plan = await GetActionPlanAsync(scenario, handle, priorTraceJson, ct);
        }
        catch (Exception ex)
        {
            planError = $"Action plan generation failed: {ex.Message}";
            _logger.LogWarning(ex, "AppPlaytester: failed to generate action plan for {Id}", scenario.Id);
        }

        if (plan is null)
        {
            return new PlaytestReport
            {
                ScenarioId = scenario.Id,
                Title = scenario.Title,
                JourneyKind = scenario.JourneyKind.ToString().ToLowerInvariant(),
                Priority = scenario.Priority.ToString().ToLowerInvariant(),
                Verdict = VerificationStatus.Inconclusive,
                Confidence = 0.0,
                OperatorReviewRequired = true,
                AmbiguityNote = planError ?? "Action plan was null",
                ExecutionError = planError,
                Layer1Result = VerificationStatus.Inconclusive,
                Layer2Result = VerificationStatus.Inconclusive,
                Layer3Result = VerificationStatus.Inconclusive,
            };
        }

        // 2. Execute actions (Layer 1 — deterministic)
        var (evidence, layer1Result, layer1Confidence, failedSurfaces) =
            await ExecuteLayer1Async(plan, handle, ct);

        // 3. Layer 2 — LLM vision assessment (stub: inconclusive when no vision available)
        var (layer2Result, layer2Note) = RunLayer2Vision(evidence);

        // 4. Layer 3 — Narrative judge
        Layer3NarrativeAssessment? narrativeAssessment = null;
        VerificationStatus layer3Result = VerificationStatus.Inconclusive;
        try
        {
            narrativeAssessment = await RunLayer3NarrativeAsync(scenario, plan, evidence, layer2Note, ct);
            layer3Result = ParseLayer3Verdict(narrativeAssessment.Layer3Verdict);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AppPlaytester: Layer-3 narrative judge failed for {Id}", scenario.Id);
        }

        // 5. Aggregate: most conservative wins
        var finalVerdict = ConservativeMerge(layer1Result, layer2Result, layer3Result);
        var finalConfidence = AggregateConfidence(layer1Confidence, narrativeAssessment?.Confidence ?? 0.5);
        var operatorReview = finalVerdict != VerificationStatus.Verified
                             || (narrativeAssessment?.OperatorReviewRequired ?? false);

        return new PlaytestReport
        {
            ScenarioId = scenario.Id,
            Title = scenario.Title,
            JourneyKind = scenario.JourneyKind.ToString().ToLowerInvariant(),
            Priority = scenario.Priority.ToString().ToLowerInvariant(),
            Verdict = finalVerdict,
            Confidence = finalConfidence,
            OperatorReviewRequired = operatorReview,
            AmbiguityNote = BuildAmbiguityNote(layer1Result, layer2Result, layer3Result, narrativeAssessment),
            ActionPlanExecuted = plan,
            Evidence = evidence,
            FailedSurfaces = failedSurfaces,
            Layer2VisionNote = layer2Note,
            NarrativeAssessment = narrativeAssessment,
            Layer1Result = layer1Result,
            Layer2Result = layer2Result,
            Layer3Result = layer3Result,
        };
    }

    // ─── Layer 1: Deterministic execution ─────────────────────────────────────

    private async Task<(
        IReadOnlyList<EvidenceEntry> evidence,
        VerificationStatus layer1Result,
        double confidence,
        IReadOnlyList<string> failedSurfaces)>
        ExecuteLayer1Async(
            PlaytestActionPlan plan,
            AppHandle handle,
            CancellationToken ct)
    {
        var evidenceEntries = new List<EvidenceEntry>();
        var snapshots = new Dictionary<string, string?>(StringComparer.Ordinal);
        var failedSurfaces = new List<string>();
        var inconclusiveSurfaces = new List<string>();
        int assertionCount = 0;
        int passedCount = 0;

        // Dispose web adapter if needed at end
        WebPlaytestAdapter? webAdapter = null;

        try
        {
            foreach (var action in plan.Actions)
            {
                ct.ThrowIfCancellationRequested();

                var adapter = SelectAdapter(action);
                if (adapter is WebPlaytestAdapter wa) webAdapter = wa;

                if (adapter is null)
                {
                    var incEvidence = new InconclusiveEvidence(
                        action.SurfaceVerified ?? action.ActionCategory,
                        $"No adapter found for action type '{action.ActionType}'");
                    evidenceEntries.Add(new EvidenceEntry
                    {
                        StepIndex = action.StepIndex,
                        Action = FormatActionLabel(action),
                        Evidence = incEvidence,
                    });
                    if (action.SurfaceVerified is not null)
                        inconclusiveSurfaces.Add(action.SurfaceVerified);
                    continue;
                }

                var evidence = await adapter.ExecuteAsync(action, handle, snapshots, ct);

                string? screenshotHandle = null;
                if (evidence is ScreenshotEvidence ss)
                    screenshotHandle = ss.Filename;

                evidenceEntries.Add(new EvidenceEntry
                {
                    StepIndex = action.StepIndex,
                    Action = FormatActionLabel(action),
                    Evidence = evidence,
                    ScreenshotHandle = screenshotHandle,
                });

                if (action.SurfaceVerified is not null)
                {
                    assertionCount++;
                    if (evidence.Passed)
                        passedCount++;
                    else if (evidence.IsInconclusive)
                        inconclusiveSurfaces.Add(action.SurfaceVerified);
                    else
                        failedSurfaces.Add(action.SurfaceVerified);
                }
            }
        }
        finally
        {
            if (webAdapter is not null)
                await webAdapter.DisposeAsync();
        }

        VerificationStatus layer1;
        if (failedSurfaces.Count > 0)
            layer1 = VerificationStatus.Broken;
        else if (inconclusiveSurfaces.Count > 0 || assertionCount == 0)
            layer1 = VerificationStatus.Inconclusive;
        else
            layer1 = VerificationStatus.Verified;

        double confidence = assertionCount == 0 ? 0.5
            : 0.95 * ((double)passedCount / assertionCount)
              - 0.1 * inconclusiveSurfaces.Count;
        confidence = Math.Clamp(confidence, 0.0, 1.0);

        var allFailed = new List<string>(failedSurfaces);
        allFailed.AddRange(inconclusiveSurfaces);

        return (evidenceEntries, layer1, confidence, allFailed);
    }

    // ─── Layer 2: Vision assessment (stub) ────────────────────────────────────

    private static (VerificationStatus result, string? note) RunLayer2Vision(IReadOnlyList<EvidenceEntry> evidence)
    {
        var screenshots = evidence
            .Where(e => e.Evidence is ScreenshotEvidence)
            .Select(e => (ScreenshotEvidence)e.Evidence)
            .ToList();

        if (screenshots.Count == 0)
            return (VerificationStatus.Inconclusive, null);

        // TODO (D6 Layer 2): When a multimodal model is available via IChatCompletionRunner,
        // encode screenshots as base64, call the model with the scenario's expected_terminal_state,
        // and parse a vision verdict. For now, we mark Layer 2 inconclusive (it doesn't penalise
        // the final verdict beyond what Layer 1 already determined).
        //
        // The conservative-merge rule means:
        //   Layer 1 = Verified + Layer 2 = Inconclusive → final = Inconclusive
        //   Layer 1 = Broken   + Layer 2 = Inconclusive → final = Broken
        // This is intentionally cautious.
        return (VerificationStatus.Inconclusive,
            $"Layer-2 vision assessment skipped — {screenshots.Count} screenshot(s) captured but no multimodal model configured. " +
            "Mark as inconclusive pending vision support.");
    }

    // ─── Layer 3: Narrative judge ──────────────────────────────────────────────

    private async Task<Layer3NarrativeAssessment> RunLayer3NarrativeAsync(
        Scenario scenario,
        PlaytestActionPlan plan,
        IReadOnlyList<EvidenceEntry> evidence,
        string? layer2Note,
        CancellationToken ct)
    {
        var scenarioYaml = BuildScenarioYaml(scenario);
        var actionPlanJson = JsonSerializer.Serialize(plan, _jsonOptions);
        var evidenceTraceJson = BuildEvidenceTraceJson(evidence);
        var screenshotDescriptions = layer2Note ?? "(no screenshots or vision assessment)";

        // System prompt for the narrative judge — inline since we cannot modify prompts/playtester/
        const string systemPrompt =
            "You are the Layer-3 Narrative Judge in the App Playtester's three-layer verification stack. " +
            "Your role is strictly evaluative — you do not execute actions. " +
            "Assess whether the evidence trace tells a coherent story matching the scenario's expected_terminal_state. " +
            "Return ONLY valid JSON matching the schema from report-narrative.md. No markdown fences. No prose.";

        var userPrompt = BuildNarrativeJudgePrompt(
            scenarioYaml, actionPlanJson, evidenceTraceJson, screenshotDescriptions);

        _logger.LogDebug("AppPlaytester: invoking Layer-3 narrative judge for scenario {Id}", scenario.Id);

        string judgeResponse;
        try
        {
            judgeResponse = await _chatRunner.InvokeAsync(
                systemPrompt, userPrompt,
                modelTier: "standard",
                agentId: "app-playtester",
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AppPlaytester: Layer-3 LLM call failed for scenario {Id}", scenario.Id);
            return new Layer3NarrativeAssessment
            {
                Layer3Verdict = "inconclusive",
                Confidence = 0.5,
                OperatorReviewRequired = true,
                AmbiguityNote = $"Layer-3 LLM call failed: {ex.Message}",
            };
        }

        return ParseNarrativeVerdict(judgeResponse, scenario.Id);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private IPlaytestAdapter? SelectAdapter(PlaytestAction action)
    {
        foreach (var adapter in _adapters)
        {
            if (adapter.CanHandle(action))
                return adapter;
        }
        return null;
    }

    private static VerificationStatus ConservativeMerge(
        VerificationStatus l1,
        VerificationStatus l2,
        VerificationStatus l3)
    {
        // Conservative order: Broken > Inconclusive > Verified
        static int Weight(VerificationStatus s) => s switch
        {
            VerificationStatus.Broken => 2,
            VerificationStatus.Inconclusive => 1,
            VerificationStatus.Verified => 0,
            _ => 1,
        };

        // Layer 2 (vision) is currently a stub that always returns Inconclusive.
        // A not-yet-implemented layer must not veto the verdict from layers that
        // actually ran. Exclude Layer 2 from the merge when it is Inconclusive
        // (i.e. the stub path), so the final verdict is determined by Layer 1
        // and Layer 3 only. Once Layer 2 has a real implementation, the
        // Inconclusive it returns will reflect a genuine assessment failure, and
        // the TODO below should be revisited.
        var effectiveL2 = l2 == VerificationStatus.Inconclusive
            ? VerificationStatus.Verified   // neutral — doesn't lower the score
            : l2;

        var maxWeight = Math.Max(Weight(l1), Math.Max(Weight(effectiveL2), Weight(l3)));
        return maxWeight switch
        {
            2 => VerificationStatus.Broken,
            1 => VerificationStatus.Inconclusive,
            _ => VerificationStatus.Verified,
        };
    }

    private static double AggregateConfidence(double layer1Confidence, double layer3Confidence)
        => Math.Round((layer1Confidence + layer3Confidence) / 2.0, 3);

    private static VerificationStatus ParseLayer3Verdict(string? verdict) =>
        verdict?.ToLowerInvariant() switch
        {
            "verified" => VerificationStatus.Verified,
            "broken" => VerificationStatus.Broken,
            _ => VerificationStatus.Inconclusive,
        };

    private static string? BuildAmbiguityNote(
        VerificationStatus l1,
        VerificationStatus l2,
        VerificationStatus l3,
        Layer3NarrativeAssessment? narrativeAssessment)
    {
        if (l1 == VerificationStatus.Verified && l2 == VerificationStatus.Verified && l3 == VerificationStatus.Verified)
            return null;

        var sb = new StringBuilder();
        if (l1 != VerificationStatus.Verified) sb.AppendLine($"Layer 1 (deterministic): {l1}");
        if (l2 != VerificationStatus.Verified) sb.AppendLine($"Layer 2 (vision): {l2}");
        if (l3 != VerificationStatus.Verified) sb.AppendLine($"Layer 3 (narrative): {l3}");
        if (narrativeAssessment?.AmbiguityNote is string note) sb.AppendLine(note);
        return sb.ToString().Trim();
    }

    private static string FormatActionLabel(PlaytestAction action)
    {
        if (action.ActionType.StartsWith("page.", StringComparison.OrdinalIgnoreCase)
            || action.ActionType.StartsWith("assert.", StringComparison.OrdinalIgnoreCase))
        {
            var selector = action.GetParam("selector") ?? action.GetParam("eventName");
            return selector is not null ? $"{action.ActionType}('{selector}')" : action.ActionType;
        }
        if (action.ActionType.StartsWith("http.", StringComparison.OrdinalIgnoreCase))
        {
            return $"{action.ActionType}('{action.GetParam("path") ?? "/"}')";
        }
        if (action.ActionType.StartsWith("cli.", StringComparison.OrdinalIgnoreCase))
        {
            return $"{action.ActionType}('{action.GetParam("binary") ?? ""}')";
        }
        return action.ActionType;
    }

    private static string BuildScenarioYaml(Scenario s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"id: {s.Id}");
        sb.AppendLine($"title: \"{s.Title}\"");
        sb.AppendLine($"journey_kind: {s.JourneyKind.ToString().ToLowerInvariant().Replace("interaction", "_interaction")}");
        sb.AppendLine($"actor: \"{s.Actor}\"");
        sb.AppendLine($"trigger: \"{s.Trigger}\"");
        if (s.Steps.Count > 0)
        {
            sb.AppendLine("steps:");
            foreach (var step in s.Steps) sb.AppendLine($"  - \"{step}\"");
        }
        if (s.ExpectedTerminalState.Count > 0)
        {
            sb.AppendLine("expected_terminal_state:");
            foreach (var state in s.ExpectedTerminalState) sb.AppendLine($"  - \"{state}\"");
        }
        if (s.ObservationSurfaces.Count > 0)
        {
            sb.AppendLine("observation_surfaces:");
            foreach (var surface in s.ObservationSurfaces)
            {
                sb.AppendLine($"  - kind: {surface.Kind}");
                foreach (var (k, v) in surface.Fields)
                    sb.AppendLine($"    {k}: {v}");
            }
        }
        sb.AppendLine($"priority: {s.Priority.ToString().ToLowerInvariant()}");
        return sb.ToString();
    }

    private static string BuildEvidenceTraceJson(IReadOnlyList<EvidenceEntry> entries)
    {
        var items = entries.Select(e => new
        {
            step_index = e.StepIndex,
            action = e.Action,
            observed = e.Evidence.ErrorMessage ?? (e.Evidence.Passed ? "passed" : "failed"),
            screenshot_handle = e.ScreenshotHandle,
            assertion_passed = e.AssertionPassed,
            inconclusive = e.Evidence.IsInconclusive,
        });
        return JsonSerializer.Serialize(items, _jsonOptions);
    }

    private static string BuildPriorTraceJson(List<PlaytestReport> completedReports)
    {
        var items = completedReports.Select(r => new
        {
            scenario_id = r.ScenarioId,
            verdict = r.Verdict.ToString().ToLowerInvariant(),
            failed_surfaces = r.FailedSurfaces,
        });
        return JsonSerializer.Serialize(items, _jsonOptions);
    }

    private static string BuildNarrativeJudgePrompt(
        string scenarioYaml,
        string actionPlanJson,
        string evidenceTraceJson,
        string screenshotDescriptions)
    {
        return $"""
            ### Scenario definition

            ```yaml
            {scenarioYaml}
            ```

            ### Action plan executed

            ```json
            {actionPlanJson}
            ```

            ### Evidence trace (ordered — one entry per executed action)

            ```json
            {evidenceTraceJson}
            ```

            ### Screenshot descriptions (Layer-2 vision summaries, indexed by filename)

            ```
            {screenshotDescriptions}
            ```

            Return ONLY valid JSON. No markdown fences. No prose.
            """;
    }

    private static Layer3NarrativeAssessment ParseNarrativeVerdict(string json, string scenarioId)
    {
        try
        {
            // Strip any markdown fences the LLM may have added despite instructions
            json = json.Trim();
            if (json.StartsWith("```")) json = json[(json.IndexOf('\n') + 1)..];
            if (json.EndsWith("```")) json = json[..json.LastIndexOf("```")];
            json = json.Trim();

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string GetStr(string key, string fallback = "")
                => root.TryGetProperty(key, out var el) ? el.GetString() ?? fallback : fallback;

            double GetDouble(string key, double fallback)
                => root.TryGetProperty(key, out var el)
                   && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : fallback;

            bool GetBool(string key, bool fallback)
                => root.TryGetProperty(key, out var el)
                   && el.ValueKind == JsonValueKind.True ? true
                   : el.ValueKind == JsonValueKind.False ? false : fallback;

            NarrativeCoherence? coherence = null;
            if (root.TryGetProperty("narrative_coherence", out var ncEl))
            {
                var coherent = ncEl.TryGetProperty("coherent", out var cEl) && cEl.GetBoolean();
                var summary = ncEl.TryGetProperty("summary", out var sEl) ? sEl.GetString() : null;
                coherence = new NarrativeCoherence { Coherent = coherent, Summary = summary };
            }

            return new Layer3NarrativeAssessment
            {
                Layer3Verdict = GetStr("layer3_verdict", "inconclusive"),
                Confidence = GetDouble("confidence", 0.5),
                OperatorReviewRequired = GetBool("operator_review_required", false),
                AmbiguityNote = GetStr("ambiguity_note"),
                Recommendation = GetStr("recommendation"),
                NarrativeCoherence = coherence,
            };
        }
        catch (Exception ex)
        {
            return new Layer3NarrativeAssessment
            {
                Layer3Verdict = "inconclusive",
                Confidence = 0.3,
                OperatorReviewRequired = true,
                AmbiguityNote = $"Failed to parse Layer-3 JSON for {scenarioId}: {ex.Message}. Raw: {json[..Math.Min(200, json.Length)]}",
            };
        }
    }

    private async Task<PlaytestActionPlan> GetActionPlanAsync(
        Scenario scenario,
        AppHandle handle,
        string priorTraceJson,
        CancellationToken ct)
    {
        var scenarioYaml = BuildScenarioYaml(scenario);

        const string systemPrompt =
            "You are the App Playtester for the VirtualDevTeam pipeline. " +
            "Given a scenario YAML and app handle, produce a deterministic JSON action plan. " +
            "Return ONLY valid JSON — no markdown fences, no prose.";

        var safetyBlock = scenario.InteractiveValidationSafe
            ? ""
            : """

            SAFETY: Do NOT include actions that perform destructive or irreversible operations (delete, archive,
            purge, revoke, disable, drop, remove permanently). For scenarios testing destructive features,
            stop at verifying the confirmation dialog exists — do NOT confirm the destructive action.
            Do NOT interact with external production systems in ways that modify state.
            """;

        var userPrompt = $"""
            ## Scenario to verify

            ```yaml
            {scenarioYaml}
            ```

            ## Live application handle

            - Base URL / handle: `{handle.BaseUrl}`
            - PlaytestContext (adapter config, DB connection string, CLI binary path): `{handle.PlaytestContextJson ?? "{}"}`

            ## Prior trace evidence (from earlier scenarios in this run, if any)

            ```json
            {priorTraceJson}
            ```

            Use the prior trace only to understand application state. Do not use it to skip steps.
            {safetyBlock}
            Produce the exact, deterministic action plan the IPlaytestAdapter will execute to verify this scenario.
            Cover every step in the scenario's steps array — in order.
            Include an explicit assertion action for every entry in the scenario's observation_surfaces array.
            Include a screenshot action at the final step for all ui_interaction scenarios.

            CRITICAL: All property names MUST use snake_case (e.g. "action_type", "step_index", "scenario_step",
            "scenario_id", "journey_kind", "surface_kind", "surface_index", "captures_snapshot", "snapshot_key",
            "surface_verified", "terminal_assertions", "precondition_check", "final_screenshot").
            Do NOT use camelCase.

            Return ONLY valid JSON. No markdown fences. No prose.
            """;

        var rawJson = await _chatRunner.InvokeAsync(
            systemPrompt, userPrompt,
            modelTier: "standard",
            agentId: "app-playtester",
            ct: ct);

        // Strip markdown fences
        rawJson = rawJson.Trim();
        if (rawJson.StartsWith("```")) rawJson = rawJson[(rawJson.IndexOf('\n') + 1)..];
        if (rawJson.EndsWith("```")) rawJson = rawJson[..rawJson.LastIndexOf("```")];
        rawJson = rawJson.Trim();

        // Extract JSON from prose responses — LLM sometimes wraps JSON in explanatory text
        if (!rawJson.StartsWith('{'))
        {
            var jsonStart = rawJson.IndexOf('{');
            var jsonEnd = rawJson.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                _logger.LogWarning("AppPlaytester: LLM returned prose wrapping JSON — extracting embedded JSON object");
                rawJson = rawJson[jsonStart..(jsonEnd + 1)];
            }
            else
            {
                throw new InvalidOperationException(
                    $"LLM returned non-JSON response for action plan (starts with '{rawJson[..Math.Min(20, rawJson.Length)]}...')");
            }
        }

        // Normalize camelCase property names to snake_case — LLMs inconsistently respect naming
        rawJson = NormalizeCamelCaseProperties(rawJson);

        var plan = JsonSerializer.Deserialize<PlaytestActionPlan>(rawJson, _jsonOptions)
               ?? throw new InvalidOperationException("LLM returned null action plan JSON");

        // Post-deserialization validation — catch partial/invalid plans before execution
        if (plan.Actions.Count == 0)
            throw new InvalidOperationException("LLM returned action plan with zero actions");

        var emptyActionTypes = plan.Actions
            .Select((a, i) => (a, i))
            .Where(x => string.IsNullOrWhiteSpace(x.a.ActionType))
            .ToList();

        if (emptyActionTypes.Count > 0)
        {
            var indices = string.Join(", ", emptyActionTypes.Select(x => x.i));
            throw new InvalidOperationException(
                $"LLM action plan has {emptyActionTypes.Count} action(s) with empty ActionType at indices [{indices}]. " +
                "The LLM likely used a property name variant not covered by normalization.");
        }

        return plan;
    }

    /// <summary>
    /// Normalizes common camelCase JSON property names to snake_case to handle LLM inconsistency.
    /// Uses JSON-aware replacement: only replaces property names (keys), not string values.
    /// </summary>
    private static string NormalizeCamelCaseProperties(string json)
    {
        // Parse as JsonNode for safe property-name-only rewriting
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is System.Text.Json.Nodes.JsonObject root)
            {
                NormalizeObjectPropertyNames(root);
                return root.ToJsonString(_serializerOptions);
            }
        }
        catch
        {
            // Fall through to string-based normalization if JsonNode parse fails
        }

        // Fallback: simple string replacement (less safe but better than nothing)
        ReadOnlySpan<(string CamelCase, string SnakeCase)> mappings =
        [
            ("\"actionType\":", "\"action_type\":"),
            ("\"stepIndex\":", "\"step_index\":"),
            ("\"scenarioStep\":", "\"scenario_step\":"),
            ("\"scenarioId\":", "\"scenario_id\":"),
            ("\"journeyKind\":", "\"journey_kind\":"),
            ("\"surfaceKind\":", "\"surface_kind\":"),
            ("\"surfaceIndex\":", "\"surface_index\":"),
            ("\"capturesSnapshot\":", "\"captures_snapshot\":"),
            ("\"snapshotKey\":", "\"snapshot_key\":"),
            ("\"surfaceVerified\":", "\"surface_verified\":"),
            ("\"terminalAssertions\":", "\"terminal_assertions\":"),
            ("\"preconditionCheck\":", "\"precondition_check\":"),
            ("\"finalScreenshot\":", "\"final_screenshot\":"),
        ];

        foreach (var (camel, snake) in mappings)
        {
            if (json.Contains(camel, StringComparison.Ordinal))
                json = json.Replace(camel, snake, StringComparison.Ordinal);
        }

        return json;
    }

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Mapping from normalized (lowercased, no separators) to correct snake_case name.</summary>
    private static readonly Dictionary<string, string> s_nameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["actiontype"] = "action_type",
        ["action_type"] = "action_type",
        ["stepindex"] = "step_index",
        ["step_index"] = "step_index",
        ["scenariostep"] = "scenario_step",
        ["scenario_step"] = "scenario_step",
        ["scenarioid"] = "scenario_id",
        ["scenario_id"] = "scenario_id",
        ["journeykind"] = "journey_kind",
        ["journey_kind"] = "journey_kind",
        ["surfacekind"] = "surface_kind",
        ["surface_kind"] = "surface_kind",
        ["surfaceindex"] = "surface_index",
        ["surface_index"] = "surface_index",
        ["capturessnapshot"] = "captures_snapshot",
        ["captures_snapshot"] = "captures_snapshot",
        ["snapshotkey"] = "snapshot_key",
        ["snapshot_key"] = "snapshot_key",
        ["surfaceverified"] = "surface_verified",
        ["surface_verified"] = "surface_verified",
        ["terminalassertions"] = "terminal_assertions",
        ["terminal_assertions"] = "terminal_assertions",
        ["preconditioncheck"] = "precondition_check",
        ["precondition_check"] = "precondition_check",
        ["finalscreenshot"] = "final_screenshot",
        ["final_screenshot"] = "final_screenshot",
        ["actioncategory"] = "action_category",
        ["action_category"] = "action_category",
    };

    private static void NormalizeObjectPropertyNames(System.Text.Json.Nodes.JsonObject obj)
    {
        // Collect property names that need renaming first to avoid modifying during enumeration
        var renames = new List<(string oldName, string newName)>();

        foreach (var prop in obj)
        {
            // Normalize the key: strip underscores and hyphens, lowercase
            var normalizedKey = prop.Key.Replace("_", "").Replace("-", "").ToLowerInvariant();
            if (s_nameMap.TryGetValue(normalizedKey, out var correctName) && prop.Key != correctName)
            {
                renames.Add((prop.Key, correctName));
            }

            // Recurse into nested objects and arrays
            if (prop.Value is System.Text.Json.Nodes.JsonObject childObj)
                NormalizeObjectPropertyNames(childObj);
            else if (prop.Value is System.Text.Json.Nodes.JsonArray arr)
                foreach (var item in arr)
                    if (item is System.Text.Json.Nodes.JsonObject arrObj)
                        NormalizeObjectPropertyNames(arrObj);
        }

        foreach (var (oldName, newName) in renames)
        {
            var value = obj[oldName];
            obj.Remove(oldName);
            obj[newName] = value;
        }
    }
}
