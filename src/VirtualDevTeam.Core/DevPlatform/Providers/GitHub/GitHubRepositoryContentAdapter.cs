// NoMessyCodePlan Theme 3: this file is the legitimate IGitHubService adapter/registration layer.
// CS0618 is the [Obsolete] warning on IGitHubService — suppressed here because the legacy interface
// IS the bridge being wrapped. Direct agent-side use elsewhere will still emit the warning as intended.
#pragma warning disable CS0618
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;
using VirtualDevTeam.Core.GitHub;

namespace VirtualDevTeam.Core.DevPlatform.Providers.GitHub;

/// <summary>
/// Adapts <see cref="IGitHubService"/> file operations to <see cref="IRepositoryContentService"/>.
/// </summary>
public sealed class GitHubRepositoryContentAdapter : IRepositoryContentService
{
    private readonly IGitHubService _github;

    public GitHubRepositoryContentAdapter(IGitHubService github)
    {
        ArgumentNullException.ThrowIfNull(github);
        _github = github;
    }

    public Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default)
        => _github.GetFileContentAsync(path, branch, ct);

    public Task<byte[]?> GetFileBytesAsync(string path, string? branch = null, CancellationToken ct = default)
        => _github.GetFileBytesAsync(path, branch, ct);

    public Task CreateOrUpdateFileAsync(string path, string content, string commitMessage, string? branch = null, CancellationToken ct = default)
        => _github.CreateOrUpdateFileAsync(path, content, commitMessage, branch, ct);

    public Task DeleteFileAsync(string path, string commitMessage, string? branch = null, CancellationToken ct = default)
        => _github.DeleteFileAsync(path, commitMessage, branch, ct);

    public async Task BatchCommitFilesAsync(
        IReadOnlyList<PlatformFileCommit> files, string commitMessage,
        string branch, CancellationToken ct = default)
    {
        var tuples = files.Select(f => (f.Path, f.Content)).ToList();
        await _github.BatchCommitFilesAsync(tuples, commitMessage, branch, ct);
    }

    public Task<string?> CommitBinaryFileAsync(
        string path, byte[] content, string commitMessage,
        string branch, CancellationToken ct = default)
        => _github.CommitBinaryFileAsync(path, content, commitMessage, branch, ct);

    public Task<string?> UploadImageForCommentAsync(
        string filename, byte[] content, string contentType = "image/png",
        int? prNumber = null, CancellationToken ct = default)
        => _github.UploadImageAsReleaseAssetAsync(filename, content, contentType, ct);

    public async Task<IReadOnlyList<string>> GetRepositoryTreeAsync(string? branch = null, CancellationToken ct = default)
        => await _github.GetRepositoryTreeAsync(branch ?? "main", ct);

    public Task<IReadOnlyList<string>> GetRepositoryTreeForCommitAsync(string commitSha, CancellationToken ct = default)
        => _github.GetRepositoryTreeForCommitAsync(commitSha, ct);
}
