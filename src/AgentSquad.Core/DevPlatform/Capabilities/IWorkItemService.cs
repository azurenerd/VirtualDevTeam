using AgentSquad.Core.DevPlatform.Models;

namespace AgentSquad.Core.DevPlatform.Capabilities;

/// <summary>
/// Work item operations. Maps to GitHub Issues or Azure DevOps Work Items (Task/Bug/Story).
/// </summary>
public interface IWorkItemService
{
    Task<PlatformWorkItem> CreateAsync(
        string title, string body, IReadOnlyList<string> labels,
        CancellationToken ct = default);

    Task<PlatformWorkItem?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformWorkItem>> ListOpenAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlatformWorkItem>> ListAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlatformWorkItem>> ListForAgentAsync(string agentName, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformWorkItem>> ListByLabelAsync(string label, string? state = null, CancellationToken ct = default);

    Task UpdateAsync(
        int id, string? title = null, string? body = null,
        IReadOnlyList<string>? labels = null, string? state = null,
        CancellationToken ct = default);

    Task UpdateTitleAsync(int id, string newTitle, CancellationToken ct = default);
    Task CloseAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Permanently delete a work item. GitHub: GraphQL deletion. ADO: DELETE with destroy=true (falls back to close).
    /// Returns true if deleted/closed successfully.
    /// </summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    Task AddCommentAsync(int id, string comment, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformComment>> GetCommentsAsync(int id, CancellationToken ct = default);

    // Hierarchy
    /// <summary>Add a work item as a child of another. GitHub: sub-issues. ADO: parent-child link.</summary>
    Task<bool> AddChildAsync(int parentId, long childPlatformId, CancellationToken ct = default);

    /// <summary>Get child work items of a parent.</summary>
    Task<IReadOnlyList<PlatformWorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default);

    // Dependencies
    /// <summary>Add a "blocked by" dependency. GitHub: issue dependencies. ADO: predecessor link.</summary>
    Task<bool> AddDependencyAsync(int blockedId, long blockingPlatformId, CancellationToken ct = default);
}
