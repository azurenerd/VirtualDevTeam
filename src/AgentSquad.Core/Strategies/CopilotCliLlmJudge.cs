using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using AgentSquad.Core.Configuration;

namespace AgentSquad.Core.Strategies;

/// <summary>
/// LLM judge that uses the ModelRegistry (CopilotCli or API provider) to score
/// candidate patches on Acceptance Criteria, Design quality, and Readability.
/// Replaces <see cref="NullLlmJudge"/> to enable quality-based winner selection.
/// </summary>
public sealed class CopilotCliLlmJudge : ILlmJudge
{
    private readonly ModelRegistry _modelRegistry;
    private readonly IOptionsMonitor<StrategyFrameworkConfig> _cfg;
    private readonly ILogger<CopilotCliLlmJudge> _logger;

    public CopilotCliLlmJudge(
        ModelRegistry modelRegistry,
        IOptionsMonitor<StrategyFrameworkConfig> cfg,
        ILogger<CopilotCliLlmJudge> logger)
    {
        _modelRegistry = modelRegistry;
        _cfg = cfg;
        _logger = logger;
    }

    public async Task<JudgeResult> ScoreAsync(JudgeInput input, CancellationToken ct)
    {
        if (input.CandidatePatches.Count == 0)
            return new JudgeResult { Scores = new Dictionary<string, CandidateScore>() };

        try
        {
            var tier = _cfg.CurrentValue.Evaluator.JudgeModelTier;
            var kernel = _modelRegistry.GetKernel(tier, agentId: "strategy-judge");
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var prompt = BuildPrompt(input);
            var history = new ChatHistory();
            history.AddUserMessage(prompt);

            var response = await chat.GetChatMessageContentsAsync(history, cancellationToken: ct);
            var responseText = string.Join("", response.Select(m => m.Content ?? ""));

            var scores = ParseScores(responseText, input.CandidatePatches.Keys.ToList());

            _logger.LogInformation(
                "LLM judge scored {Count} candidates for task {TaskId}: {Summary}",
                scores.Count, input.TaskId,
                string.Join(", ", scores.Select(kv => $"{kv.Key}=AC:{kv.Value.AcceptanceCriteriaScore}/D:{kv.Value.DesignScore}/R:{kv.Value.ReadabilityScore}")));

            return new JudgeResult { Scores = scores };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM judge failed for task {TaskId}, falling back to tiebreakers", input.TaskId);
            return new JudgeResult
            {
                Scores = new Dictionary<string, CandidateScore>(),
                Error = ex.Message
            };
        }
    }

    private static string BuildPrompt(JudgeInput input)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are an expert code judge. Score each candidate implementation on three dimensions (0-10 each):");
        sb.AppendLine();
        sb.AppendLine("1. **AcceptanceCriteria** (0-10): Does the code fulfill the task requirements? Does it produce a working application?");
        sb.AppendLine("   - 0: No meaningful implementation");
        sb.AppendLine("   - 5: Partial implementation, missing key features");
        sb.AppendLine("   - 8: Fully implements requirements with minor gaps");
        sb.AppendLine("   - 10: Perfect implementation of all requirements");
        sb.AppendLine();
        sb.AppendLine("2. **Design** (0-10): Code architecture, file structure, separation of concerns, naming conventions.");
        sb.AppendLine("   - 0: Spaghetti code, everything in one file");
        sb.AppendLine("   - 5: Reasonable structure but some issues");
        sb.AppendLine("   - 10: Clean architecture, well-organized, follows best practices");
        sb.AppendLine();
        sb.AppendLine("3. **Readability** (0-10): Code clarity, comments where needed, consistent style.");
        sb.AppendLine("   - 0: Unreadable, no structure");
        sb.AppendLine("   - 5: Readable but inconsistent");
        sb.AppendLine("   - 10: Crystal clear, well-documented");
        sb.AppendLine();
        sb.AppendLine("CRITICAL SCORING RULES:");
        sb.AppendLine("- If a web app references external data files (like data.json) but does NOT include them in the patch, score AcceptanceCriteria ≤ 3 (the app will crash at runtime).");
        sb.AppendLine("- If a web app gitignores required data files, score AcceptanceCriteria ≤ 2.");
        sb.AppendLine("- If the implementation is complete and self-contained (all files needed to run are present), score AcceptanceCriteria ≥ 7.");
        sb.AppendLine();
        sb.AppendLine($"## Task: {input.TaskTitle}");
        sb.AppendLine();
        sb.AppendLine(input.TaskDescription);
        sb.AppendLine();
        sb.AppendLine("## Candidates");
        sb.AppendLine();

        foreach (var (id, patch) in input.CandidatePatches)
        {
            sb.AppendLine($"### Candidate: {id}");
            sb.AppendLine("```diff");
            sb.AppendLine(patch);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## Response Format");
        sb.AppendLine("Respond ONLY with valid JSON (no markdown fences, no explanation outside JSON):");
        sb.AppendLine("```");
        sb.AppendLine("{");
        sb.AppendLine("  \"scores\": {");
        sb.AppendLine("    \"candidate-id\": { \"ac\": 8, \"design\": 7, \"readability\": 9, \"reasoning\": \"Brief explanation\" }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("```");

        return sb.ToString();
    }

    private Dictionary<string, CandidateScore> ParseScores(string response, List<string> candidateIds)
    {
        var scores = new Dictionary<string, CandidateScore>();

        // Strip markdown code fences if present
        var json = response.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline > 0) json = json[(firstNewline + 1)..];
            var lastFence = json.LastIndexOf("```");
            if (lastFence > 0) json = json[..lastFence];
            json = json.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement scoresElement;
            if (root.TryGetProperty("scores", out scoresElement))
            {
                foreach (var candidateId in candidateIds)
                {
                    if (scoresElement.TryGetProperty(candidateId, out var candidate))
                    {
                        scores[candidateId] = new CandidateScore
                        {
                            AcceptanceCriteriaScore = GetInt(candidate, "ac", "AcceptanceCriteria"),
                            DesignScore = GetInt(candidate, "design", "Design"),
                            ReadabilityScore = GetInt(candidate, "readability", "Readability"),
                            Reasoning = GetString(candidate, "reasoning", "Reasoning") ?? ""
                        };
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Failed to parse judge JSON response: {Error}. Response: {Response}",
                ex.Message, json.Length > 500 ? json[..500] : json);
        }

        // If parsing failed for some candidates, give them neutral scores
        foreach (var id in candidateIds)
        {
            if (!scores.ContainsKey(id))
            {
                scores[id] = new CandidateScore
                {
                    AcceptanceCriteriaScore = 5,
                    DesignScore = 5,
                    ReadabilityScore = 5,
                    Reasoning = "Judge could not parse score for this candidate"
                };
            }
        }

        return scores;
    }

    private static int GetInt(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
                return Math.Clamp(prop.GetInt32(), 0, 10);
        }
        return 5;
    }

    private static string? GetString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }
}
