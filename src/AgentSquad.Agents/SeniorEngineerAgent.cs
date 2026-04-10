using AgentSquad.Core.Agents;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentSquad.Agents;

/// <summary>
/// Senior Engineer agent — handles medium-complexity tasks with a multi-turn AI approach
/// including a self-review pass. Inherits all shared behavior from <see cref="EngineerAgentBase"/>.
/// </summary>
public class SeniorEngineerAgent : EngineerAgentBase
{
    public SeniorEngineerAgent(
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
        ILogger<SeniorEngineerAgent> logger,
        BuildRunner? buildRunner = null,
        TestRunner? testRunner = null,
        Core.Metrics.BuildTestMetrics? metrics = null,
        PlaywrightRunner? playwrightRunner = null)
        : base(identity, messageBus, github, prWorkflow, issueWorkflow,
               projectFiles, modelRegistry, stateStore, config.Value, memoryStore, logger,
               buildRunner, testRunner, metrics, playwrightRunner)
    {
    }

    protected override string GetRoleDisplayName() => "Senior Engineer";

    protected override string GetImplementationSystemPrompt(string techStack) =>
        $"You are a Senior Engineer implementing a task from a GitHub Issue. " +
        $"The project uses {techStack} as its technology stack. " +
        "You produce clean, well-structured code that follows the project architecture " +
        "and fulfills the business requirements from the PM specification. " +
        "Include proper error handling, logging, and unit tests. " +
        "Be thorough and practical.";

    /// <summary>
    /// Senior Engineers do an extra self-review turn for quality assurance.
    /// </summary>
    protected override async Task<string> RunSelfReviewAsync(
        ChatHistory history, string implementation, CancellationToken ct)
    {
        var kernel = Models.GetKernel(Identity.ModelTier, Identity.Id);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        history.AddUserMessage(
            "Review your implementation critically. Check for:\n" +
            "1. Missing error handling\n" +
            "2. Architecture alignment issues\n" +
            "3. Edge cases not covered\n" +
            "4. Any bugs or logic errors\n\n" +
            "If you find issues, provide the COMPLETE corrected files using the same FILE: format. " +
            "If it looks good, confirm with a brief summary.");

        var reviewResponse = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
        return reviewResponse.Content?.Trim() ?? implementation;
    }
}
