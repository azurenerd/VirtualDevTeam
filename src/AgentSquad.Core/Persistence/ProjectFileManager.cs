using System.Globalization;
using AgentSquad.Core.Agents;
using AgentSquad.Core.GitHub;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Persistence;

/// <summary>
/// Manages the shared Markdown files in the repo: TeamMembers.md, EngineeringPlan.md, Architecture.md, Research.md, PMSpec.md.
/// </summary>
public class ProjectFileManager
{
    private readonly IGitHubService _github;
    private readonly ILogger<ProjectFileManager> _logger;
    private readonly string? _branch;
    private readonly HashSet<string> _warnedMissingAgents = new(StringComparer.OrdinalIgnoreCase);

    private const string TeamMembersPath = "TeamMembers.md";
    private const string EngineeringPlanPath = "EngineeringPlan.md";
    private const string ArchitecturePath = "Architecture.md";
    private const string ResearchPath = "Research.md";
    private const string PMSpecPath = "PMSpec.md";
    private const string TeamCompositionPath = "TeamComposition.md";

    public ProjectFileManager(
        IGitHubService github,
        ILogger<ProjectFileManager> logger,
        string? branch = null)
    {
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _branch = branch;
    }

    #region TeamMembers.md

    /// <summary>
    /// Get the full content of TeamMembers.md.
    /// </summary>
    public async Task<string> GetTeamMembersAsync(CancellationToken ct = default)
    {
        var content = await _github.GetFileContentAsync(TeamMembersPath, _branch, ct);
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

        await _github.CreateOrUpdateFileAsync(
            TeamMembersPath, updated,
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

        await _github.CreateOrUpdateFileAsync(
            TeamMembersPath,
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

        await _github.CreateOrUpdateFileAsync(
            TeamMembersPath,
            string.Join('\n', lines),
            $"Remove team member: {agentId}",
            _branch, ct);
    }

    #endregion

    #region EngineeringPlan.md

    public async Task<string> GetEngineeringPlanAsync(CancellationToken ct = default)
    {
        var content = await _github.GetFileContentAsync(EngineeringPlanPath, _branch, ct);
        return content ?? "# Engineering Plan\n\n_No engineering plan has been created yet._\n";
    }

    public async Task UpdateEngineeringPlanAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating EngineeringPlan.md");
        await _github.CreateOrUpdateFileAsync(EngineeringPlanPath, content, "Update engineering plan", _branch, ct);
    }

    #endregion

    #region Architecture.md

    public async Task<string> GetArchitectureDocAsync(CancellationToken ct = default)
    {
        var content = await _github.GetFileContentAsync(ArchitecturePath, _branch, ct);
        return content ?? "# Architecture\n\n_No architecture document has been created yet._\n";
    }

    public async Task UpdateArchitectureDocAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating Architecture.md");
        await _github.CreateOrUpdateFileAsync(ArchitecturePath, content, "Update architecture document", _branch, ct);
    }

    #endregion

    #region Research.md

    public async Task<string> GetResearchDocAsync(CancellationToken ct = default)
    {
        var content = await _github.GetFileContentAsync(ResearchPath, _branch, ct);
        return content ?? "# Research\n\n_No research has been documented yet._\n";
    }

    public async Task UpdateResearchDocAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating Research.md");
        await _github.CreateOrUpdateFileAsync(ResearchPath, content, "Update research document", _branch, ct);
    }

    #endregion

    #region PMSpec.md

    public async Task<string> GetPMSpecAsync(CancellationToken ct = default)
    {
        var content = await _github.GetFileContentAsync(PMSpecPath, _branch, ct);
        return content ?? "# PM Specification\n\n_No PM specification has been created yet._\n";
    }

    public async Task UpdatePMSpecAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating PMSpec.md");
        await _github.CreateOrUpdateFileAsync(PMSpecPath, content, "Update PM specification", _branch, ct);
    }

    #endregion

    #region Team Composition

    /// <summary>
    /// Get TeamComposition.md — the PM's analysis of team structure, specialists, and skill gaps.
    /// Returns null if not yet created (leader SE should plan without it).
    /// </summary>
    public async Task<string?> GetTeamCompositionAsync(CancellationToken ct = default)
    {
        return await _github.GetFileContentAsync(TeamCompositionPath, _branch, ct);
    }

    public async Task UpdateTeamCompositionAsync(string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        _logger.LogInformation("Updating TeamComposition.md");
        await _github.CreateOrUpdateFileAsync(TeamCompositionPath, content, "Update team composition", _branch, ct);
    }

    #endregion

    #region Generic File Operations

    /// <summary>
    /// Get any file content from the repo.
    /// </summary>
    public async Task<string?> GetFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await _github.GetFileContentAsync(path, _branch, ct);
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
        await _github.CreateOrUpdateFileAsync(path, content, commitMessage, _branch, ct);
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
