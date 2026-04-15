using AgentSquad.Core.Configuration;
using AgentSquad.Core.Prompts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Tests;

public class PromptTemplateServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PromptTemplateService _service;

    public PromptTemplateServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"prompt-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new AgentSquadConfig
        {
            Prompts = new PromptsConfig
            {
                BasePath = _tempDir,
                HotReload = false,
                MaxIncludeDepth = 10
            }
        };

        _service = new PromptTemplateService(
            Options.Create(config),
            NullLogger<PromptTemplateService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteTemplate(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    // --- Variable Substitution ---

    [Fact]
    public async Task RenderAsync_SubstitutesVariables()
    {
        WriteTemplate("test/hello.md", "Hello {{name}}, welcome to {{project}}!");
        var vars = new Dictionary<string, string>
        {
            ["name"] = "Alice",
            ["project"] = "AgentSquad"
        };

        var result = await _service.RenderAsync("test/hello", vars);

        Assert.Equal("Hello Alice, welcome to AgentSquad!", result);
    }

    [Fact]
    public async Task RenderAsync_HandlesWhitespaceInBraces()
    {
        WriteTemplate("test/spaces.md", "Hello {{ name }} and {{  project  }}!");
        var vars = new Dictionary<string, string>
        {
            ["name"] = "Bob",
            ["project"] = "Test"
        };

        var result = await _service.RenderAsync("test/spaces", vars);

        Assert.Equal("Hello Bob and Test!", result);
    }

    [Fact]
    public async Task RenderAsync_LeavesUndefinedVariablesAsIs()
    {
        WriteTemplate("test/undef.md", "Hello {{name}}, your role is {{role}}.");
        var vars = new Dictionary<string, string> { ["name"] = "Charlie" };

        var result = await _service.RenderAsync("test/undef", vars);

        Assert.Equal("Hello Charlie, your role is {{role}}.", result);
    }

    [Fact]
    public async Task RenderAsync_HandlesEmptyVariableValue()
    {
        WriteTemplate("test/empty.md", "Context:{{context}}End");
        var vars = new Dictionary<string, string> { ["context"] = "" };

        var result = await _service.RenderAsync("test/empty", vars);

        Assert.Equal("Context:End", result);
    }

    // --- Missing Templates ---

    [Fact]
    public async Task RenderAsync_ReturnsNullForMissingTemplate()
    {
        var result = await _service.RenderAsync("nonexistent/template", new Dictionary<string, string>());

        Assert.Null(result);
    }

    // --- Frontmatter Parsing ---

    [Fact]
    public async Task RenderAsync_StripsFrontmatter()
    {
        WriteTemplate("test/fm.md", """
            ---
            version: "1.0"
            description: "Test template"
            ---
            Body content here.
            """);

        var result = await _service.RenderAsync("test/fm", new Dictionary<string, string>());

        Assert.Equal("Body content here.", result?.Trim());
    }

    [Fact]
    public async Task GetMetadataAsync_ParsesVersionAndDescription()
    {
        WriteTemplate("test/meta.md", """
            ---
            version: "2.1"
            description: "My test template"
            variables:
              - name
              - project
            tags:
              - researcher
              - system
            ---
            Body.
            """);

        var meta = await _service.GetMetadataAsync("test/meta");

        Assert.NotNull(meta);
        Assert.Equal("2.1", meta!.Version);
        Assert.Equal("My test template", meta.Description);
        Assert.Equal(new[] { "name", "project" }, meta.Variables);
        Assert.Equal(new[] { "researcher", "system" }, meta.Tags);
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsNullForMissing()
    {
        var meta = await _service.GetMetadataAsync("does/not/exist");
        Assert.Null(meta);
    }

    [Fact]
    public void ParseFrontmatter_HandlesInlineArrays()
    {
        var content = """
            ---
            version: "1.0"
            variables: [alpha, beta, gamma]
            ---
            Content
            """;

        var (meta, body) = PromptTemplateService.ParseFrontmatter(content);

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, meta.Variables);
        Assert.Equal("Content", body.Trim());
    }

    [Fact]
    public void ParseFrontmatter_HandlesNoFrontmatter()
    {
        var content = "Just plain body content.";

        var (meta, body) = PromptTemplateService.ParseFrontmatter(content);

        Assert.True(string.IsNullOrEmpty(meta.Version));
        Assert.True(string.IsNullOrEmpty(meta.Description));
        Assert.Equal("Just plain body content.", body.Trim());
    }

    // --- Fragment Includes ---

    [Fact]
    public async Task RenderAsync_ResolvesIncludes()
    {
        WriteTemplate("shared/greeting.md", "Welcome to the team!");
        WriteTemplate("test/with-include.md", "Hello.\n{{> shared/greeting}}\nGoodbye.");

        var result = await _service.RenderAsync("test/with-include", new Dictionary<string, string>());

        Assert.Contains("Welcome to the team!", result);
        Assert.Contains("Hello.", result);
        Assert.Contains("Goodbye.", result);
    }

    [Fact]
    public async Task RenderAsync_ResolvesVariablesInIncludes()
    {
        WriteTemplate("shared/ctx.md", "Stack: {{tech_stack}}");
        WriteTemplate("test/inc-vars.md", "{{> shared/ctx}}");
        var vars = new Dictionary<string, string> { ["tech_stack"] = "C# .NET 8" };

        var result = await _service.RenderAsync("test/inc-vars", vars);

        Assert.Equal("Stack: C# .NET 8", result?.Trim());
    }

    [Fact]
    public async Task RenderAsync_DetectsCircularIncludes()
    {
        WriteTemplate("test/a.md", "{{> test/b}}");
        WriteTemplate("test/b.md", "{{> test/a}}");

        // Should throw InvalidOperationException on circular include
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RenderAsync("test/a", new Dictionary<string, string>()));
    }

    [Fact]
    public async Task RenderAsync_HandlesMissingInclude()
    {
        WriteTemplate("test/bad-inc.md", "Before\n{{> nonexistent/fragment}}\nAfter");

        var result = await _service.RenderAsync("test/bad-inc", new Dictionary<string, string>());

        Assert.NotNull(result);
        Assert.Contains("Before", result);
        Assert.Contains("After", result);
    }

    // --- Caching ---

    [Fact]
    public async Task RenderAsync_CachesTemplates()
    {
        WriteTemplate("test/cached.md", "Version 1");

        var result1 = await _service.RenderAsync("test/cached", new Dictionary<string, string>());
        Assert.Equal("Version 1", result1?.Trim());

        // Modify file — should still return cached version
        WriteTemplate("test/cached.md", "Version 2");

        var result2 = await _service.RenderAsync("test/cached", new Dictionary<string, string>());
        Assert.Equal("Version 1", result2?.Trim());
    }

    [Fact]
    public async Task InvalidateCache_ClearsSingleEntry()
    {
        WriteTemplate("test/inv.md", "Original");
        await _service.RenderAsync("test/inv", new Dictionary<string, string>());

        WriteTemplate("test/inv.md", "Updated");
        _service.InvalidateCache("test/inv");

        var result = await _service.RenderAsync("test/inv", new Dictionary<string, string>());
        Assert.Equal("Updated", result?.Trim());
    }

    [Fact]
    public async Task InvalidateCache_NullClearsAll()
    {
        WriteTemplate("test/c1.md", "One");
        WriteTemplate("test/c2.md", "Two");
        await _service.RenderAsync("test/c1", new Dictionary<string, string>());
        await _service.RenderAsync("test/c2", new Dictionary<string, string>());

        WriteTemplate("test/c1.md", "One-v2");
        WriteTemplate("test/c2.md", "Two-v2");
        _service.InvalidateCache(null);

        var r1 = await _service.RenderAsync("test/c1", new Dictionary<string, string>());
        var r2 = await _service.RenderAsync("test/c2", new Dictionary<string, string>());
        Assert.Equal("One-v2", r1?.Trim());
        Assert.Equal("Two-v2", r2?.Trim());
    }

    // --- ListTemplates ---

    [Fact]
    public void ListTemplates_FindsTemplatesInRole()
    {
        WriteTemplate("researcher/system.md", "sys");
        WriteTemplate("researcher/user.md", "usr");
        WriteTemplate("pm/spec.md", "spec");

        var templates = _service.ListTemplates("researcher");

        Assert.Contains("researcher/system", templates);
        Assert.Contains("researcher/user", templates);
        Assert.DoesNotContain("pm/spec", templates);
    }

    // --- GetRawContentAsync / SaveRawContentAsync ---

    [Fact]
    public async Task GetRawContentAsync_ReturnsFullContent()
    {
        var content = "---\nversion: \"1.0\"\n---\nBody here.";
        WriteTemplate("test/raw.md", content);

        var raw = await _service.GetRawContentAsync("test/raw");

        Assert.Equal(content, raw);
    }

    [Fact]
    public async Task SaveRawContentAsync_WritesAndInvalidatesCache()
    {
        WriteTemplate("test/save.md", "Original");
        await _service.RenderAsync("test/save", new Dictionary<string, string>());

        await _service.SaveRawContentAsync("test/save", "Updated content");

        var raw = await _service.GetRawContentAsync("test/save");
        Assert.Equal("Updated content", raw);

        // Cache should be invalidated
        var rendered = await _service.RenderAsync("test/save", new Dictionary<string, string>());
        Assert.Equal("Updated content", rendered?.Trim());
    }

    // --- Edge Cases ---

    [Fact]
    public async Task RenderAsync_HandlesEmptyTemplate()
    {
        WriteTemplate("test/empty.md", "");

        var result = await _service.RenderAsync("test/empty", new Dictionary<string, string>());

        Assert.Equal("", result);
    }

    [Fact]
    public async Task RenderAsync_HandlesFrontmatterOnly()
    {
        WriteTemplate("test/fm-only.md", "---\nversion: \"1.0\"\n---\n");

        var result = await _service.RenderAsync("test/fm-only", new Dictionary<string, string>());

        Assert.NotNull(result);
        Assert.Equal("", result!.Trim());
    }

    [Fact]
    public async Task RenderAsync_PreservesMultilineContent()
    {
        WriteTemplate("test/multi.md", "Line 1\nLine 2\n\nLine 4 with {{var}}.");
        var vars = new Dictionary<string, string> { ["var"] = "value" };

        var result = await _service.RenderAsync("test/multi", vars);

        Assert.Equal("Line 1\nLine 2\n\nLine 4 with value.", result);
    }
}
