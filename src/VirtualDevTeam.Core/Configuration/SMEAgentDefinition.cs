namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Defines a dynamically-created SME (Subject Matter Expert) agent role.
/// Reusable across runs and persistable to JSON.
/// </summary>
public record SMEAgentDefinition
{
    /// <summary>Unique identifier for this definition (reusable across runs)</summary>
    public required string DefinitionId { get; init; }

    /// <summary>Human-readable role name (e.g., "Security Auditor", "API Specialist")</summary>
    public required string RoleName { get; init; }

    /// <summary>Detailed system prompt defining the agent's expertise and behavior</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>MCP server names from the registry that this agent needs</summary>
    public List<string> McpServers { get; init; } = [];

    /// <summary>External URLs to fetch and digest as domain knowledge</summary>
    public List<string> KnowledgeLinks { get; init; } = [];

    /// <summary>Model tier override (premium/standard/budget) — defaults to standard</summary>
    public string ModelTier { get; init; } = "standard";

    /// <summary>What kinds of tasks this agent can handle</summary>
    public List<string> Capabilities { get; init; } = [];

    /// <summary>Maximum concurrent instances of this SME type</summary>
    public int MaxInstances { get; init; } = 1;

    /// <summary>How the agent participates in the workflow</summary>
    public SmeWorkflowMode WorkflowMode { get; init; } = SmeWorkflowMode.OnDemand;

    /// <summary>Message types this agent should subscribe to</summary>
    public List<string> SubscribeTo { get; init; } = [];

    /// <summary>
    /// Which base agent template to use when spawning this SME.
    /// "engineer" → SpecialistEngineerAgent (full rework/build/test capabilities).
    /// "custom" → SmeAgent (lightweight, no engineering loop). Default for backward compat.
    /// </summary>
    public string BaseTemplate { get; init; } = "custom";

    /// <summary>Agent that created this definition (for audit/lineage)</summary>
    public string? CreatedByAgentId { get; init; }

    /// <summary>When this definition was created</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The display name assigned when this agent was first spawned.
    /// Persisted so that restarts produce the same name (critical for PR/issue recovery).
    /// </summary>
    public string? SpawnedDisplayName { get; set; }
}

/// <summary>
/// How an SME agent participates in the workflow.
/// </summary>
public enum SmeWorkflowMode
{
    /// <summary>Agent is spawned on-demand when a task matches its capabilities</summary>
    OnDemand,

    /// <summary>Agent runs continuously in a polling loop (like existing agents)</summary>
    Continuous,

    /// <summary>Agent runs once for a specific task then shuts down</summary>
    OneShot
}
