namespace VirtualDevTeam.Integration.Tests.Fixtures;

using System.Text.Json;
using System.Text.Json.Serialization;
using VirtualDevTeam.Integration.Tests.Fakes;

/// <summary>
/// Loads JSON fixture files and hydrates an <see cref="InMemoryGitHubService"/>
/// with the specified branches, files, issues, and pull requests.
/// Optionally writes agent script files for scripted CLI mode.
/// </summary>
public static class FixtureLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Load a fixture by name from the Fixtures directory.</summary>
    public static TestFixture Load(string fixtureName)
    {
        var path = ResolveFixturePath(fixtureName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TestFixture>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize fixture: {fixtureName}");
    }

    /// <summary>
    /// Hydrate an <see cref="InMemoryGitHubService"/> with the fixture data.
    /// Returns the fixture for further assertions.
    /// </summary>
    public static TestFixture HydrateGitHub(InMemoryGitHubService github, string fixtureName)
    {
        var fixture = Load(fixtureName);
        Hydrate(github, fixture);
        return fixture;
    }

    /// <summary>Hydrate the GitHub service from an already-loaded fixture.</summary>
    public static void Hydrate(InMemoryGitHubService github, TestFixture fixture)
    {
        // Seed branches (main already exists)
        foreach (var branch in fixture.Branches ?? [])
        {
            if (branch != "main")
                github.CreateBranchAsync(branch).GetAwaiter().GetResult();
        }

        // Seed files per branch
        if (fixture.Files != null)
        {
            foreach (var (branch, files) in fixture.Files)
            {
                github.SeedFiles(branch, new Dictionary<string, string>(files));
            }
        }

        // Seed issues
        foreach (var issueDef in fixture.Issues ?? [])
        {
            github.SeedIssue(
                issueDef.Title,
                issueDef.Body ?? "",
                issueDef.State ?? "open",
                issueDef.Labels?.ToArray());
        }

        // Seed pull requests
        foreach (var prDef in fixture.PullRequests ?? [])
        {
            github.SeedPullRequest(
                prDef.Title,
                prDef.HeadBranch,
                prDef.BaseBranch ?? "main",
                prDef.State ?? "open",
                prDef.Labels?.ToArray(),
                prDef.ChangedFiles != null ? new Dictionary<string, string>(prDef.ChangedFiles) : null);
        }
    }

    /// <summary>Write the agent scripts to a temp JSON file for scripted CLI mode.</summary>
    public static string? WriteScriptFile(TestFixture fixture)
    {
        if (fixture.AgentScripts == null || fixture.AgentScripts.Count == 0)
            return null;

        var path = Path.Combine(Path.GetTempPath(), $"fixture-scripts-{Guid.NewGuid():N}.json");
        var json = JsonSerializer.Serialize(fixture.AgentScripts, JsonOptions);
        File.WriteAllText(path, json);
        return path;
    }

    private static string ResolveFixturePath(string fixtureName)
    {
        // Try direct path first
        if (File.Exists(fixtureName))
            return fixtureName;

        // Try relative to Fixtures directory
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures");
        var path = Path.Combine(dir, fixtureName.EndsWith(".json") ? fixtureName : $"{fixtureName}.json");
        if (File.Exists(path))
            return path;

        // Try relative to project root
        var projectDir = FindProjectRoot();
        if (projectDir != null)
        {
            path = Path.Combine(projectDir, "tests", "VirtualDevTeam.Integration.Tests", "Fixtures",
                fixtureName.EndsWith(".json") ? fixtureName : $"{fixtureName}.json");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException($"Fixture not found: {fixtureName}. Searched in: {dir}");
    }

    private static string? FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VirtualDevTeam.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

// ── Fixture Models ──────────────────────────────────────────────

public class TestFixture
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public FixtureConfig? Config { get; set; }
    public List<string>? Branches { get; set; }
    public Dictionary<string, Dictionary<string, string>>? Files { get; set; }
    public List<FixtureIssue>? Issues { get; set; }
    public List<FixturePullRequest>? PullRequests { get; set; }
    public List<FixtureAgentScript>? AgentScripts { get; set; }
    public FixtureExpectations? Expect { get; set; }
}

public class FixtureConfig
{
    public bool SinglePRMode { get; set; }
    public string ProjectDescription { get; set; } = "";
}

public class FixtureIssue
{
    public int? Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? State { get; set; }
    public List<string>? Labels { get; set; }
}

public class FixturePullRequest
{
    public int? Number { get; set; }
    public string Title { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string? BaseBranch { get; set; }
    public string? State { get; set; }
    public List<string>? Labels { get; set; }
    public Dictionary<string, string>? ChangedFiles { get; set; }
}

public class FixtureAgentScript
{
    public string? PromptContains { get; set; }
    public string? Response { get; set; }
}

public class FixtureExpectations
{
    public int? MinIssues { get; set; }
    public int? MinPullRequests { get; set; }
    public List<string>? BranchExists { get; set; }
    public List<string>? FileExists { get; set; }
    public string? WorkflowPhase { get; set; }
}
