using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Preview;

/// <summary>
/// Artifact metadata record for the Test Artifacts dashboard tab.
/// </summary>
public record TestArtifactEntry
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required TestArtifactType Type { get; init; }
    public required string AgentName { get; init; }
    public string? PrNumber { get; init; }
    public DateTime CapturedAtUtc { get; init; }
    public long FileSizeBytes { get; init; }

    /// <summary>Source of the artifact — "Strategy Framework" or "Agent Tests".</summary>
    public string Source { get; init; } = "Agent Tests";

    /// <summary>Relative URL path for serving this artifact via API.</summary>
    public string ApiPath => $"/api/preview/artifacts/{Id}";
}

public enum TestArtifactType
{
    Screenshot,
    Video,
    Trace
}

/// <summary>
/// Scans agent workspace test-results/ directories and indexes all Playwright artifacts
/// (screenshots, videos, traces) with metadata for the dashboard to display.
/// </summary>
public sealed class TestArtifactIndexService
{
    private readonly ILogger<TestArtifactIndexService> _logger;
    private readonly VirtualDevTeamConfig _config;
    private List<TestArtifactEntry> _cache = [];
    private DateTime _lastScanUtc = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    public TestArtifactIndexService(
        ILogger<TestArtifactIndexService> logger,
        IOptions<VirtualDevTeamConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// Returns all indexed test artifacts, refreshing the cache if stale.
    /// </summary>
    public IReadOnlyList<TestArtifactEntry> GetArtifacts(bool forceRefresh = false)
    {
        if (!forceRefresh && DateTime.UtcNow - _lastScanUtc < _cacheDuration)
            return _cache;

        _cache = ScanAllWorkspaces();
        _lastScanUtc = DateTime.UtcNow;
        return _cache;
    }

    /// <summary>
    /// Find a specific artifact by ID. If not found in cache, forces a rescan
    /// to catch files written after the last index (e.g., strategy framework
    /// media artifacts copied to durable storage mid-evaluation).
    /// </summary>
    public TestArtifactEntry? GetArtifactById(string id)
    {
        var artifacts = GetArtifacts();
        var entry = artifacts.FirstOrDefault(a => a.Id == id);
        if (entry is not null) return entry;

        // Cache miss — force rescan in case the file was written after last index
        artifacts = GetArtifacts(forceRefresh: true);
        return artifacts.FirstOrDefault(a => a.Id == id);
    }

    /// <summary>
    /// Get artifacts filtered by PR number.
    /// </summary>
    public IReadOnlyList<TestArtifactEntry> GetArtifactsByPR(string prNumber)
    {
        return GetArtifacts().Where(a => a.PrNumber == prNumber).ToList();
    }

    /// <summary>
    /// Get artifacts filtered by agent name.
    /// </summary>
    public IReadOnlyList<TestArtifactEntry> GetArtifactsByAgent(string agentName)
    {
        return GetArtifacts()
            .Where(a => a.AgentName.Contains(agentName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Get artifacts filtered by source ("Strategy Framework" or "Agent Tests").
    /// </summary>
    public IReadOnlyList<TestArtifactEntry> GetArtifactsBySource(string source)
    {
        return GetArtifacts()
            .Where(a => a.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<TestArtifactEntry> ScanAllWorkspaces()
    {
        var results = new List<TestArtifactEntry>();
        var rootPath = _config.Workspace.RootPath;

        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            _logger.LogDebug("Workspace root {Path} does not exist, no artifacts to index", rootPath);
            return results;
        }

        // Scan all agent subdirectories
        foreach (var agentDir in Directory.GetDirectories(rootPath))
        {
            var agentName = Path.GetFileName(agentDir);

            // Look for test-results in any repo subdirectory.
            // Use safe enumeration to skip .sandbox, node_modules, etc. — these contain
            // inaccessible Windows cache paths that throw UnauthorizedAccessException.
            var testResultsDirs = SafeGetDirectories(agentDir, _config.Workspace.TestResultsDir);

            foreach (var testResultsDir in testResultsDirs)
            {
                // Try to determine PR number from directory structure
                // Pattern: .agents/{AgentName}/{Repo}/test-results/ or
                //          .agents/{AgentName}/{Repo}/pr-{N}/test-results/
                var prNumber = ExtractPrNumber(testResultsDir, agentDir);

                ScanDirectory(testResultsDir, agentName, prNumber, results);
            }
        }

        // Sort by capture time, newest first
        results.Sort((a, b) => b.CapturedAtUtc.CompareTo(a.CapturedAtUtc));

        // Also scan durable strategy-artifacts directory
        var strategyArtifactsDir = Path.Combine(rootPath, "strategy-artifacts");
        if (Directory.Exists(strategyArtifactsDir))
        {
            ScanStrategyArtifacts(strategyArtifactsDir, results);
        }

        _logger.LogDebug("Indexed {Count} test artifacts across all workspaces", results.Count);
        return results;
    }

    /// <summary>
    /// Recursively finds directories matching <paramref name="searchPattern"/>, skipping
    /// directories that are known to contain inaccessible OS cache paths (.sandbox, node_modules, etc.).
    /// Mirrors the SafeGetFiles pattern from BuildRunner.
    /// </summary>
    private List<string> SafeGetDirectories(string root, string searchPattern)
    {
        // Skip directories known to contain inaccessible OS cache paths or irrelevant content.
        // Do NOT skip .candidates/.candidates-eval — they contain strategy test-results.
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sandbox", ".git", "node_modules", "bin", "obj",
        };

        var results = new List<string>();
        SafeGetDirectoriesRecursive(root, searchPattern, skipDirs, results);
        return results;
    }

    private void SafeGetDirectoriesRecursive(
        string current, string searchPattern, HashSet<string> skipDirs, List<string> results)
    {
        try
        {
            foreach (var subDir in Directory.GetDirectories(current))
            {
                var dirName = Path.GetFileName(subDir);
                if (skipDirs.Contains(dirName))
                    continue;

                if (string.Equals(dirName, searchPattern, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(subDir);
                }

                SafeGetDirectoriesRecursive(subDir, searchPattern, skipDirs, results);
            }
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Skipping inaccessible directory: {Path}", current);
        }
        catch (IOException)
        {
            _logger.LogDebug("Skipping inaccessible directory (IO): {Path}", current);
        }
    }

    private void ScanDirectory(string dir, string agentName, string? prNumber, List<TestArtifactEntry> results)
    {
        if (!Directory.Exists(dir)) return;

        // Screenshots (PNG + GIF)
        var screenshotsDir = Path.Combine(dir, "screenshots");
        if (Directory.Exists(screenshotsDir))
        {
            foreach (var file in Directory.GetFiles(screenshotsDir, "*.png", SearchOption.AllDirectories))
                results.Add(CreateEntry(file, TestArtifactType.Screenshot, agentName, prNumber));
            foreach (var file in Directory.GetFiles(screenshotsDir, "*.gif", SearchOption.AllDirectories))
                results.Add(CreateEntry(file, TestArtifactType.Screenshot, agentName, prNumber));
        }

        // Also look for screenshots directly in test-results (some configs put them here)
        foreach (var file in Directory.GetFiles(dir, "*.png", SearchOption.TopDirectoryOnly))
            results.Add(CreateEntry(file, TestArtifactType.Screenshot, agentName, prNumber));
        foreach (var file in Directory.GetFiles(dir, "*.gif", SearchOption.TopDirectoryOnly))
            results.Add(CreateEntry(file, TestArtifactType.Screenshot, agentName, prNumber));

        // Videos
        var videosDir = Path.Combine(dir, "videos");
        if (Directory.Exists(videosDir))
        {
            foreach (var file in Directory.GetFiles(videosDir, "*.webm", SearchOption.AllDirectories))
                results.Add(CreateEntry(file, TestArtifactType.Video, agentName, prNumber));
        }

        // Traces
        var tracesDir = Path.Combine(dir, "traces");
        if (Directory.Exists(tracesDir))
        {
            foreach (var file in Directory.GetFiles(tracesDir, "*.zip", SearchOption.AllDirectories))
                results.Add(CreateEntry(file, TestArtifactType.Trace, agentName, prNumber));
        }
    }

    /// <summary>
    /// Scans the durable strategy-artifacts/ directory for media files.
    /// Structure: strategy-artifacts/{runId}/{taskId}/{strategyId}/{files}
    /// </summary>
    private void ScanStrategyArtifacts(string strategyDir, List<TestArtifactEntry> results)
    {
        try
        {
            foreach (var file in Directory.GetFiles(strategyDir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var type = ext switch
                {
                    ".png" => TestArtifactType.Screenshot,
                    ".gif" => TestArtifactType.Screenshot,
                    ".webm" => TestArtifactType.Video,
                    _ => (TestArtifactType?)null
                };
                if (type is null) continue;
                results.Add(CreateEntry(file, type.Value, "Strategy Framework", null));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to scan strategy-artifacts directory");
        }
    }

    private static TestArtifactEntry CreateEntry(string fullPath, TestArtifactType type, string agentName, string? prNumber)
    {
        // Canonicalize path for stable hash computation (must match Strategies.razor GetArtifactUrl)
        fullPath = Path.GetFullPath(fullPath);
        var fi = new FileInfo(fullPath);
        // Create a stable ID from canonical path hash
        var id = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(fullPath)))[..16].ToLowerInvariant();

        // Detect source: files with "framework-" prefix are from the Strategy Framework
        var source = fi.Name.StartsWith("framework-", StringComparison.OrdinalIgnoreCase)
            ? "Strategy Framework"
            : "Agent Tests";

        return new TestArtifactEntry
        {
            Id = id,
            FileName = fi.Name,
            FullPath = fullPath,
            Type = type,
            AgentName = agentName,
            PrNumber = prNumber,
            CapturedAtUtc = fi.LastWriteTimeUtc,
            FileSizeBytes = fi.Exists ? fi.Length : 0,
            Source = source
        };
    }

    private static string? ExtractPrNumber(string testResultsDir, string agentDir)
    {
        // Look for "pr-{N}" or "PR-{N}" in the path between agent dir and test-results
        var relativePath = Path.GetRelativePath(agentDir, testResultsDir);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var segment in segments)
        {
            if (segment.StartsWith("pr-", StringComparison.OrdinalIgnoreCase) &&
                segment.Length > 3 &&
                int.TryParse(segment[3..], out _))
            {
                return segment[3..];
            }
        }

        return null;
    }
}
