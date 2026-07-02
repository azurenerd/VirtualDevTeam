using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.Agents;

/// <summary>
/// DevPlatform services for agents that interact with PRs, issues, and repository content.
/// Agents that don't need platform interaction (e.g. CustomAgent) omit this from their constructor.
/// </summary>
public class AgentPlatformServices
{
    public AgentPlatformServices(
        IPullRequestService prService,
        IWorkItemService workItemService,
        IRepositoryContentService repoContent,
        IReviewService reviewService,
        PullRequestWorkflow prWorkflow,
        IBranchService? branchService = null,
        IssueWorkflow? issueWorkflow = null,
        IRunBranchProvider? branchProvider = null,
        IDocumentReferenceResolver? docResolver = null,
        IPlatformHostContext? platformHost = null)
    {
        PrService = prService ?? throw new ArgumentNullException(nameof(prService));
        WorkItemService = workItemService ?? throw new ArgumentNullException(nameof(workItemService));
        RepoContent = repoContent ?? throw new ArgumentNullException(nameof(repoContent));
        ReviewService = reviewService ?? throw new ArgumentNullException(nameof(reviewService));
        PrWorkflow = prWorkflow ?? throw new ArgumentNullException(nameof(prWorkflow));
        BranchService = branchService;
        IssueWorkflow = issueWorkflow;
        BranchProvider = branchProvider;
        DocResolver = docResolver;
        PlatformHost = platformHost;
    }

    // Required — agents using platform services need these
    public IPullRequestService PrService { get; }
    public IWorkItemService WorkItemService { get; }
    public IRepositoryContentService RepoContent { get; }
    public IReviewService ReviewService { get; }
    public PullRequestWorkflow PrWorkflow { get; }

    // Optional — not all platform agents need these
    public IBranchService? BranchService { get; }
    public IssueWorkflow? IssueWorkflow { get; }
    public IRunBranchProvider? BranchProvider { get; }
    public IDocumentReferenceResolver? DocResolver { get; }
    public IPlatformHostContext? PlatformHost { get; }
}
