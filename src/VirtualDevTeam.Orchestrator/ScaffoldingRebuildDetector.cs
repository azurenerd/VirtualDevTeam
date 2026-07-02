using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Belt-and-suspenders defense against agents re-creating project scaffolding files
/// (Program.cs, .sln, package.json, etc.) that already exist in the repo. This pattern
/// indicates an agent has incorrectly decided to scaffold a fresh project rather than
/// extend the existing one — a symptom of the issue-reopen / duplicate-task pattern where
/// an agent picks up a re-opened task and treats the workspace as a blank slate.
///
/// <para>
/// Strategy: every <see cref="ScanInterval"/> minutes, for each Working agent, locate the
/// agent's workspace directory and check whether any <see cref="ScaffoldingMarkers"/> file
/// was CREATED (not merely edited) within the last <see cref="RecentWriteThreshold"/> AND
/// is smaller than the corroborating size threshold (freshly-scaffolded stubs are small).
/// Using <c>CreationTime</c> rather than <c>LastWriteTime</c> prevents false positives from
/// legitimate agent edits to pre-existing files in the workspace.
/// </para>
///
/// <para>
/// Best-effort: workspace path resolution follows the same convention as
/// <see cref="WriteLocationMismatchDetector"/> — reads from VirtualDevTeamConfig first,
/// then probes default candidates. Silently skips agents with no resolvable workspace.
/// </para>
///
/// <para>Dedup key: <c>scaffolding-rebuild:{agentId}:{filename}</c></para>
/// </summary>
public sealed class ScaffoldingRebuildDetector : IFlowDetector
{
    public string DetectorId => "scaffolding-rebuild";

    /// <summary>Minimum interval between full workspace scans to bound filesystem cost.</summary>
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);

    /// <summary>A scaffolding file created within this window is considered "freshly scaffolded".</summary>
    internal static readonly TimeSpan RecentWriteThreshold = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Corroborating size threshold: freshly-created scaffold files are typically small stubs.
    /// A file larger than this was likely an existing mature file, not a fresh recreation.
    /// </summary>
    private const long CorroboratingSizeThresholdBytes = 50 * 1024; // 50 KB

    /// <summary>Filenames that signal project scaffolding recreation when freshly written.</summary>
    private static readonly string[] ScaffoldingMarkers =
    [
        "Program.cs",
        "appsettings.json",
        "package.json",
        "Dockerfile",
        "docker-compose.yml",
        "docker-compose.yaml",
    ];

    /// <summary>
    /// Glob patterns for solution files (.sln) — detected separately because they have
    /// variable names (MyProject.sln, VirtualDevTeam.sln, etc.).
    /// </summary>
    private const string SlnPattern = "*.sln";

    private DateTimeOffset _lastScanAt = DateTimeOffset.MinValue;

    private readonly ILogger<ScaffoldingRebuildDetector> _logger;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _config;
    private readonly Func<string, string?> _workspaceResolver;

    public ScaffoldingRebuildDetector(
        ILogger<ScaffoldingRebuildDetector> logger,
        IOptionsMonitor<VirtualDevTeamConfig> config)
    {
        _logger = logger;
        _config = config;
        _workspaceResolver = ResolveAgentWorkspaceFromConfig;
    }

    /// <summary>Constructor for tests — inject a custom workspace resolver.</summary>
    internal ScaffoldingRebuildDetector(
        ILogger<ScaffoldingRebuildDetector> logger,
        Func<string, string?> workspaceResolver)
    {
        _logger = logger;
        _workspaceResolver = workspaceResolver;
        _config = null;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        if (ctx.Now - _lastScanAt < ScanInterval)
            return Task.FromResult<IReadOnlyList<FlowFinding>>(Array.Empty<FlowFinding>());

        _lastScanAt = ctx.Now;

        var findings = new List<FlowFinding>();
        try
        {
            var recentCutoff = ctx.Now.UtcDateTime - RecentWriteThreshold;

            foreach (var agent in ctx.Agents)
            {
                if (ct.IsCancellationRequested) break;
                if (agent.Status != "Working") continue;

                var workspace = _workspaceResolver(agent.Id);
                if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace)) continue;

                // Check named scaffolding markers.
                foreach (var marker in ScaffoldingMarkers)
                {
                    if (ct.IsCancellationRequested) break;
                    TryCheckFile(workspace, marker, agent, ctx, recentCutoff, findings);
                }

                // Check for .sln files (variable names).
                try
                {
                    foreach (var slnFile in Directory.EnumerateFiles(workspace, SlnPattern, SearchOption.TopDirectoryOnly))
                    {
                        if (ct.IsCancellationRequested) break;
                        TryCheckFile(slnFile, Path.GetFileName(slnFile), agent, ctx, recentCutoff, findings, fullPath: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "ScaffoldingRebuildDetector: .sln scan in {Workspace} failed (non-fatal)", workspace);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ScaffoldingRebuildDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    private static void TryCheckFile(
        string workspaceOrPath,
        string filename,
        AgentStateView agent,
        DetectorContext ctx,
        DateTime recentCutoff,
        List<FlowFinding> findings,
        bool fullPath = false)
    {
        try
        {
            var filePath = fullPath ? workspaceOrPath : Path.Combine(workspaceOrPath, filename);
            if (!File.Exists(filePath)) return;

            var creationTime = File.GetCreationTimeUtc(filePath);
            if (creationTime < recentCutoff) return;

            // Corroborate with file size: freshly-scaffolded stubs are small.
            // A large file was almost certainly an existing mature file that was edited,
            // not recreated — skip it to avoid false positives on legitimate edits.
            var fileSize = new FileInfo(filePath).Length;
            if (fileSize > CorroboratingSizeThresholdBytes) return;

            // File was CREATED within the recent threshold AND is small enough to be a fresh scaffold.
            findings.Add(new FlowFinding
            {
                Id = Guid.NewGuid().ToString("N"),
                DetectedAt = ctx.Now,
                DetectorId = "scaffolding-rebuild",
                Severity = FlowFindingSeverity.Warning,
                TargetAgentId = agent.Id,
                TargetDisplayName = agent.DisplayName,
                TargetResource = $"file:{filename}",
                Summary =
                    $"Agent {agent.DisplayName} freshly created scaffolding file '{filename}' in its workspace",
                Rationale =
                    $"Agent {agent.DisplayName} ({agent.Id}) has a freshly-CREATED '{filename}' " +
                    $"(created {creationTime:u}, within the {RecentWriteThreshold.TotalMinutes:0}-minute window; " +
                    $"file size {fileSize / 1024.0:0.#} KB, under the {CorroboratingSizeThresholdBytes / 1024} KB corroboration threshold). " +
                    "Scaffolding files (Program.cs, .sln, package.json, etc.) should already exist in the " +
                    "target repository. A freshly-created copy of one of these files indicates the agent is " +
                    "scaffolding a new project rather than extending the existing codebase — a known symptom " +
                    "of the issue-reopen pattern where a re-opened task is treated as a blank-slate new task. " +
                    "NOTE: this detector fires on CreationTime, not LastWriteTime, to avoid false positives " +
                    "from legitimate edits to existing files. Verify the agent's working branch is based on " +
                    "the correct HEAD and that it is modifying existing files rather than recreating them.",
                DedupKey = $"scaffolding-rebuild:{agent.Id}:{filename}",
            });
        }
        catch (Exception)
        {
            // Best-effort: file access errors are silently ignored.
        }
    }

    private string? ResolveAgentWorkspaceFromConfig(string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return null;
        var cfg = _config?.CurrentValue;
        var rootPath = cfg?.Workspace?.RootPath;
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            try
            {
                var resolved = Path.GetFullPath(Path.Combine(rootPath, agentId));
                if (Directory.Exists(resolved))
                {
                    var repoDir = Directory.EnumerateDirectories(resolved).FirstOrDefault();
                    return repoDir ?? resolved;
                }
            }
            catch { /* fall through */ }
        }
        return ResolveAgentWorkspaceDefault(agentId);
    }

    private static string? ResolveAgentWorkspaceDefault(string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return null;
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".agents", agentId),
            Path.Combine(AppContext.BaseDirectory, ".agents", agentId),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".agents", agentId),
        };
        foreach (var c in candidates)
        {
            try
            {
                var resolved = Path.GetFullPath(c);
                if (!Directory.Exists(resolved)) continue;
                var repoDir = Directory.EnumerateDirectories(resolved).FirstOrDefault();
                return repoDir ?? resolved;
            }
            catch { /* best-effort */ }
        }
        return null;
    }
}
