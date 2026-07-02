namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Configuration section for the SME (Subject Matter Expert) agent system.
/// Controls dynamic agent creation, template management, and resource limits.
/// </summary>
public class SmeAgentsConfig
{
    /// <summary>Master switch for the SME agent system</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum total SME agents that can be running simultaneously</summary>
    public int MaxTotalSmeAgents { get; set; } = 5;

    /// <summary>Whether PM/PE agents can create new SME definitions via AI</summary>
    public bool AllowAgentCreatedDefinitions { get; set; } = true;

    /// <summary>Whether to persist SME definitions to disk for reuse across runs</summary>
    public bool PersistDefinitions { get; set; } = true;

    /// <summary>Path to the JSON file for persisted SME definitions</summary>
    public string DefinitionsPath { get; set; } = "sme-definitions.json";

    /// <summary>
    /// Predefined SME agent templates keyed by template ID.
    /// These are ready-to-use definitions that PM/PE can activate.
    /// </summary>
    public Dictionary<string, SMEAgentDefinition> Templates { get; set; } = new();
}
