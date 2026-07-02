using System.Text.Json;
using VirtualDevTeam.Agents.AI;
using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VirtualDevTeam.Agents.Tests;

/// <summary>
/// LlmJudge unit tests. Use the internal chat-factory seam to bypass real ModelRegistry/Kernel
/// construction. Validates the contract guarantees in <see cref="ILlmJudge"/>:
///  - Never throws on malformed LLM output (returns empty Scores + Error).
///  - Schema-invalid output (null scores, missing/duplicate ids, OOB scores) handled deterministically.
///  - Chat-resolve and chat-invoke failures fail closed (empty Scores + Error).
///  - Cancellation propagates as OCE.
/// </summary>
public class LlmJudgeTests
{
    private const string Tier = "standard";

    private static (LlmJudge judge, FakeChat chat) BuildJudge(string responseText)
    {
        var chat = new FakeChat(responseText);
        return BuildJudge(chat);
    }

    private static (LlmJudge judge, FakeChat chat) BuildJudge(FakeChat chat)
    {
        // Empty Models dict is fine — GetModelConfig returns null, judge uses caller's MaxPatchChars.
        var registry = new ModelRegistry(
            new VirtualDevTeamConfig { Models = new Dictionary<string, ModelConfig>() },
            NullLoggerFactory.Instance,
            new AgentUsageTracker(),
            new ActiveLlmCallTracker());

        var stratCfg = new TestStratCfg(new StrategyFrameworkConfig());
        var judge = new LlmJudge(
            registry,
            stratCfg,
            NullLogger<LlmJudge>.Instance,
            chatFactoryOverride: (_, _) => chat);
        return (judge, chat);
    }

    private static JudgeInput Input(params string[] candidateIds)
    {
        var dict = candidateIds.ToDictionary(id => id, id => $"diff for {id}\n+ a line\n");
        return new JudgeInput
        {
            TaskId = "task-1",
            TaskTitle = "Add login",
            TaskDescription = "Implement login form",
            CandidatePatches = dict,
        };
    }

    [Fact]
    public async Task Returns_scores_for_well_formed_json()
    {
        var json = """
        {"scores":[
          {"candidateId":"baseline","ac":7,"design":8,"readability":9},
          {"candidateId":"mcp","ac":10,"design":6,"readability":5}
        ]}
        """;
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline", "mcp"), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.False(result.IsFallback);
        Assert.Equal(2, result.Scores.Count);
        Assert.Equal(7, result.Scores["baseline"].AcceptanceCriteriaScore);
        Assert.Equal(8, result.Scores["baseline"].DesignScore);
        Assert.Equal(9, result.Scores["baseline"].ReadabilityScore);
        Assert.Equal(10, result.Scores["mcp"].AcceptanceCriteriaScore);
    }

    [Fact]
    public async Task Strips_markdown_code_fences_around_json()
    {
        var json = "```json\n{\"scores\":[{\"candidateId\":\"baseline\",\"ac\":5,\"design\":5,\"readability\":5}]}\n```";
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Single(result.Scores);
    }

    [Fact]
    public async Task Malformed_json_returns_empty_scores_with_error_no_throw()
    {
        var (judge, _) = BuildJudge("this is not json at all { ohno");
        var result = await judge.ScoreAsync(Input("baseline", "mcp"), CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal("invalid-json", result.Error);
        Assert.Empty(result.Scores);
    }

    [Fact]
    public async Task Schema_invalid_null_scores_field_returns_invalid_schema()
    {
        var (judge, _) = BuildJudge("""{"scores":null}""");
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal("invalid-schema", result.Error);
    }

    [Fact]
    public async Task Schema_invalid_empty_object_returns_invalid_schema()
    {
        var (judge, _) = BuildJudge("{}");
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal("invalid-schema", result.Error);
    }

    [Fact]
    public async Task Unknown_candidate_ids_silently_dropped()
    {
        var json = """
        {"scores":[
          {"candidateId":"baseline","ac":5,"design":5,"readability":5},
          {"candidateId":"ghost-strategy","ac":10,"design":10,"readability":10}
        ]}
        """;
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Single(result.Scores);
        Assert.True(result.Scores.ContainsKey("baseline"));
        Assert.False(result.Scores.ContainsKey("ghost-strategy"));
    }

    [Fact]
    public async Task Out_of_range_scores_are_clamped()
    {
        var json = """
        {"scores":[
          {"candidateId":"baseline","ac":-3,"design":99,"readability":7}
        ]}
        """;
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.Equal(0, result.Scores["baseline"].AcceptanceCriteriaScore);
        Assert.Equal(10, result.Scores["baseline"].DesignScore);
        Assert.Equal(7, result.Scores["baseline"].ReadabilityScore);
    }

    [Fact]
    public async Task Duplicate_candidate_ids_first_wins()
    {
        var json = """
        {"scores":[
          {"candidateId":"baseline","ac":3,"design":3,"readability":3},
          {"candidateId":"baseline","ac":9,"design":9,"readability":9}
        ]}
        """;
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.Single(result.Scores);
        Assert.Equal(3, result.Scores["baseline"].AcceptanceCriteriaScore);
    }

    [Fact]
    public async Task Missing_score_fields_default_to_zero()
    {
        var json = """{"scores":[{"candidateId":"baseline"}]}""";
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.Equal(0, result.Scores["baseline"].AcceptanceCriteriaScore);
        Assert.Equal(0, result.Scores["baseline"].DesignScore);
        Assert.Equal(0, result.Scores["baseline"].ReadabilityScore);
    }

    [Fact]
    public async Task Empty_response_returns_error_no_throw()
    {
        var (judge, _) = BuildJudge("   ");
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.Equal("empty-response", result.Error);
    }

    [Fact]
    public async Task Chat_throws_returns_error_no_throw()
    {
        var chat = new FakeChat("") { ThrowOnCall = new InvalidOperationException("boom") };
        var (judge, _) = BuildJudge(chat);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.True(result.IsFallback);
        Assert.StartsWith("chat-exception:", result.Error);
    }

    [Fact]
    public async Task Cancellation_propagates_as_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var chat = new FakeChat("") { HonorCancellation = true };
        var (judge, _) = BuildJudge(chat);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => judge.ScoreAsync(Input("baseline"), cts.Token));
    }

    [Fact]
    public async Task Empty_candidate_set_returns_no_candidates_error_no_chat()
    {
        var chat = new FakeChat("UNREACHED");
        var (judge, _) = BuildJudge(chat);
        var input = new JudgeInput
        {
            TaskId = "t",
            TaskTitle = "x",
            TaskDescription = "y",
            CandidatePatches = new Dictionary<string, string>(),
        };
        var result = await judge.ScoreAsync(input, CancellationToken.None);

        Assert.Equal("no-candidates", result.Error);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task Sanitizes_patches_before_submission()
    {
        var chat = new FakeChat("""{"scores":[{"candidateId":"baseline","ac":5,"design":5,"readability":5}]}""");
        var (judge, _) = BuildJudge(chat);
        var input = new JudgeInput
        {
            TaskId = "t",
            TaskTitle = "x",
            TaskDescription = "y",
            CandidatePatches = new Dictionary<string, string>
            {
                // Embed a control character — sanitizer should strip it before submission.
                ["baseline"] = "diff line\u0001injected\n",
            },
        };
        await judge.ScoreAsync(input, CancellationToken.None);

        Assert.Single(chat.LastUserPrompts);
        var prompt = chat.LastUserPrompts[0];
        Assert.DoesNotContain('\u0001', prompt);
    }

    [Fact]
    public async Task Result_is_consumable_by_evaluator_ranking_path()
    {
        // Sanity: scores returned with distinct AC ranks should sort correctly in the
        // evaluator's OrderByDescending chain (same shape as CandidateEvaluator uses).
        var json = """
        {"scores":[
          {"candidateId":"a","ac":3,"design":9,"readability":9},
          {"candidateId":"b","ac":9,"design":1,"readability":1}
        ]}
        """;
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("a", "b"), CancellationToken.None);

        var winner = new[] { "a", "b" }
            .OrderByDescending(id => result.Scores[id].AcceptanceCriteriaScore)
            .ThenByDescending(id => result.Scores[id].DesignScore)
            .First();
        Assert.Equal("b", winner);
    }

    // ---- Per-dimension feedback tests ------------------------------------------

    [Fact]
    public async Task Per_dimension_feedback_extracted_from_json()
    {
        var json = """
        {"scores":[{
          "candidateId":"baseline",
          "ac":7,"design":8,"readability":9,
          "feedback":"Overall solid implementation",
          "ac_feedback":"Covers most acceptance criteria but misses edge case",
          "design_feedback":"Clean separation of concerns",
          "readability_feedback":"Excellent naming and comments"
        }]}
        """;
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        Assert.Null(result.Error);
        var s = result.Scores["baseline"];
        Assert.Equal("Overall solid implementation", s.Feedback);
        Assert.Equal("Covers most acceptance criteria but misses edge case", s.AcFeedback);
        Assert.Equal("Clean separation of concerns", s.DesignFeedback);
        Assert.Equal("Excellent naming and comments", s.ReadabilityFeedback);
    }

    [Fact]
    public async Task Missing_per_dimension_feedback_synthesized_for_low_scores()
    {
        // LLM omits per-dimension feedback — judge should synthesize based on score
        var json = """{"scores":[{"candidateId":"baseline","ac":4,"design":5,"readability":3}]}""";
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        var s = result.Scores["baseline"];
        // Synthesized feedback should mention the score and indicate improvement needed
        Assert.Contains("4", s.AcFeedback);
        Assert.Contains("5", s.DesignFeedback);
        Assert.Contains("3", s.ReadabilityFeedback);
    }

    [Fact]
    public async Task Missing_per_dimension_feedback_synthesized_for_high_scores()
    {
        var json = """{"scores":[{"candidateId":"baseline","ac":9,"design":10,"readability":8}]}""";
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        var s = result.Scores["baseline"];
        // Synthesized feedback for high scores should be positive
        Assert.Contains("9", s.AcFeedback);
        Assert.Contains("10", s.DesignFeedback);
        Assert.Contains("8", s.ReadabilityFeedback);
    }

    [Fact]
    public async Task Overall_feedback_synthesized_when_omitted_for_low_scores()
    {
        var json = """{"scores":[{"candidateId":"baseline","ac":5,"design":3,"readability":7}]}""";
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        var s = result.Scores["baseline"];
        // LLM omitted overall feedback — should be synthesized mentioning below-threshold axes
        Assert.NotNull(s.Feedback);
        Assert.NotEmpty(s.Feedback);
        Assert.Contains("ac=5", s.Feedback);
        Assert.Contains("design=3", s.Feedback);
    }

    [Fact]
    public async Task Overall_feedback_synthesized_when_omitted_for_high_scores()
    {
        var json = """{"scores":[{"candidateId":"baseline","ac":9,"design":9,"readability":9}]}""";
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        var s = result.Scores["baseline"];
        // LLM omitted overall feedback with all high scores — should get praise
        Assert.NotNull(s.Feedback);
        Assert.Contains("ac=9", s.Feedback);
    }

    [Fact]
    public async Task Per_dimension_feedback_with_special_characters_preserved()
    {
        var json = """
        {"scores":[{
          "candidateId":"baseline","ac":7,"design":8,"readability":6,
          "feedback":"Good work overall",
          "ac_feedback":"Uses fetch('/api/data') but doesn't handle 404 errors",
          "design_feedback":"The \"Builder\" pattern is well-applied",
          "readability_feedback":"Comments need improvement\nSome methods lack docs"
        }]}
        """;
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline"), CancellationToken.None);

        var s = result.Scores["baseline"];
        Assert.Contains("/api/data", s.AcFeedback);
        Assert.Contains("Builder", s.DesignFeedback);
        Assert.Contains("Comments need improvement", s.ReadabilityFeedback);
    }

    [Fact]
    public async Task Multiple_candidates_each_get_independent_feedback()
    {
        var json = """
        {"scores":[
          {"candidateId":"baseline","ac":5,"design":6,"readability":7,
           "feedback":"Baseline needs work","ac_feedback":"Missing login flow","design_feedback":"Monolithic","readability_feedback":"OK naming"},
          {"candidateId":"mcp","ac":9,"design":8,"readability":9,
           "feedback":"MCP is excellent","ac_feedback":"All AC met","design_feedback":"Clean modules","readability_feedback":"Very clear"}
        ]}
        """;
        var (judge, _) = BuildJudge(json);
        var result = await judge.ScoreAsync(Input("baseline", "mcp"), CancellationToken.None);

        Assert.Equal("Missing login flow", result.Scores["baseline"].AcFeedback);
        Assert.Equal("All AC met", result.Scores["mcp"].AcFeedback);
        Assert.Equal("Monolithic", result.Scores["baseline"].DesignFeedback);
        Assert.Equal("Clean modules", result.Scores["mcp"].DesignFeedback);
    }

    // ---- Test doubles ----------------------------------------------------------

    private sealed class TestStratCfg : IOptionsMonitor<StrategyFrameworkConfig>
    {
        public TestStratCfg(StrategyFrameworkConfig cfg) { CurrentValue = cfg; }
        public StrategyFrameworkConfig CurrentValue { get; }
        public StrategyFrameworkConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<StrategyFrameworkConfig, string?> listener) => null;
    }

    private sealed class FakeChat : IChatCompletionService
    {
        private readonly string _response;
        public int CallCount;
        public List<string> LastUserPrompts { get; } = new();
        public Exception? ThrowOnCall { get; set; }
        public bool HonorCancellation { get; set; }

        public FakeChat(string response) { _response = response; }

        public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            if (HonorCancellation) cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            foreach (var m in chatHistory)
            {
                if (m.Role == AuthorRole.User) LastUserPrompts.Add(m.Content ?? "");
            }
            if (ThrowOnCall is not null) throw ThrowOnCall;
            IReadOnlyList<ChatMessageContent> r = new[] { new ChatMessageContent(AuthorRole.Assistant, _response) };
            return Task.FromResult(r);
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
