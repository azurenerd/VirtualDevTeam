using VirtualDevTeam.Core.Workspace;

namespace VirtualDevTeam.Core.Tests;

/// <summary>
/// Pins the OpenAPI parsing + path-substitution helpers that power
/// <see cref="PlaywrightRunner.RunApiSmokeTestAsync"/>. The smoke method itself spawns
/// a real app process and isn't unit-testable, but these helpers are pure and
/// represent the brittle parts (JSON shape variations, path templating).
/// post-run-target-app-smoke-test (2026-05-11).
/// </summary>
public class PlaywrightRunnerOpenApiHelperTests
{
    [Fact]
    public void ExtractOpenApiGetPaths_SwashbuckleShape_ReturnsAllGets()
    {
        // The shape ASP.NET Core's Swashbuckle emits — paths keyed by route, verbs nested as objects.
        const string json = """
        {
          "openapi": "3.0.1",
          "paths": {
            "/api/towers": { "get": { "summary": "list towers" } },
            "/api/towers/{id}": {
              "get": { "summary": "get tower" },
              "delete": { "summary": "delete tower" }
            },
            "/api/auth/login": { "post": { "summary": "login" } }
          }
        }
        """;

        var paths = PlaywrightRunner.ExtractOpenApiGetPaths(json);

        Assert.Equal(2, paths.Count);
        Assert.Contains("/api/towers", paths);
        Assert.Contains("/api/towers/{id}", paths);
        Assert.DoesNotContain("/api/auth/login", paths); // POST-only
    }

    [Fact]
    public void ExtractOpenApiGetPaths_NoPathsKey_ReturnsEmpty()
    {
        const string json = """{ "openapi": "3.0.1" }""";
        var paths = PlaywrightRunner.ExtractOpenApiGetPaths(json);
        Assert.Empty(paths);
    }

    [Fact]
    public void ExtractOpenApiGetPaths_EmptyPaths_ReturnsEmpty()
    {
        const string json = """{ "openapi": "3.0.1", "paths": {} }""";
        var paths = PlaywrightRunner.ExtractOpenApiGetPaths(json);
        Assert.Empty(paths);
    }

    [Fact]
    public void ExtractOpenApiGetPaths_PathWithNoVerbs_Skipped()
    {
        const string json = """
        {
          "paths": {
            "/api/empty": {},
            "/api/with-get": { "get": {} }
          }
        }
        """;
        var paths = PlaywrightRunner.ExtractOpenApiGetPaths(json);
        Assert.Single(paths);
        Assert.Equal("/api/with-get", paths[0]);
    }

    [Fact]
    public void ExtractOpenApiGetPaths_GetVerbCaseInsensitive()
    {
        const string json = """
        {
          "paths": {
            "/api/upper": { "GET": {} },
            "/api/mixed": { "Get": {} },
            "/api/lower": { "get": {} }
          }
        }
        """;
        var paths = PlaywrightRunner.ExtractOpenApiGetPaths(json);
        Assert.Equal(3, paths.Count);
    }

    [Theory]
    [InlineData("/api/towers", "http://localhost:5100/api/towers")]
    [InlineData("/api/towers/{id}", "http://localhost:5100/api/towers/1")]
    [InlineData("/api/users/{userId}/orders/{orderId}", "http://localhost:5100/api/users/1/orders/1")]
    [InlineData("/api/items/{type}", "http://localhost:5100/api/items/1")]
    public void SubstituteOpenApiPathTemplates_ReplacesPlaceholders(string template, string expected)
    {
        var baseUri = new Uri("http://localhost:5100/");
        var result = PlaywrightRunner.SubstituteOpenApiPathTemplates(baseUri, template);
        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public void SubstituteOpenApiPathTemplates_RelativePathWithoutLeadingSlash_StillWorks()
    {
        var baseUri = new Uri("http://localhost:5100/");
        var result = PlaywrightRunner.SubstituteOpenApiPathTemplates(baseUri, "api/towers");
        Assert.Equal("http://localhost:5100/api/towers", result.ToString());
    }

    [Fact]
    public void SubstituteOpenApiPathTemplates_NoPlaceholder_PassesThrough()
    {
        var baseUri = new Uri("http://localhost:5100/");
        var result = PlaywrightRunner.SubstituteOpenApiPathTemplates(baseUri, "/api/health");
        Assert.Equal("http://localhost:5100/api/health", result.ToString());
    }

    [Fact]
    public void ExtractOpenApiGetPaths_RealisticGridGuardiansShape()
    {
        // Mirror of the actual shape that would have caught the 2026-05-11
        // GridGuardians regression — 5xx on /api/config/*.
        const string json = """
        {
          "openapi": "3.0.1",
          "info": { "title": "GridGuardians.Api", "version": "v1" },
          "paths": {
            "/api/config/towers":   { "get": { "tags": ["Config"] } },
            "/api/config/enemies":  { "get": { "tags": ["Config"] } },
            "/api/config/maps":     { "get": { "tags": ["Config"] } },
            "/api/config/daily":    { "get": { "tags": ["Config"] } },
            "/api/runs":            { "post": { "tags": ["Runs"] } },
            "/api/runs/{id}":       { "get": { "tags": ["Runs"] } }
          }
        }
        """;

        var paths = PlaywrightRunner.ExtractOpenApiGetPaths(json);

        Assert.Equal(5, paths.Count);
        Assert.Contains("/api/config/towers", paths);
        Assert.Contains("/api/config/enemies", paths);
        Assert.Contains("/api/config/maps", paths);
        Assert.Contains("/api/config/daily", paths);
        Assert.Contains("/api/runs/{id}", paths);
        Assert.DoesNotContain("/api/runs", paths); // POST-only
    }
}
