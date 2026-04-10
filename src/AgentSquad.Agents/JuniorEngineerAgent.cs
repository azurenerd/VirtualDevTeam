using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.GitHub.Models;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Agents;

/// <summary>
/// Junior Engineer agent — handles low-complexity tasks with budget/local models.
/// Truncates context to fit smaller model windows and can escalate complexity.
/// Inherits all shared behavior from <see cref="EngineerAgentBase"/>.
/// </summary>
public class JuniorEngineerAgent : EngineerAgentBase
{
    public JuniorEngineerAgent(
        AgentIdentity identity,
        IMessageBus messageBus,
        IGitHubService github,
        PullRequestWorkflow prWorkflow,
        IssueWorkflow issueWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentStateStore stateStore,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        ILogger<JuniorEngineerAgent> logger,
        BuildRunner? buildRunner = null,
        TestRunner? testRunner = null,
        Core.Metrics.BuildTestMetrics? metrics = null,
        PlaywrightRunner? playwrightRunner = null)
        : base(identity, messageBus, github, prWorkflow, issueWorkflow,
               projectFiles, modelRegistry, stateStore, config.Value, memoryStore, logger,
               buildRunner, testRunner, metrics, playwrightRunner)
    {
    }

    protected override string GetRoleDisplayName() => "Junior Engineer";

    protected override string GetImplementationSystemPrompt(string techStack) =>
        $"You are a Junior Engineer implementing a task from a GitHub Issue. " +
        $"The project uses {techStack}. " +
        "Follow the architecture closely. Write clean, well-commented code. " +
        "Include proper error handling and basic unit tests. " +
        "If something is too complex, say so clearly.";

    /// <summary>Truncate PMSpec for budget model context windows.</summary>
    protected override async Task<string> GetPMSpecForContextAsync(CancellationToken ct)
        => TruncateForContext(await ProjectFiles.GetPMSpecAsync(ct));

    /// <summary>Truncate Architecture doc for budget model context windows.</summary>
    protected override async Task<string> GetArchitectureForContextAsync(CancellationToken ct)
        => TruncateForContext(await ProjectFiles.GetArchitectureDocAsync(ct));

    /// <summary>
    /// After legacy PR implementation, check if the task is too complex and escalate if needed.
    /// </summary>
    protected override async Task WorkOnExistingPrAsync(AgentPullRequest pr, CancellationToken ct)
    {
        try
        {
            await base.WorkOnExistingPrAsync(pr, ct);
        }
        catch (Exception ex) when (ex.Message.Contains("complex", StringComparison.OrdinalIgnoreCase))
        {
            await EscalateComplexityAsync(pr, ct);
        }
    }

    #region Complexity Escalation

    private async Task EscalateComplexityAsync(AgentPullRequest pr, CancellationToken ct)
    {
        var title = $"Task #{pr.Number} exceeds Junior Engineer capability";
        var body = $"## Complexity Escalation\n\n" +
                   $"**Junior Engineer:** {Identity.DisplayName}\n" +
                   $"**PR:** #{pr.Number} — {pr.Title}\n\n" +
                   $"This task appears too complex for a Junior Engineer. " +
                   $"Requesting reassignment to a Senior or Principal Engineer.";

        try
        {
            var issue = await IssueWf.AskAgentAsync(
                Identity.DisplayName,
                "Principal Engineer",
                $"{title}\n\n{body}",
                ct);

            UpdateStatus(AgentStatus.Blocked, $"Escalated PR #{pr.Number} — too complex");
            Identity.AssignedPullRequest = null;

            Logger.LogWarning("Junior Engineer {Name} escalated PR #{PrNumber} via issue #{IssueNumber}",
                Identity.DisplayName, pr.Number, issue.Number);

            await MessageBus.PublishAsync(new HelpRequestMessage
            {
                FromAgentId = Identity.Id,
                ToAgentId = "PrincipalEngineer",
                MessageType = "ComplexityEscalation",
                IssueTitle = title,
                IssueBody = body,
                IsBlocker = true
            }, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Junior Engineer {Name} failed to escalate complexity",
                Identity.DisplayName);
            RecordError($"Escalation failed: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Warning, ex);
        }
    }

    #endregion

    #region Helpers

    private static string TruncateForContext(string content, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxLength)
            return content;

        return content[..maxLength] + "\n\n[... truncated for context window ...]";
    }

    #endregion
}
