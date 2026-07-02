using System.Text.Json.Serialization;
using VirtualDevTeam.Core.DevPlatform.Auth;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps repository management using the Git Repositories REST API.
/// https://learn.microsoft.com/en-us/rest/api/azure-devops/git/repositories/create
/// </summary>
public sealed class AdoRepositoryManagementService : AdoHttpClientBase, IRepositoryManagementService
{
    private readonly ILogger<AdoRepositoryManagementService> _logger;

    public AdoRepositoryManagementService(
        HttpClient http,
        IDevPlatformAuthProvider authProvider,
        IOptions<Configuration.VirtualDevTeamConfig> config,
        ILogger<AdoRepositoryManagementService> logger)
        : base(http, authProvider, config, logger)
    {
        _logger = logger;
    }

    public async Task<RepositoryCreationResult> CreateRepositoryAsync(string name, bool isPrivate = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        try
        {
            // Fetch project ID
            var projectUrl = BuildUrl($"_apis/projects/{Uri.EscapeDataString(Project)}");
            var project = await GetAsync<AdoProjectResponse>(projectUrl, ct);
            if (project is null)
                return new RepositoryCreationResult(false, null, $"Could not resolve ADO project '{Project}'");

            var repoUrl = BuildUrl($"{Project}/_apis/git/repositories");
            var body = new AdoCreateRepoRequest
            {
                Name = name,
                Project = new AdoProjectRef { Id = project.Id }
            };

            var repo = await PostAsync<AdoRepoResponse>(repoUrl, body, ct);
            var webUrl = repo?.WebUrl;

            _logger.LogInformation("Created ADO repository {RepoName} in project {Project}", name, Project);
            return new RepositoryCreationResult(true, webUrl, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to create ADO repository {RepoName}", name);
            return new RepositoryCreationResult(false, null, ex.Message);
        }
    }

    // Internal DTOs for this endpoint only
    private record AdoProjectResponse
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
    }

    private record AdoCreateRepoRequest
    {
        public string Name { get; init; } = "";
        public AdoProjectRef Project { get; init; } = new();
    }

    private record AdoProjectRef
    {
        public string Id { get; init; } = "";
    }

    private record AdoRepoResponse
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        [JsonPropertyName("webUrl")]
        public string? WebUrl { get; init; }
    }
}
