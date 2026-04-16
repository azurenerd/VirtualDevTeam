using AgentSquad.Core.Agents;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Diagnostics;

/// <summary>
/// Loads and caches docs/Requirements.md from the repository root.
/// Extracts role-specific sections so agents can reference their
/// expected behavior without re-reading the file each time.
/// </summary>
public sealed class RequirementsCache
{
    private readonly ILogger<RequirementsCache> _logger;
    private string _fullText = "";
    private readonly Dictionary<AgentRole, string> _roleSections = new();
    private string _scenarioText = "";
    private bool _loaded;
    private readonly object _loadLock = new();

    public RequirementsCache(ILogger<RequirementsCache> logger)
    {
        _logger = logger;
    }

    /// <summary>Full text of Requirements.md (empty if file not found).</summary>
    public string FullText
    {
        get
        {
            EnsureLoaded();
            return _fullText;
        }
    }

    /// <summary>The scenarios section of the requirements.</summary>
    public string Scenarios
    {
        get
        {
            EnsureLoaded();
            return _scenarioText;
        }
    }

    /// <summary>Returns the requirements section relevant to a specific role.</summary>
    public string GetRoleSection(AgentRole role)
    {
        EnsureLoaded();
        return _roleSections.GetValueOrDefault(role, "");
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_loadLock)
        {
            if (_loaded) return;
            LoadFromDisk();
            _loaded = true;
        }
    }

    private void LoadFromDisk()
    {
        var path = FindRequirementsFile();
        if (path is null)
        {
            _logger.LogWarning("Could not find docs/Requirements.md — diagnostics will use built-in expectations only");
            return;
        }

        try
        {
            _fullText = File.ReadAllText(path);
            _logger.LogInformation("Loaded Requirements.md ({Length} chars) from {Path}", _fullText.Length, path);
            ParseSections();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Requirements.md from {Path}", path);
        }
    }

    private static string? FindRequirementsFile()
    {
        // Walk up from CWD looking for docs/Requirements.md
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "Requirements.md");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private void ParseSections()
    {
        // Extract role-specific sections by matching ## headers
        ExtractSection("Program Manager", AgentRole.ProgramManager);
        ExtractSection("Researcher", AgentRole.Researcher);
        ExtractSection("Architect", AgentRole.Architect);
        ExtractSection("Software Engineer", AgentRole.SoftwareEngineer);
        ExtractSection("Test Engineer", AgentRole.TestEngineer);

        // Extract scenario sections (## End-to-End or ### Scenario)
        var scenarioStart = _fullText.IndexOf("## End-to-End", StringComparison.OrdinalIgnoreCase);
        if (scenarioStart < 0)
            scenarioStart = _fullText.IndexOf("## 20.", StringComparison.OrdinalIgnoreCase);
        if (scenarioStart >= 0)
            _scenarioText = _fullText[scenarioStart..];
    }

    private void ExtractSection(string headerKeyword, AgentRole role)
    {
        // Find ## header containing the keyword
        var lines = _fullText.Split('\n');
        var startLine = -1;
        var endLine = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("## ", StringComparison.Ordinal)
                && line.Contains(headerKeyword, StringComparison.OrdinalIgnoreCase))
            {
                startLine = i;
                continue;
            }

            // End at next ## header
            if (startLine >= 0 && i > startLine
                && line.StartsWith("## ", StringComparison.Ordinal))
            {
                endLine = i;
                break;
            }
        }

        if (startLine >= 0)
        {
            _roleSections[role] = string.Join('\n', lines[startLine..endLine]);
        }
    }
}
