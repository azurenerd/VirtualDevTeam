using AgentSquad.Core.DevPlatform.Auth;
using AgentSquad.Core.DevPlatform.Capabilities;
using AgentSquad.Core.DevPlatform.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Azure DevOps repository content operations using Git Items + Pushes API.
/// https://learn.microsoft.com/en-us/rest/api/azure-devops/git/items
/// https://learn.microsoft.com/en-us/rest/api/azure-devops/git/pushes
/// </summary>
public sealed class AdoRepositoryContentService : AdoHttpClientBase, IRepositoryContentService
{
    private readonly ILogger<AdoRepositoryContentService> _logger;

    public AdoRepositoryContentService(
        HttpClient http,
        IDevPlatformAuthProvider authProvider,
        IOptions<Configuration.AgentSquadConfig> config,
        ILogger<AdoRepositoryContentService> logger)
        : base(http, authProvider, config, logger)
    {
        _logger = logger;
    }

    public async Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var url = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/items",
            $"path={Uri.EscapeDataString(normalizedPath)}&includeContent=true&$format=json" +
            (branch is not null ? $"&versionDescriptor.version={Uri.EscapeDataString(branch)}&versionDescriptor.versionType=branch" : ""));

        var item = await GetAsync<AdoGitItem>(url, ct, suppressNotFound: true);
        return item?.Content;
    }

    public async Task<byte[]?> GetFileBytesAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var url = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/items",
            $"path={Uri.EscapeDataString(normalizedPath)}&$format=octetStream" +
            (branch is not null ? $"&versionDescriptor.version={Uri.EscapeDataString(branch)}&versionDescriptor.versionType=branch" : ""));

        try
        {
            var response = await SendRawAsync(HttpMethod.Get, url, null!, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task CreateOrUpdateFileAsync(
        string path, string content, string commitMessage, string? branch = null, CancellationToken ct = default)
    {
        branch ??= "main";
        var normalizedPath = NormalizePath(path);

        // Get current branch ref to find old object ID
        var refUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs",
            $"filter=heads/{branch}");
        var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(refUrl, ct);
        var currentRef = refs?.Value.FirstOrDefault();
        var oldObjectId = currentRef?.ObjectId ?? new string('0', 40);

        // Check if file already exists to determine change type
        var existing = await GetFileContentAsync(normalizedPath, branch, ct);
        var changeType = existing is not null ? "edit" : "add";

        var push = new AdoGitPushRequest
        {
            RefUpdates = [new AdoGitRefUpdate { Name = $"refs/heads/{branch}", OldObjectId = oldObjectId }],
            Commits =
            [
                new AdoGitCommit
                {
                    Comment = commitMessage,
                    Changes =
                    [
                        new AdoGitChange
                        {
                            ChangeType = changeType,
                            Item = new AdoGitItemDescriptor { Path = normalizedPath },
                            NewContent = new AdoGitNewContent { Content = content, ContentType = "rawtext" }
                        }
                    ]
                }
            ]
        };

        var pushUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pushes");
        await PushWithConflictRetryAsync(push, branch, ct);
        _logger.LogInformation("Committed file {Path} to branch {Branch}", normalizedPath, branch);
    }

    public async Task DeleteFileAsync(string path, string commitMessage, string? branch = null, CancellationToken ct = default)
    {
        branch ??= "main";
        var normalizedPath = NormalizePath(path);

        var refUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs", $"filter=heads/{branch}");
        var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(refUrl, ct);
        var oldObjectId = refs?.Value.FirstOrDefault()?.ObjectId ?? new string('0', 40);

        var push = new AdoGitPushRequest
        {
            RefUpdates = [new AdoGitRefUpdate { Name = $"refs/heads/{branch}", OldObjectId = oldObjectId }],
            Commits =
            [
                new AdoGitCommit
                {
                    Comment = commitMessage,
                    Changes =
                    [
                        new AdoGitChange
                        {
                            ChangeType = "delete",
                            Item = new AdoGitItemDescriptor { Path = normalizedPath }
                        }
                    ]
                }
            ]
        };

        var pushUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pushes");
        await PushWithConflictRetryAsync(push, branch, ct);
    }

    public async Task BatchCommitFilesAsync(
        IReadOnlyList<PlatformFileCommit> files, string commitMessage, string branch, CancellationToken ct = default)
    {
        var refUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs", $"filter=heads/{branch}");
        var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(refUrl, ct);
        var oldObjectId = refs?.Value.FirstOrDefault()?.ObjectId ?? new string('0', 40);

        var changes = new List<AdoGitChange>();
        foreach (var file in files)
        {
            var normalizedPath = NormalizePath(file.Path);
            var existing = await GetFileContentAsync(normalizedPath, branch, ct);
            changes.Add(new AdoGitChange
            {
                ChangeType = existing is not null ? "edit" : "add",
                Item = new AdoGitItemDescriptor { Path = normalizedPath },
                NewContent = new AdoGitNewContent { Content = file.Content, ContentType = "rawtext" }
            });
        }

        var push = new AdoGitPushRequest
        {
            RefUpdates = [new AdoGitRefUpdate { Name = $"refs/heads/{branch}", OldObjectId = oldObjectId }],
            Commits = [new AdoGitCommit { Comment = commitMessage, Changes = changes }]
        };

        var pushUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pushes");
        await PushWithConflictRetryAsync(push, branch, ct);
        _logger.LogInformation("Batch committed {Count} files to branch {Branch}", files.Count, branch);
    }

    public async Task<string?> CommitBinaryFileAsync(
        string path, byte[] content, string commitMessage, string branch, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        var refUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs", $"filter=heads/{branch}");
        var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(refUrl, ct);
        var oldObjectId = refs?.Value.FirstOrDefault()?.ObjectId ?? new string('0', 40);

        var existing = await GetFileBytesAsync(normalizedPath, branch, ct);

        var push = new AdoGitPushRequest
        {
            RefUpdates = [new AdoGitRefUpdate { Name = $"refs/heads/{branch}", OldObjectId = oldObjectId }],
            Commits =
            [
                new AdoGitCommit
                {
                    Comment = commitMessage,
                    Changes =
                    [
                        new AdoGitChange
                        {
                            ChangeType = existing is not null ? "edit" : "add",
                            Item = new AdoGitItemDescriptor { Path = normalizedPath },
                            NewContent = new AdoGitNewContent
                            {
                                Content = Convert.ToBase64String(content),
                                ContentType = "base64encoded"
                            }
                        }
                    ]
                }
            ]
        };

        var pushUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pushes");
        await PushWithConflictRetryAsync(push, branch, ct);

        // Return the raw file URL
        return $"https://dev.azure.com/{Organization}/{Project}/_apis/git/repositories/{Repository}/items?path={Uri.EscapeDataString(normalizedPath)}&versionDescriptor.version={Uri.EscapeDataString(branch)}&versionDescriptor.versionType=branch&api-version=7.1";
    }

    public async Task<IReadOnlyList<string>> GetRepositoryTreeAsync(string? branch = null, CancellationToken ct = default)
    {
        var url = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/items",
            $"recursionLevel=Full&includeContentMetadata=true" +
            (branch is not null ? $"&versionDescriptor.version={Uri.EscapeDataString(branch)}&versionDescriptor.versionType=branch" : ""));

        var response = await GetAsync<AdoListResponse<AdoGitItem>>(url, ct);
        return response?.Value
            .Where(i => i.GitObjectType == "blob")
            .Select(i => i.Path)
            .ToList() ?? new List<string>();
    }

    public async Task<IReadOnlyList<string>> GetRepositoryTreeForCommitAsync(string commitSha, CancellationToken ct = default)
    {
        var url = BuildUrl(
            $"{Project}/_apis/git/repositories/{Repository}/items",
            $"recursionLevel=Full&includeContentMetadata=true&versionDescriptor.version={Uri.EscapeDataString(commitSha)}&versionDescriptor.versionType=commit");

        var response = await GetAsync<AdoListResponse<AdoGitItem>>(url, ct);
        return response?.Value
            .Where(i => i.GitObjectType == "blob")
            .Select(i => i.Path)
            .ToList() ?? new List<string>();
    }

    /// <summary>
    /// Push with automatic retry on 409 Conflict (stale oldObjectId).
    /// Re-fetches the latest branch ref and retries the push up to 3 times.
    /// </summary>
    private async Task PushWithConflictRetryAsync(
        AdoGitPushRequest push, string branch, CancellationToken ct, int maxRetries = 3)
    {
        var pushUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/pushes");

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                await PostAsync<object>(pushUrl, push, ct);
                return; // Success
            }
            catch (HttpRequestException ex) when (
                attempt < maxRetries &&
                (ex.StatusCode == System.Net.HttpStatusCode.Conflict || ex.Message.Contains("409")))
            {
                _logger.LogWarning(
                    "Push to branch {Branch} got 409 Conflict (attempt {Attempt}/{Max}), re-fetching ref and retrying",
                    branch, attempt + 1, maxRetries + 1);

                // Small backoff before retry
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct);

                // Re-fetch the latest ref
                var refUrl = BuildUrl($"{Project}/_apis/git/repositories/{Repository}/refs", $"filter=heads/{branch}");
                var refs = await GetAsync<AdoListResponse<AdoGitRefResponse>>(refUrl, ct);
                var newObjectId = refs?.Value.FirstOrDefault()?.ObjectId ?? new string('0', 40);

                // Update the push request with the new objectId
                if (push.RefUpdates.Count > 0)
                    push.RefUpdates[0].OldObjectId = newObjectId;
            }
        }
    }

    private static string NormalizePath(string path)
    {
        if (!path.StartsWith('/'))
            path = "/" + path;
        return path;
    }
}
