namespace AgentSquad.Core.Prompts;

/// <summary>
/// Parsed YAML frontmatter metadata from a prompt template.
/// Fields are advisory — agents control actual model selection and parameters.
/// </summary>
public record PromptMetadata
{
    public string? Version { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Variables { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// Parsed representation of a prompt template file (frontmatter + body).
/// </summary>
public record PromptTemplate
{
    public required PromptMetadata Metadata { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset LoadedAt { get; init; }
}
