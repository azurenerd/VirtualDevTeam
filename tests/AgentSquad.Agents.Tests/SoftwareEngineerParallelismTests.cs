namespace AgentSquad.Agents.Tests;

/// <summary>
/// Tests for PE Parallelism Enhancements: file overlap detection, wave scheduling,
/// typed dependencies, path normalization, and issue body round-trip.
/// </summary>
public class SoftwareEngineerParallelismTests
{
    // ── Path Normalization ──────────────────────────────────────────────

    [Theory]
    [InlineData("MyApp/File.cs", "myapp/file.cs")]
    [InlineData("MyApp\\File.cs", "myapp/file.cs")]
    [InlineData("/MyApp/File.cs", "myapp/file.cs")]
    [InlineData("MyApp/File.cs(MyApp.Services)", "myapp/file.cs")]
    [InlineData("  MyApp/File.cs  ", "myapp/file.cs")]
    [InlineData("", "")]
    public void NormalizeFilePath_HandlesVariousFormats(string input, string expected)
    {
        Assert.Equal(expected, SoftwareEngineerAgent.NormalizeFilePath(input));
    }

    // ── File Extraction from FilePlan ───────────────────────────────────

    [Fact]
    public void ExtractAllFilesFromFilePlan_ExtractsCreateAndModify()
    {
        var filePlan = "CREATE:MyApp/Program.cs(MyApp);MODIFY:MyApp/Startup.cs;USE:IService(MyApp)";
        var files = SoftwareEngineerAgent.ExtractAllFilesFromFilePlan(filePlan);

        Assert.Equal(2, files.Count);
        Assert.Contains("myapp/program.cs", files);
        Assert.Contains("myapp/startup.cs", files);
        // USE should not be included
    }

    [Fact]
    public void ExtractAllFilesFromFilePlan_DeduplicatesSameFile()
    {
        var filePlan = "CREATE:MyApp/File.cs;MODIFY:MyApp/File.cs";
        var files = SoftwareEngineerAgent.ExtractAllFilesFromFilePlan(filePlan);

        Assert.Single(files);
    }

    [Fact]
    public void ExtractAllFilesFromFilePlan_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(SoftwareEngineerAgent.ExtractAllFilesFromFilePlan(""));
        Assert.Empty(SoftwareEngineerAgent.ExtractAllFilesFromFilePlan(null!));
    }

    // ── Shared File Extraction ──────────────────────────────────────────

    [Fact]
    public void ExtractSharedFilesFromFilePlan_ExtractsSharedDeclarations()
    {
        var filePlan = "CREATE:MyApp/Program.cs;SHARED:MyApp/Program.cs;CREATE:MyApp/Startup.cs";
        var shared = SoftwareEngineerAgent.ExtractSharedFilesFromFilePlan(filePlan);

        Assert.Single(shared);
        Assert.Contains("myapp/program.cs", shared);
    }

    [Fact]
    public void ExtractSharedFilesFromFilePlan_Empty_ReturnsEmpty()
    {
        Assert.Empty(SoftwareEngineerAgent.ExtractSharedFilesFromFilePlan(""));
    }

    // ── File Overlap Detection ──────────────────────────────────────────

    [Fact]
    public void DetectFileOverlaps_NoOverlaps_ReturnsEmpty()
    {
        var tasks = new List<EngineeringTask>
        {
            new() { Id = "T1", OwnedFiles = ["myapp/a.cs", "myapp/b.cs"] },
            new() { Id = "T2", OwnedFiles = ["myapp/c.cs", "myapp/d.cs"] }
        };

        var overlaps = SoftwareEngineerAgent.DetectFileOverlaps(tasks, []);
        Assert.Empty(overlaps);
    }

    [Fact]
    public void DetectFileOverlaps_WithOverlaps_ReturnsConflictingFiles()
    {
        var tasks = new List<EngineeringTask>
        {
            new() { Id = "T1", OwnedFiles = ["myapp/program.cs", "myapp/a.cs"] },
            new() { Id = "T2", OwnedFiles = ["myapp/program.cs", "myapp/b.cs"] }
        };

        var overlaps = SoftwareEngineerAgent.DetectFileOverlaps(tasks, []);
        Assert.Single(overlaps);
        Assert.True(overlaps.ContainsKey("myapp/program.cs"));
        Assert.Equal(2, overlaps["myapp/program.cs"].Count);
    }

    [Fact]
    public void DetectFileOverlaps_SharedFilesExcluded()
    {
        var tasks = new List<EngineeringTask>
        {
            new() { Id = "T1", OwnedFiles = ["myapp/program.cs", "myapp/a.cs"] },
            new() { Id = "T2", OwnedFiles = ["myapp/program.cs", "myapp/b.cs"] }
        };

        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp/program.cs" };
        var overlaps = SoftwareEngineerAgent.DetectFileOverlaps(tasks, shared);
        Assert.Empty(overlaps);
    }

    [Fact]
    public void DetectFileOverlaps_InfrastructureFilesExcluded()
    {
        var tasks = new List<EngineeringTask>
        {
            new() { Id = "T1", OwnedFiles = [".gitignore", "myapp/a.cs"] },
            new() { Id = "T2", OwnedFiles = [".gitignore", "myapp/b.cs"] }
        };

        var overlaps = SoftwareEngineerAgent.DetectFileOverlaps(tasks, []);
        Assert.Empty(overlaps);
    }

    // ── Typed Dependency Parsing ────────────────────────────────────────

    [Fact]
    public void ParseTypedDependencies_TypedFormat_ParsesCorrectly()
    {
        var rawDeps = new List<string> { "T1(files)", "T3(api)" };
        var (plainDeps, depTypes) = SoftwareEngineerAgent.ParseTypedDependencies(rawDeps);

        Assert.Equal(2, plainDeps.Count);
        Assert.Equal("T1", plainDeps[0]);
        Assert.Equal("T3", plainDeps[1]);
        Assert.Equal("files", depTypes["T1"]);
        Assert.Equal("api", depTypes["T3"]);
    }

    [Fact]
    public void ParseTypedDependencies_PlainFormat_NoTypes()
    {
        var rawDeps = new List<string> { "T1", "T3" };
        var (plainDeps, depTypes) = SoftwareEngineerAgent.ParseTypedDependencies(rawDeps);

        Assert.Equal(2, plainDeps.Count);
        Assert.Empty(depTypes);
    }

    [Fact]
    public void ParseTypedDependencies_MixedFormat()
    {
        var rawDeps = new List<string> { "T1", "T3(api)", "T5" };
        var (plainDeps, depTypes) = SoftwareEngineerAgent.ParseTypedDependencies(rawDeps);

        Assert.Equal(3, plainDeps.Count);
        Assert.Single(depTypes);
        Assert.Equal("api", depTypes["T3"]);
    }

    // ── Dependency Relaxation ───────────────────────────────────────────

    [Fact]
    public void CanRelaxDependency_ApiDep_W1Task_CanRelax()
    {
        var depTask = new EngineeringTask { Id = "T2", Wave = "W1" };
        Assert.True(SoftwareEngineerAgent.CanRelaxDependency("api", depTask, []));
    }

    [Fact]
    public void CanRelaxDependency_FileDep_CannotRelax()
    {
        var depTask = new EngineeringTask { Id = "T2", Wave = "W1" };
        Assert.False(SoftwareEngineerAgent.CanRelaxDependency("files", depTask, []));
    }

    [Fact]
    public void CanRelaxDependency_SchemaDep_OnlyT1CanRelax()
    {
        var t1 = new EngineeringTask { Id = "T1", Wave = "W1" };
        var t2 = new EngineeringTask { Id = "T2", Wave = "W1" };
        Assert.True(SoftwareEngineerAgent.CanRelaxDependency("schema", t1, []));
        Assert.False(SoftwareEngineerAgent.CanRelaxDependency("schema", t2, []));
    }

    [Fact]
    public void CanRelaxDependency_UnknownType_CannotRelax()
    {
        var depTask = new EngineeringTask { Id = "T2", Wave = "W1" };
        Assert.False(SoftwareEngineerAgent.CanRelaxDependency("unknown", depTask, []));
    }

    // ── Issue Body Round-Trip ───────────────────────────────────────────

    [Fact]
    public void ParseWave_ExtractsFromBody()
    {
        var body = "## Metadata\n- **Task ID:** T2\n- **Wave:** W2\n- **Complexity:** Medium";
        Assert.Equal("W2", EngineeringTaskIssueManager.ParseWave(body));
    }

    [Fact]
    public void ParseWave_NoWave_DefaultsToW1()
    {
        var body = "## Metadata\n- **Task ID:** T1\n- **Complexity:** High";
        Assert.Equal("W1", EngineeringTaskIssueManager.ParseWave(body));
    }

    [Fact]
    public void ParseWave_NullBody_DefaultsToW1()
    {
        Assert.Equal("W1", EngineeringTaskIssueManager.ParseWave(null));
    }

    [Fact]
    public void ParseDependencyTypes_ExtractsFromBody()
    {
        var body = "## Metadata\n- **Dependency Types:** T1(files), T3(api)";
        var depTypes = EngineeringTaskIssueManager.ParseDependencyTypes(body);

        Assert.Equal(2, depTypes.Count);
        Assert.Equal("files", depTypes["T1"]);
        Assert.Equal("api", depTypes["T3"]);
    }

    [Fact]
    public void ParseDependencyTypes_NoDeps_ReturnsEmpty()
    {
        var body = "## Metadata\n- **Task ID:** T1";
        Assert.Empty(EngineeringTaskIssueManager.ParseDependencyTypes(body));
    }

    [Fact]
    public void ParseOwnedFiles_ExtractsFromBody()
    {
        var body = "## Metadata\n- **Owned Files:** myapp/a.cs, myapp/b.cs, myapp/c.cs";
        var files = EngineeringTaskIssueManager.ParseOwnedFiles(body);

        Assert.Equal(3, files.Count);
        Assert.Contains("myapp/a.cs", files);
        Assert.Contains("myapp/b.cs", files);
        Assert.Contains("myapp/c.cs", files);
    }

    [Fact]
    public void ParseOwnedFiles_NoFiles_ReturnsEmpty()
    {
        Assert.Empty(EngineeringTaskIssueManager.ParseOwnedFiles("just some text"));
        Assert.Empty(EngineeringTaskIssueManager.ParseOwnedFiles(null));
    }

    [Fact]
    public void BuildIssueBodyWithDeps_IncludesWaveAndOwnedFiles()
    {
        var task = new EngineeringTask
        {
            Id = "T3",
            Name = "Build auth",
            Description = "Implement JWT auth.",
            Complexity = "Medium",
            Wave = "W1",
            OwnedFiles = ["myapp/auth.cs", "myapp/token.cs"],
            DependencyTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["T1"] = "files"
            }
        };

        var body = EngineeringTaskIssueManager.BuildIssueBodyWithDeps(task, [98]);

        Assert.Contains("**Wave:** W1", body);
        Assert.Contains("**Owned Files:** myapp/auth.cs, myapp/token.cs", body);
        Assert.Contains("**Dependency Types:** T1(files)", body);
        Assert.Contains("**Depends On:** #98", body);
    }

    [Fact]
    public void MapIssueToTask_WithWaveAndTypedDeps_MapsCorrectly()
    {
        var issue = new AgentSquad.Core.GitHub.Models.AgentIssue
        {
            Number = 200,
            Title = "[T3] Build auth module",
            Body = "## Build auth module\n\nImplement JWT.\n\n## Metadata\n" +
                   "- **Task ID:** T3\n- **Complexity:** Medium\n- **Wave:** W2\n" +
                   "- **Parent Issue:** #50\n- **Depends On:** #98\n" +
                   "- **Dependency Types:** T1(files), T2(api)\n" +
                   "- **Owned Files:** myapp/auth.cs, myapp/token.cs",
            State = "open",
            Url = "https://github.com/owner/repo/issues/200",
            Labels = ["engineering-task", "complexity:medium"]
        };

        var task = EngineeringTaskIssueManager.MapIssueToTask(issue);

        Assert.Equal("W2", task.Wave);
        Assert.Equal(2, task.DependencyTypes.Count);
        Assert.Equal("files", task.DependencyTypes["T1"]);
        Assert.Equal("api", task.DependencyTypes["T2"]);
        Assert.Equal(2, task.OwnedFiles.Count);
        Assert.Contains("myapp/auth.cs", task.OwnedFiles);
    }

    // ── EngineeringTask Record Defaults ─────────────────────────────────

    [Fact]
    public void EngineeringTask_DefaultValues_AreCorrect()
    {
        var task = new EngineeringTask();
        Assert.Equal("W1", task.Wave);
        Assert.Empty(task.OwnedFiles);
        Assert.Empty(task.DependencyTypes);
    }
}
