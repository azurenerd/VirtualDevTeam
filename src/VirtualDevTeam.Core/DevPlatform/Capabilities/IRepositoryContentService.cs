using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Capabilities;

/// <summary>
/// File content operations on the Git repository.
/// Maps to GitHub Contents/Trees API or Azure DevOps Items/Pushes API.
/// </summary>
public interface IRepositoryContentService
{
    Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default);
    Task<byte[]?> GetFileBytesAsync(string path, string? branch = null, CancellationToken ct = default);

    Task CreateOrUpdateFileAsync(
        string path, string content, string commitMessage,
        string? branch = null, CancellationToken ct = default);

    Task DeleteFileAsync(
        string path, string commitMessage,
        string? branch = null, CancellationToken ct = default);

    /// <summary>
    /// Commit multiple files in a single atomic operation.
    /// GitHub: Git Trees API. ADO: Pushes API with multiple changes.
    /// </summary>
    Task BatchCommitFilesAsync(
        IReadOnlyList<PlatformFileCommit> files, string commitMessage,
        string branch, CancellationToken ct = default);

    /// <summary>
    /// Commit a binary file (e.g., PNG screenshot) and return the URL to access it.
    /// </summary>
    Task<string?> CommitBinaryFileAsync(
        string path, byte[] content, string commitMessage,
        string branch, CancellationToken ct = default);

    /// <summary>
    /// Upload an image and return a permanent URL for embedding in PR/issue markdown.
    /// For GitHub: uses release assets (no expiring token). For ADO: uses PR attachments API.
    /// Returns null if upload fails (callers should post text-only content).
    /// </summary>
    /// <param name="filename">Unique filename for the image.</param>
    /// <param name="content">Raw image bytes.</param>
    /// <param name="contentType">MIME type (default image/png).</param>
    /// <param name="prNumber">Optional PR number — enables PR attachment upload on ADO.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> UploadImageForCommentAsync(
        string filename, byte[] content, string contentType = "image/png",
        int? prNumber = null, CancellationToken ct = default);

    /// <summary>
    /// Get the full file tree of the repository from a branch.
    /// Returns file paths only (no directories).
    /// </summary>
    Task<IReadOnlyList<string>> GetRepositoryTreeAsync(string? branch = null, CancellationToken ct = default);

    /// <summary>
    /// Get the file tree for a specific commit SHA.
    /// </summary>
    Task<IReadOnlyList<string>> GetRepositoryTreeForCommitAsync(string commitSha, CancellationToken ct = default);
}
