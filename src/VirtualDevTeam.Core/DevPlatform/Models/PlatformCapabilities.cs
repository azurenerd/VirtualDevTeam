namespace VirtualDevTeam.Core.DevPlatform.Models;

/// <summary>
/// Describes what features the current platform provider supports.
/// Consumers can check capabilities before calling optional operations.
/// </summary>
public record PlatformCapabilities
{
    /// <summary>GitHub sub-issues, ADO parent-child links.</summary>
    public bool SupportsWorkItemHierarchy { get; init; }

    /// <summary>GitHub issue dependencies, ADO predecessor links.</summary>
    public bool SupportsWorkItemDependencies { get; init; }

    /// <summary>GitHub supports GraphQL deletion. ADO can only close/remove.</summary>
    public bool SupportsWorkItemDeletion { get; init; }

    /// <summary>Both GitHub and ADO support inline review comments.</summary>
    public bool SupportsInlineReviewComments { get; init; }

    /// <summary>GitHub: labels on issues. ADO: tags on work items.</summary>
    public bool SupportsLabelsOnWorkItems { get; init; }

    /// <summary>Both platforms support labels on PRs.</summary>
    public bool SupportsLabelsOnPullRequests { get; init; }

    /// <summary>GitHub: ["Issue"]. ADO: ["Task","Bug","User Story","Epic",...].</summary>
    public IReadOnlyList<string> SupportedWorkItemTypes { get; init; } = ["Issue"];

    /// <summary>Whether the platform supports commit-tree based repo reset.</summary>
    public bool SupportsAtomicTreeReset { get; init; }

    /// <summary>
    /// GitHub capabilities (default).
    /// </summary>
    public static PlatformCapabilities GitHub => new()
    {
        SupportsWorkItemHierarchy = true,
        SupportsWorkItemDependencies = true,
        SupportsWorkItemDeletion = true,
        SupportsInlineReviewComments = true,
        SupportsLabelsOnWorkItems = true,
        SupportsLabelsOnPullRequests = true,
        SupportedWorkItemTypes = ["Issue"],
        SupportsAtomicTreeReset = true
    };

    /// <summary>
    /// Azure DevOps capabilities.
    /// </summary>
    public static PlatformCapabilities AzureDevOps => new()
    {
        SupportsWorkItemHierarchy = true,
        SupportsWorkItemDependencies = true,
        SupportsWorkItemDeletion = false,
        SupportsInlineReviewComments = true,
        SupportsLabelsOnWorkItems = true,
        SupportsLabelsOnPullRequests = true,
        SupportedWorkItemTypes = ["Task", "Bug", "User Story", "Epic", "Feature"],
        SupportsAtomicTreeReset = false
    };
}
