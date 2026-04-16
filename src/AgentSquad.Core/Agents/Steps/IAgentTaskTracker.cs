namespace AgentSquad.Core.Agents.Steps;

/// <summary>
/// Tracks agent task execution at the step level. Agents call BeginStep/CompleteStep
/// as they progress through their workflow, and the dashboard queries for real-time visibility.
/// </summary>
public interface IAgentTaskTracker
{
    /// <summary>Begin a new step for the given agent and task. Returns the step ID.</summary>
    string BeginStep(string agentId, string taskId, string stepName, string? description = null, string? modelTier = null);

    /// <summary>Record a sub-step within an active step (e.g., a single turn in a multi-turn conversation).</summary>
    void RecordSubStep(string stepId, string description, TimeSpan? duration = null, decimal cost = 0);

    /// <summary>Mark a step as completed successfully.</summary>
    void CompleteStep(string stepId, AgentTaskStepStatus status = AgentTaskStepStatus.Completed);

    /// <summary>Mark a step as failed with a reason.</summary>
    void FailStep(string stepId, string reason);

    /// <summary>Mark a step as waiting on human intervention (e.g., decision gate).</summary>
    void SetStepWaiting(string stepId);

    /// <summary>Increment the LLM call count and cost for a step.</summary>
    void RecordLlmCall(string stepId, decimal cost = 0);

    /// <summary>Get all steps for a specific agent, ordered by step index.</summary>
    IReadOnlyList<AgentTaskStep> GetSteps(string agentId);

    /// <summary>Get the currently active (in-progress) steps across all agents.</summary>
    IReadOnlyList<AgentTaskStep> GetActiveSteps();

    /// <summary>Get the current step for a specific agent (most recent in-progress step).</summary>
    AgentTaskStep? GetCurrentStep(string agentId);

    /// <summary>Get all steps for a specific task.</summary>
    IReadOnlyList<AgentTaskStep> GetTaskSteps(string agentId, string taskId);

    /// <summary>Get the total count of steps (completed + in-progress + pending) for an agent.</summary>
    (int completed, int total) GetProgress(string agentId);

    /// <summary>Fired when any step changes state. Subscribe for real-time dashboard updates.</summary>
    event Action<AgentTaskStep>? OnStepChanged;
}
