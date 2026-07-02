using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;

namespace VirtualDevTeam.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// Adapts Octokit repository management to <see cref="IRepositoryManagementService"/>.
/// </summary>
public sealed class GitHubRepositoryManagementAdapter : IRepositoryManagementService
{
    private readonly IGitHubClient _client;
    private readonly ILogger<GitHubRepositoryManagementAdapter> _logger;

    public GitHubRepositoryManagementAdapter(IOptions<VirtualDevTeamConfig> config, ILogger<GitHubRepositoryManagementAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var token = config.Value.Project?.GitHubToken ?? "";
        var client = new GitHubClient(new ProductHeaderValue("VirtualDevTeam"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.Credentials = new Credentials(token);
        }
        else
        {
            _logger.LogWarning("GitHubRepositoryManagementAdapter initialized without a token. Operations will fail until a token is configured.");
        }
        _client = client;
    }

    public async Task<RepositoryCreationResult> CreateRepositoryAsync(string name, bool isPrivate = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        try
        {
            var newRepo = new NewRepository(name) { Private = isPrivate };
            var repo = await _client.Repository.Create(newRepo);

            _logger.LogInformation("Created GitHub repository {RepoName} (private={IsPrivate})", name, isPrivate);
            return new RepositoryCreationResult(true, repo.HtmlUrl, null);
        }
        catch (ApiException ex)
        {
            _logger.LogError(ex, "Failed to create GitHub repository {RepoName}", name);
            return new RepositoryCreationResult(false, null, ex.Message);
        }
    }
}
