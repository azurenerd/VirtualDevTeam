using AgentSquad.Core.Agents;
using AgentSquad.Core.Agents.Decisions;
using AgentSquad.Core.Agents.Steps;
using AgentSquad.Core.AI;
using AgentSquad.Core.Configuration;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.GitHub;
using AgentSquad.Core.Messaging;
using AgentSquad.Core.Persistence;
using AgentSquad.Core.Prompts;
using AgentSquad.Core.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Agents;

/// <summary>
/// A specialist engineer agent created dynamically from an <see cref="SMEAgentDefinition"/>.
/// Unlike <see cref="SmeAgent"/> (which extends CustomAgent), this extends <see cref="EngineerAgentBase"/>
/// and has full engineering capabilities: rework loops, build/test verification, clarification handling,
/// and the complete PR lifecycle. The specialist persona is injected from the definition.
/// 
/// Registers as <see cref="AgentRole.SoftwareEngineer"/> so the leader SE sees it as a team member
/// and can assign work to it via skill-based matching on <see cref="AgentIdentity.Capabilities"/>.
/// </summary>
public class SpecialistEngineerAgent : EngineerAgentBase
{
    /// <summary>The SME definition that created this specialist.</summary>
    public SMEAgentDefinition Definition { get; }

    public SpecialistEngineerAgent(
        AgentIdentity identity,
        SMEAgentDefinition definition,
        IMessageBus messageBus,
        IssueWorkflow issueWorkflow,
        PullRequestWorkflow prWorkflow,
        ProjectFileManager projectFiles,
        ModelRegistry modelRegistry,
        AgentStateStore stateStore,
        AgentMemoryStore memoryStore,
        IOptions<AgentSquadConfig> config,
        IGateCheckService gateCheck,
        ILogger<SpecialistEngineerAgent> logger,
        IPromptTemplateService? promptService = null,
        RoleContextProvider? roleContextProvider = null,
        BuildRunner? buildRunner = null,
        TestRunner? testRunner = null,
        Core.Metrics.BuildTestMetrics? metrics = null,
        PlaywrightRunner? playwrightRunner = null,
        DecisionGateService? decisionGate = null,
        IAgentTaskTracker? taskTracker = null,
        IBranchService? branchService = null)
        : base(identity, messageBus, prWorkflow, issueWorkflow,
               projectFiles, modelRegistry, stateStore, config.Value, memoryStore, gateCheck, logger,
               promptService, roleContextProvider, buildRunner, testRunner, metrics, playwrightRunner, decisionGate, taskTracker,
               branchService: branchService)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    protected override string GetRoleDisplayName() => Definition.RoleName;

    protected override string GetImplementationSystemPrompt(string techStack)
    {
        // Try loading from specialist-engineer prompt template first
        if (PromptService is not null)
        {
            var rendered = PromptService.RenderAsync("specialist-engineer/implementation-system",
                new Dictionary<string, string>
                {
                    ["tech_stack"] = techStack,
                    ["role_name"] = Definition.RoleName,
                    ["specialist_persona"] = Definition.SystemPrompt,
                    ["capabilities"] = string.Join(", ", Definition.Capabilities)
                }).GetAwaiter().GetResult();
            if (rendered is not null) return rendered;
        }

        // Fallback: build prompt from definition
        var capabilities = Definition.Capabilities.Count > 0
            ? $"Your specialized capabilities: {string.Join(", ", Definition.Capabilities)}. "
            : "";

        return $"You are a {Definition.RoleName} — a specialist engineer on the development team. " +
            $"{Definition.SystemPrompt}\n\n" +
            $"The project uses {techStack} as its technology stack. " +
            $"{capabilities}" +
            "The PM Specification defines the business requirements, and the Architecture " +
            "document defines the technical design. The GitHub Issue contains the User Story " +
            "and acceptance criteria for this specific task. " +
            "Produce detailed, production-quality code that leverages your domain expertise. " +
            "Ensure the implementation fulfills the business goals from the PM spec.\n\n" +
            "DEPENDENCY RULE: Before using ANY external library, package, or framework, check the project's " +
            "dependency manifest (e.g., .csproj, package.json, requirements.txt, etc.). " +
            "If a dependency is not already listed, add it to the manifest and include that file in your output. " +
            "Never import/using/require a package without ensuring it is declared in the project.";
    }

    protected override string GetReworkSystemPrompt(string techStack)
    {
        if (PromptService is not null)
        {
            var rendered = PromptService.RenderAsync("specialist-engineer/rework-system",
                new Dictionary<string, string>
                {
                    ["tech_stack"] = techStack,
                    ["role_name"] = Definition.RoleName,
                    ["specialist_persona"] = Definition.SystemPrompt,
                    ["capabilities"] = string.Join(", ", Definition.Capabilities)
                }).GetAwaiter().GetResult();
            if (rendered is not null) return rendered;
        }

        return $"You are a {Definition.RoleName} addressing review feedback on your pull request. " +
            $"The project uses {techStack}. " +
            $"{Definition.SystemPrompt}\n\n" +
            "You have access to the full architecture, PM spec, and engineering plan. " +
            "Carefully read the feedback, understand what needs to be fixed, and produce " +
            "an updated implementation that addresses ALL the feedback points. " +
            "Apply your specialist expertise to ensure the fix is thorough and production-quality.";
    }

    private int _idleLoopCount;
    private const int SelfClaimAfterIdleLoops = 2; // Self-claim faster than SE workers (specialists are always idle)

    /// <summary>
    /// Self-claim fallback: if this specialist has been idle for several loops and the leader
    /// hasn't assigned work via the bus, look for unassigned engineering-task issues on GitHub
    /// that match our capabilities and claim one directly.
    /// </summary>
    protected override async Task RunAdditionalLoopWorkAsync(CancellationToken ct)
    {
        // Only self-claim if we have no current work
        if (CurrentPrNumber is not null || AssignmentQueue.Count > 0)
        {
            _idleLoopCount = 0;
            return;
        }

        _idleLoopCount++;
        if (_idleLoopCount < SelfClaimAfterIdleLoops)
            return;

        try
        {
            // Find unassigned engineering tasks
            var allItems = await WorkItemService.ListByLabelAsync("engineering-task", "open", ct);
            var unassigned = allItems
                .Where(item => string.IsNullOrEmpty(item.AssignedAgent)
                    && !item.Labels.Contains("done", StringComparer.OrdinalIgnoreCase)
                    && !item.Labels.Contains("in-progress", StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (unassigned.Count == 0)
            {
                Logger.LogDebug("{Role} {Name} self-claim: no unassigned engineering tasks available",
                    Identity.Role, Identity.DisplayName);
                return;
            }

            // Prefer tasks matching our capabilities
            var capabilityKeywords = Definition.Capabilities
                .SelectMany(c => c.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(w => w.Length > 3)
                .ToHashSet();

            var bestMatch = unassigned
                .OrderByDescending(item =>
                {
                    var text = $"{item.Title} {item.Body}".ToLowerInvariant();
                    return capabilityKeywords.Count(kw => text.Contains(kw));
                })
                .First();

            // Self-assign: update the issue title to claim it
            var cleanTitle = bestMatch.Title.Contains(':')
                ? bestMatch.Title[(bestMatch.Title.IndexOf(':') + 1)..].Trim()
                : bestMatch.Title;
            var newTitle = $"{Identity.DisplayName}: {cleanTitle}";
            var newLabels = bestMatch.Labels.ToList();
            if (!newLabels.Contains("assigned"))
                newLabels.Add("assigned");

            await WorkItemService.UpdateAsync(bestMatch.Number, title: newTitle, labels: newLabels, state: "inprogress", ct: ct);

            // Enqueue as an assignment so the base loop picks it up next iteration
            AssignmentQueue.Enqueue(new IssueAssignmentMessage
            {
                FromAgentId = Identity.Id, // Self-assigned
                ToAgentId = Identity.Id,
                IssueNumber = bestMatch.Number,
                IssueTitle = cleanTitle,
                Complexity = "Medium", // Default — exact complexity is in the issue body
                MessageType = "IssueAssignment"
            });

            _idleLoopCount = 0;
            Logger.LogInformation(
                "{Role} {Name} self-claimed task #{IssueNumber}: {Title} (idle for {Loops} loops)",
                Identity.Role, Identity.DisplayName, bestMatch.Number, cleanTitle, _idleLoopCount);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Role} {Name} self-claim failed", Identity.Role, Identity.DisplayName);
        }
    }
}
