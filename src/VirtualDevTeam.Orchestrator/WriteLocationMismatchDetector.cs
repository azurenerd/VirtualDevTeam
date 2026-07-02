using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.HealthMonitor;
using VirtualDevTeam.Core.HealthMonitor.Detectors;

namespace VirtualDevTeam.Orchestrator;

/// <summary>
/// Detects when an agent's status reason claims a file write succeeded but the file
/// is missing from the agent's workspace OR was written to an unexpected directory.
/// Targets the 2026-05-12 failure mode where Artist SME 1's status said
/// "PR #1505 step 7/9: <b>goblin/idle.png</b> (83KB) ✓" while a recursive search
/// of its workspace turned up zero PNGs by that name.
///
/// <para>
/// Heuristic: scan the agent's status reason for filename mentions
/// (e.g. <c>archer-tower/idle.png (68KB) ✓</c>) and confirm the file actually
/// exists in the agent's workspace. If the status mentions a directory the
/// workspace doesn't have (e.g. <c>../../client/public/...</c> resolving outside
/// the workspace), flag a directory mismatch.
/// </para>
///
/// <para>
/// Best-effort: workspace path is read from the agent's role-default config
/// (<c>.agents/{agent-id-slug}/{repo-name}</c>); if unavailable the detector
/// no-ops. False positives are minimized by requiring (a) the status text to
/// include the ✓ marker (claim of success) and (b) at least 30s to have passed
/// since the status was set (lets a slow filesystem flush settle).
/// </para>
/// </summary>
public sealed class WriteLocationMismatchDetector : IFlowDetector
{
    public string DetectorId => "write-location-mismatch";

    /// <summary>Minimum age of a status reason before we trust the file-not-found result.
    /// Below this we assume the agent is mid-write and the FS hasn't synced.</summary>
    internal static readonly TimeSpan MinStatusAge = TimeSpan.FromSeconds(30);

    /// <summary>Pattern: capture filenames like "goblin/idle.png" or "client/public/x.json"
    /// followed by an optional size annotation and a ✓ marker.
    /// Examples that match:
    ///   "step 7/9: <b>goblin/idle.png</b> (83KB) ✓"
    ///   "saved client/src/components/Foo.tsx ✓"
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex FileMention = new(
        @"(?<path>[a-zA-Z0-9._\-/]+\.[a-zA-Z0-9]+)\s*(?:\(\d+(?:\.\d+)?(?:KB|MB|B)\)\s*)?✓",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly ILogger<WriteLocationMismatchDetector> _logger;
    private readonly Func<string, string?> _workspaceResolver;
    private readonly IOptionsMonitor<VirtualDevTeamConfig>? _config;

    public WriteLocationMismatchDetector(
        ILogger<WriteLocationMismatchDetector> logger,
        IOptionsMonitor<VirtualDevTeamConfig> config)
    {
        _logger = logger;
        _config = config;
        _workspaceResolver = ResolveAgentWorkspaceFromConfig;
    }

    /// <summary>Constructor for tests — inject a custom workspace resolver.</summary>
    internal WriteLocationMismatchDetector(
        ILogger<WriteLocationMismatchDetector> logger,
        Func<string, string?> workspaceResolver)
    {
        _logger = logger;
        _workspaceResolver = workspaceResolver;
        _config = null;
    }

    public Task<IReadOnlyList<FlowFinding>> DetectAsync(DetectorContext ctx, CancellationToken ct)
    {
        var findings = new List<FlowFinding>();
        try
        {
            foreach (var agent in ctx.Agents)
            {
                if (string.IsNullOrEmpty(agent.StatusReason)) continue;
                if (agent.StatusChangedAt is null) continue;
                if ((ctx.Now - agent.StatusChangedAt.Value) < MinStatusAge) continue;

                var matches = FileMention.Matches(agent.StatusReason);
                if (matches.Count == 0) continue;

                var workspace = _workspaceResolver(agent.Id);
                if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace)) continue;

                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    if (ct.IsCancellationRequested) break;
                    var rel = m.Groups["path"].Value;
                    // Reject obvious noise like ".gitignore" or anything with no slash AND no
                    // extension that's not a real asset format. Real asset claims include a
                    // directory part ("foo/bar.png") not a bare filename like "log.txt".
                    if (!rel.Contains('/') && !rel.Contains('\\')) continue;

                    var basename = Path.GetFileName(rel);
                    var found = false;
                    try
                    {
                        // Recursive search by basename — this catches the case where the agent
                        // claimed "client/public/X.png" but actually wrote to "X.png" or to
                        // some other subdirectory. Cap enumeration at 3000 entries to bound cost.
                        var enumerated = 0;
                        foreach (var path in Directory.EnumerateFiles(workspace, basename, SearchOption.AllDirectories))
                        {
                            enumerated++;
                            if (enumerated > 3000) break;
                            // Found at least one file with the matching basename — not a missing-file case.
                            found = true;
                            // Check if the path matches the claimed sub-path (location match).
                            var rel2 = Path.GetRelativePath(workspace, path).Replace('\\', '/');
                            if (rel2.EndsWith(rel.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                            {
                                // Exact location match — agent claim is true.
                                goto NextMatch;
                            }
                            // Same basename but different directory — record as location mismatch.
                            findings.Add(new FlowFinding
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                DetectedAt = ctx.Now,
                                DetectorId = DetectorId,
                                Severity = FlowFindingSeverity.Warning,
                                TargetAgentId = agent.Id,
                                TargetDisplayName = agent.DisplayName,
                                Summary = $"Agent claimed write to '{rel}' but the file is at a different location",
                                Rationale =
                                    $"Status reason claimed write to '{rel}' (with ✓ marker). A file with the same " +
                                    $"basename was found at '{rel2}' instead. Either the agent's status text is " +
                                    "wrong or the agent wrote to the wrong directory. Investigate the prompt " +
                                    "instructions and the agent's path-resolution logic.",
                                DedupKey = $"write-location:{agent.Id}:{rel}",
                            });
                            goto NextMatch;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "WriteLocationMismatchDetector: workspace search for {Basename} in {Workspace} failed",
                            basename, workspace);
                    }

                    if (!found)
                    {
                        findings.Add(new FlowFinding
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            DetectedAt = ctx.Now,
                            DetectorId = DetectorId,
                            Severity = FlowFindingSeverity.Warning,
                            TargetAgentId = agent.Id,
                            TargetDisplayName = agent.DisplayName,
                            Summary = $"Agent claimed write to '{rel}' but no such file exists in workspace",
                            Rationale =
                                $"Status reason: \"{Truncate(agent.StatusReason, 200)}\" includes a ✓ for '{rel}' " +
                                $"but a recursive search of the agent's workspace ({workspace}) found no file " +
                                "with that basename. Either (a) the agent is hallucinating success, (b) the " +
                                "agent wrote outside its sandboxed workspace, or (c) the agent committed and " +
                                "the working tree was already cleaned. Investigate.",
                            DedupKey = $"write-location:{agent.Id}:missing:{rel}",
                        });
                    }

                    NextMatch:;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WriteLocationMismatchDetector tick failed (non-fatal)");
        }
        return Task.FromResult<IReadOnlyList<FlowFinding>>(findings);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// rd-3 fix (2026-05-12 evening): the previous default resolver only probed derived
    /// paths (CWD/.agents/, BaseDirectory/.agents/, etc.) and silently no-opped for any
    /// runtime that uses a custom WorkspaceConfig.RootPath via develop-settings.json —
    /// which is every production deployment. Now reads VirtualDevTeamConfig.Workspace.RootPath
    /// (the same source-of-truth used at runtime, populated from develop-settings.json by
    /// RunCoordinator.ReconfigureServicesForRepoAsync), then falls back to the legacy probe
    /// list as a safety net for tests / cold-start before configuration loads.
    /// </summary>
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
            catch { /* fall through to derived-path probe */ }
        }
        return ResolveAgentWorkspaceDefault(agentId);
    }

    /// <summary>
    /// Default workspace resolver. Agents follow the convention
    /// <c>.agents/{agent-id-slug}/{repo-name}/</c> rooted at the runner's working
    /// directory or the configured workspace root. We probe a couple of likely
    /// candidates and return the first one that exists.
    /// </summary>
    private static string? ResolveAgentWorkspaceDefault(string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return null;
        var slug = agentId; // agent IDs are already slugified
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".agents", slug),
            Path.Combine(AppContext.BaseDirectory, ".agents", slug),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".agents", slug),
        };
        foreach (var c in candidates)
        {
            try
            {
                var resolved = Path.GetFullPath(c);
                if (!Directory.Exists(resolved)) continue;
                // Find the first sub-directory (the repo name) and use that as the search root.
                var repoDir = Directory.EnumerateDirectories(resolved).FirstOrDefault();
                return repoDir ?? resolved;
            }
            catch { /* best-effort */ }
        }
        return null;
    }
}
