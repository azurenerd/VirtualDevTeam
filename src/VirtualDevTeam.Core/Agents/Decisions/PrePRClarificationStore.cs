using System.Collections.Concurrent;

namespace VirtualDevTeam.Core.Agents.Decisions;

/// <summary>
/// In-memory store for pre-PR clarification question sets.
/// Agents write pending question sets; Dashboard reads and finalizes them.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public class PrePRClarificationStore
{
    private readonly ConcurrentDictionary<string, PrePRClarificationSet> _sets = new();

    /// <summary>Fired when a new set is added or an existing set is finalized.</summary>
    public event Action? OnChange;

    /// <summary>Add a new pending question set. Returns the set ID.</summary>
    public string Add(PrePRClarificationSet set)
    {
        _sets[set.Id] = set;
        OnChange?.Invoke();
        return set.Id;
    }

    /// <summary>Get a specific question set by ID.</summary>
    public PrePRClarificationSet? Get(string id) =>
        _sets.TryGetValue(id, out var set) ? set : null;

    /// <summary>Get all pending (unfinalized) question sets.</summary>
    public IReadOnlyList<PrePRClarificationSet> GetPending() =>
        _sets.Values.Where(s => !s.IsFinalized).OrderByDescending(s => s.CreatedAt).ToList();

    /// <summary>Get all finalized question sets.</summary>
    public IReadOnlyList<PrePRClarificationSet> GetFinalized() =>
        _sets.Values.Where(s => s.IsFinalized).OrderByDescending(s => s.FinalizedAt).ToList();

    /// <summary>Get pending set for a specific agent + issue combination.</summary>
    public PrePRClarificationSet? GetPendingForIssue(string agentId, int issueNumber) =>
        _sets.Values.FirstOrDefault(s => !s.IsFinalized && s.AgentId == agentId && s.IssueNumber == issueNumber);

    /// <summary>
    /// Finalize a question set with the given answers. Call after human approval.
    /// Questions without explicit FinalAnswer will use their ProposedAnswer.
    /// </summary>
    public void Finalize(string id, List<string>? editedAnswers = null)
    {
        if (!_sets.TryGetValue(id, out var set)) return;

        if (editedAnswers is not null)
        {
            for (var i = 0; i < set.Questions.Count && i < editedAnswers.Count; i++)
            {
                set.Questions[i].FinalAnswer = editedAnswers[i];
            }
        }

        // Fill any null FinalAnswers with ProposedAnswer
        foreach (var q in set.Questions.Where(q => q.FinalAnswer is null))
        {
            q.FinalAnswer = q.ProposedAnswer;
        }

        set.IsFinalized = true;
        set.FinalizedAt = DateTime.UtcNow;
        OnChange?.Invoke();
    }

    /// <summary>Auto-approve a question set (gate disabled — use AI answers directly).</summary>
    public void AutoApprove(string id)
    {
        if (!_sets.TryGetValue(id, out var set)) return;

        foreach (var q in set.Questions)
        {
            q.FinalAnswer = q.ProposedAnswer;
        }

        set.IsFinalized = true;
        set.FinalizedAt = DateTime.UtcNow;
        set.WasAutoApproved = true;
        OnChange?.Invoke();
    }

    /// <summary>Get the count of pending question sets.</summary>
    public int PendingCount => _sets.Values.Count(s => !s.IsFinalized);
}
