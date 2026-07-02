using VirtualDevTeam.Core.Preview;

namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Public-facing DTO for test artifacts served to the Dashboard Testing page API.
/// Maps from the internal <see cref="TestArtifactEntry"/> without exposing full filesystem paths.
/// </summary>
public record TestArtifact
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required string AgentName { get; init; }
    public required TestArtifactType Type { get; init; }
    public int? PrNumber { get; init; }
    public DateTime CreatedAt { get; init; }
    public long FileSizeBytes { get; init; }
}

/// <summary>
/// Extension methods to register the test artifact services and map API endpoints
/// for the Dashboard Testing page.
/// </summary>
public static class TestArtifactEndpoints
{
    /// <summary>
    /// Registers the <see cref="TestArtifactIndexService"/> singleton and its dependencies
    /// for the Dashboard standalone host.
    /// </summary>
    public static IServiceCollection AddTestArtifactServices(this IServiceCollection services)
    {
        services.AddSingleton<PreviewBuildService>();
        services.AddSingleton<TestArtifactIndexService>();
        return services;
    }

    /// <summary>
    /// Maps the /api/testing/artifacts endpoints for browsing and serving test artifacts.
    /// </summary>
    public static WebApplication MapTestArtifactEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/testing").WithTags("Testing");

        group.MapGet("/artifacts", (TestArtifactIndexService svc,
            string? pr, string? agent, bool? refresh) =>
        {
            var entries = svc.GetArtifacts(forceRefresh: refresh == true);

            if (!string.IsNullOrWhiteSpace(pr))
                entries = svc.GetArtifactsByPR(pr);
            else if (!string.IsNullOrWhiteSpace(agent))
                entries = svc.GetArtifactsByAgent(agent);

            var artifacts = entries.Select(MapToDto).ToList();
            return Results.Ok(artifacts);
        });

        group.MapGet("/artifacts/{id}/content", (string id, TestArtifactIndexService svc) =>
        {
            var entry = svc.GetArtifactById(id);
            if (entry is null || !File.Exists(entry.FullPath))
                return Results.NotFound();

            var contentType = entry.Type switch
            {
                TestArtifactType.Screenshot => GetImageContentType(entry.FileName),
                TestArtifactType.Video => GetVideoContentType(entry.FileName),
                TestArtifactType.Trace => "application/zip",
                _ => "application/octet-stream"
            };

            return Results.File(entry.FullPath, contentType, entry.FileName);
        });

        return app;
    }

    private static TestArtifact MapToDto(TestArtifactEntry entry)
    {
        int? prNumber = entry.PrNumber is not null && int.TryParse(entry.PrNumber, out var n) ? n : null;

        return new TestArtifact
        {
            Id = entry.Id,
            FileName = entry.FileName,
            FilePath = entry.ApiPath,
            AgentName = FormatAgentName(entry.AgentName),
            Type = (TestArtifactType)entry.Type,
            PrNumber = prNumber,
            CreatedAt = entry.CapturedAtUtc,
            FileSizeBytes = entry.FileSizeBytes
        };
    }

    /// <summary>
    /// Converts workspace directory names (e.g., "testengineer-abc123") to display names ("Test Engineer").
    /// </summary>
    private static string FormatAgentName(string rawName)
    {
        // Strip trailing hash/id suffixes (e.g., "testengineer-abc123" → "testengineer")
        var dashIdx = rawName.LastIndexOf('-');
        var baseName = dashIdx > 0 && rawName[(dashIdx + 1)..].All(char.IsLetterOrDigit)
            ? rawName[..dashIdx]
            : rawName;

        // Insert spaces before uppercase letters or between known word boundaries
        return baseName switch
        {
            var s when s.StartsWith("testengineer", StringComparison.OrdinalIgnoreCase) => "Test Engineer",
            var s when s.StartsWith("softwareengineer", StringComparison.OrdinalIgnoreCase) => "Software Engineer",
            var s when s.StartsWith("architect", StringComparison.OrdinalIgnoreCase) => "Architect",
            var s when s.StartsWith("researcher", StringComparison.OrdinalIgnoreCase) => "Researcher",
            var s when s.StartsWith("pm", StringComparison.OrdinalIgnoreCase) => "PM",
            _ => baseName
        };
    }

    private static string GetImageContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png"
    };

    private static string GetVideoContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => "video/webm"
    };
}
