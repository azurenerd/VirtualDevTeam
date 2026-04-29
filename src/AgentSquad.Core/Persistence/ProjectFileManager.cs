using System.Globalization;
using AgentSquad.Core.Agents;
using AgentSquad.Core.DevPlatform.Capabilities;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Persistence;

/// <summary>
/// Manages the shared Markdown files in the repo: TeamMembers.md, EngineeringPlan.md, Architecture.md, Research.md, PMSpec.md.
/// Paths are resolved relative to <see cref="ArtifactBasePath"/> (e.g., "AgentDocs/101/PMSpec.md").
/// When reading, falls back to the repo root if the scoped path is not found (legacy compatibility).
/// </summary>
public class ProjectFileManager
{
    private readonly IRepositoryContentService _repoContent;
    private readonly ILogger<ProjectFileManager> _logger;
    private readonly string? _branch;
    private readonly HashSet<string> _warnedMissingAgents = new(StringComparer.OrdinalIgnoreCase);

    private const string TeamMembersFile = "TeamMembers.md";
    private const string EngineeringPlanFile = "EngineeringPlan.md";
    private const string ArchitectureFile = "Architecture.md";
    private const string ResearchFile = "Research.md";
    private const string PMSpecFile = "PMSpec.md";
    private const string TeamCompositionFile = "TeamComposition.md";

    /// <summary>
    /// Base path prefix for all agent-generated documents (e.g., "AgentDocs/101").
    /// Set by RunCoordinator when a run starts. Empty string means repo root (legacy behavior).
    /// </summary>
    public string ArtifactBasePath { get; set; } = "";

    public ProjectFileManager(
        IRepositoryContentService repoContent,
        ILogger<ProjectFileManager> logger,
        string? branch = null)
    {
        _repoContent = repoContent ?? throw new ArgumentNullException(nameof(repoContent));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _branch = branch;
    }

    /// <summary>Resolve a doc filename to its full path under <see cref="ArtifactBasePath"/>.</summary>
    private string ResolvePath(string fileName) =>
        string.IsNullOrEmpty(ArtifactBasePath) ? fileName : $"{ArtifactBasePath}/{fileName}";

    /// <summary>
    /// Read a file from the scoped path; if not found, fall back to repo root (legacy repos).
    /// </summary>
    private async Task<string?> GetFileWithFallbackAsync(string fileName, CancellationToken ct)
    {
        var scopedPath = ResolvePath(fileName);
        var content = await _repoContent.GetFileContentAsync(scopedPath, _branch, ct);

        if (content is not null || string.IsNullOrEmpty(ArtifactBasePath))
            return content;

        // Fallback: try bare filename at repo root for repos created before AgentDocs/ was introduced
        _logger.LogDebug("File not found at {ScopedPath}, falling back to root {FileName}", scopedPath, fileName);
        return await _repoContent.GetFileContentAsync(fileName, _branch, ct);
    }

    #region TeamMembers.md

    /// <summary>
    /// Get the full content of TeamMembers.md.
    /// </summary>
    public async Task<string> GetTeamMembersAsync(CancellationToken ct = default)
    {
        var content = await GetFileWithFallbackAsync(TeamMembersFile, ct);
        return content ?? CreateEmptyTeamMembersDoc();
    }

    /// <summary>
    /// Add a team member row to TeamMembers.md.
    /// </summary>
    public async Task AddTeamMemberAsync(
        AgentIdentity agent,
        string status,
        string? communicationDetails = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var content = await GetTeamMembersAsync(ct);
        var currentPr = agent.AssignedPullRequest ?? "—";
        var since = agent.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var comm = communicationDetails ?? "Internal Bus";

        var newRow = $"| {agent.DisplayName} | {agent.Role} | {status} | {agent.ModelTier} | {currentPr} | {since} | {comm} |";

        var updated = content.TrimEnd() + "\n" + newRow + "\n";

        _logger.LogInformation("Adding team member {Name} ({Role}) to TeamMembers.md", agent.DisplayName, agent.Role);

        await _repoContent.CreateOrUpdateFileAsync(
            ResolvePath(TeamMembersFile), updated,
            $"Add team member: {agent.DisplayName}",
            _branch, ct);
    }

    /// <summary>
    /// Update the status column for a team member identified by agent display name.
    /// </summary>
    public async Task UpdateTeamMemberStatusAsync(
        string agentId,
        string newStatus,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newStatus);

        var content = await GetTeamMembersAsync(ct);
        var lines = content.Split('\n');
        var updated = false;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith('|') || !lines[i].TrimEnd().EndsWith('|'))
                continue;

            var columns = ParseTableRow(lines[i]);
            if (columns.Count < 7)
                continue;

            // Match by Name column (index 0)
            if (!string.Equals(columns[0].Trim(), agentId, StringComparison.OrdinalIgnoreCase))
                continue;

            columns[2] = $" {newStatus} ";
            lines[i] = "|" + string.Join("|", columns) + "|";
            updated = true;
            break;
        }

        if (!updated)
        {
            if (_warnedMissingAgents.Add(agentId))
                _logger.LogWarning("Agent {AgentId} not found in TeamMembers.md", agentId);
            return;
        }

        _logger.LogInformation("Updating status for {AgentId} to {Status}", agentId, newStatus);

        await _repoContent.CreateOrUpdateFileAsync(
            ResolvePath(TeamMembersFile),
            string.Join('\n', lines),
            $"Update {agentId} status to {newStatus}",
            _branch, ct);
    }

    /// <summary>
    /// Remove a team member row from TeamMembers.md.
    /// </summary>
    public async Task RemoveTeamMemberAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var content = await GetTeamMembersAsync(ct);
        var lines = content.Split('\n').ToList();
        var removed = false;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!lines[i].StartsWith('|') || !lines[i].TrimEnd().EndsWith('|'))
                continue;

            var columns = ParseTableRow(lines[i]);
            if (columns.Count < 7)
                continue;

            if (string.Equals(columns[0].Trim(), agentId, StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                removed = true;
                break;
            }
        }

        if (!removed)
        {
            _logger.LogWarning("Agent {AgentId} not found in TeamMembers.md for removal", agentId);
            return;
        }

        _logger.LogInformation("Removing team member {AgentId} from TeamMembers.md", agentId);

        await _repoContent.CreateOrUpdateFileAsync(
            ResolvePath(TeamMembersFile),
            string.Join('\n', lines),
            $"Remove team member: {agentId}",
            _branch, ct);
    }

    #endregion

    #region EngineeringPlan.md

    public async Task<string> GetEngineeringPlanAsync(CancellationToken ct = default)
    {
        var content = await GetFileWithFallbackAsync(EngineeringPlanFile, ct);
        return content ?? "# Engineering Plan\n\n_No engineering plan has been created yet._\n";
    }

    public async Task UpdateEngineeringPlanAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating EngineeringPlan.md at {Path}", ResolvePath(EngineeringPlanFile));
        await _repoContent.CreateOrUpdateFileAsync(ResolvePath(EngineeringPlanFile), content, "Update engineering plan", _branch, ct);
    }

    #endregion

    #region Architecture.md

    public async Task<string> GetArchitectureDocAsync(CancellationToken ct = default)
    {
        var content = await GetFileWithFallbackAsync(ArchitectureFile, ct);
        return content ?? "# Architecture\n\n_No architecture document has been created yet._\n";
    }

    public async Task UpdateArchitectureDocAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating Architecture.md at {Path}", ResolvePath(ArchitectureFile));
        await _repoContent.CreateOrUpdateFileAsync(ResolvePath(ArchitectureFile), content, "Update architecture document", _branch, ct);
    }

    #endregion

    #region Research.md

    public async Task<string> GetResearchDocAsync(CancellationToken ct = default)
    {
        var content = await GetFileWithFallbackAsync(ResearchFile, ct);
        return content ?? "# Research\n\n_No research has been documented yet._\n";
    }

    public async Task UpdateResearchDocAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating Research.md at {Path}", ResolvePath(ResearchFile));
        await _repoContent.CreateOrUpdateFileAsync(ResolvePath(ResearchFile), content, "Update research document", _branch, ct);
    }

    #endregion

    #region PMSpec.md

    public async Task<string> GetPMSpecAsync(CancellationToken ct = default)
    {
        var content = await GetFileWithFallbackAsync(PMSpecFile, ct);
        return content ?? "# PM Specification\n\n_No PM specification has been created yet._\n";
    }

    public async Task UpdatePMSpecAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating PMSpec.md at {Path}", ResolvePath(PMSpecFile));
        await _repoContent.CreateOrUpdateFileAsync(ResolvePath(PMSpecFile), content, "Update PM specification", _branch, ct);
    }

    #endregion

    #region Team Composition

    /// <summary>
    /// Get TeamComposition.md — the PM's analysis of team structure, specialists, and skill gaps.
    /// Returns null if not yet created (leader SE should plan without it).
    /// </summary>
    public async Task<string?> GetTeamCompositionAsync(CancellationToken ct = default)
    {
        return await GetFileWithFallbackAsync(TeamCompositionFile, ct);
    }

    public async Task UpdateTeamCompositionAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating TeamComposition.md at {Path}", ResolvePath(TeamCompositionFile));
        await _repoContent.CreateOrUpdateFileAsync(ResolvePath(TeamCompositionFile), content, "Update team composition", _branch, ct);
    }

    #endregion

    #region Generic File Operations

    /// <summary>
    /// Get any file content from the repo.
    /// </summary>
    public async Task<string?> GetFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await _repoContent.GetFileContentAsync(path, _branch, ct);
    }

    /// <summary>
    /// Save any file to the repo with a commit message.
    /// </summary>
    public async Task SaveFileAsync(string path, string content, string commitMessage, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitMessage);

        _logger.LogInformation("Saving file {Path}", path);
        await _repoContent.CreateOrUpdateFileAsync(path, content, commitMessage, _branch, ct);
    }

    #endregion

    #region Helpers

    private static string CreateEmptyTeamMembersDoc()
    {
        return """
            # Team Members

            | Name | Role | Status | Model Tier | Current PR | Since | Communication |
            |------|------|--------|------------|------------|-------|---------------|
            """;
    }

    /// <summary>
    /// Parse a Markdown table row into its column values (excluding outer pipes).
    /// </summary>
    private static List<string> ParseTableRow(string row)
    {
        var trimmed = row.Trim();

        if (trimmed.StartsWith('|'))
            trimmed = trimmed[1..];
        if (trimmed.EndsWith('|'))
            trimmed = trimmed[..^1];

        return [.. trimmed.Split('|')];
    }

    #endregion
}
