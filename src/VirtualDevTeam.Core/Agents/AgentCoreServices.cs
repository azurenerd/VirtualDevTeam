using VirtualDevTeam.Core.AI;
using VirtualDevTeam.Core.Agents.Reasoning;
using VirtualDevTeam.Core.Agents.Steps;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.Messaging;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Core.Prompts;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Agents;

/// <summary>
/// Core services that every agent needs. Registered as a singleton and resolved
/// from DI by <c>ActivatorUtilities.CreateInstance</c> in AgentFactory.
/// </summary>
public class AgentCoreServices
{
    public AgentCoreServices(
        IMessageBus messageBus,
        ModelRegistry modelRegistry,
        IChatCompletionRunner chatRunner,
        ProjectFileManager projectFiles,
        AgentMemoryStore memoryStore,
        IGateCheckService gateCheck,
        IOptions<VirtualDevTeamConfig> config,
        IPromptTemplateService? promptService = null,
        RoleContextProvider? roleContextProvider = null,
        SelfAssessmentService? selfAssessment = null,
        IAgentReasoningLog? reasoningLog = null,
        IAgentTaskTracker? taskTracker = null,
        AgentStateStore? stateStore = null,
        FlowTimelineTracker? flowTimeline = null,
        Workspace.SharedCloneManager? sharedCloneManager = null,
        PushFailureTracker? pushFailureTracker = null)
    {
        MessageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        ModelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        ChatRunner = chatRunner ?? throw new ArgumentNullException(nameof(chatRunner));
        ProjectFiles = projectFiles ?? throw new ArgumentNullException(nameof(projectFiles));
        MemoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        GateCheck = gateCheck ?? throw new ArgumentNullException(nameof(gateCheck));
        ConfigOptions = config ?? throw new ArgumentNullException(nameof(config));
        PromptService = promptService;
        RoleContextProvider = roleContextProvider;
        SelfAssessment = selfAssessment;
        ReasoningLog = reasoningLog;
        TaskTracker = taskTracker;
        StateStore = stateStore;
        FlowTimeline = flowTimeline;
        SharedCloneManager = sharedCloneManager;
        PushFailureTracker = pushFailureTracker;
    }

    // Required services — every agent needs these
    public IMessageBus MessageBus { get; }
    public ModelRegistry ModelRegistry { get; }
    public IChatCompletionRunner ChatRunner { get; }
    public ProjectFileManager ProjectFiles { get; }
    public AgentMemoryStore MemoryStore { get; }
    public IGateCheckService GateCheck { get; }
    public IOptions<VirtualDevTeamConfig> ConfigOptions { get; }

    /// <summary>Convenience accessor — equivalent to <c>ConfigOptions.Value</c>.</summary>
    public VirtualDevTeamConfig Config => ConfigOptions.Value;

    // Optional services — not all agents use these
    public IPromptTemplateService? PromptService { get; }
    public RoleContextProvider? RoleContextProvider { get; }
    public SelfAssessmentService? SelfAssessment { get; }
    public IAgentReasoningLog? ReasoningLog { get; }
    public IAgentTaskTracker? TaskTracker { get; }
    public AgentStateStore? StateStore { get; }

    /// <summary>Pipeline milestone recorder for the Flow Timeline page.</summary>
    public FlowTimelineTracker? FlowTimeline { get; }

    /// <summary>Shared clone manager for Worktree/InPlace workspace modes.</summary>
    public Workspace.SharedCloneManager? SharedCloneManager { get; }

    /// <summary>Tracker for git push failures — wired into WorktreeWorkspace for FlowMonitor detection.</summary>
    public PushFailureTracker? PushFailureTracker { get; }
}
