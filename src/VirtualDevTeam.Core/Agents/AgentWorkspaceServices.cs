using VirtualDevTeam.Core.Metrics;
using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Agents;

/// <summary>
/// Build, test, and deployment services for engineering agents that interact with workspaces.
/// All members are optional — presence depends on host configuration and runner capabilities.
/// </summary>
public class AgentWorkspaceServices
{
    public AgentWorkspaceServices(
        BuildRunner? buildRunner = null,
        TestRunner? testRunner = null,
        PlaywrightRunner? playwrightRunner = null,
        BuildTestMetrics? metrics = null)
    {
        BuildRunner = buildRunner;
        TestRunner = testRunner;
        PlaywrightRunner = playwrightRunner;
        Metrics = metrics;
    }

    public BuildRunner? BuildRunner { get; }
    public TestRunner? TestRunner { get; }
    public PlaywrightRunner? PlaywrightRunner { get; }
    public BuildTestMetrics? Metrics { get; }
}
