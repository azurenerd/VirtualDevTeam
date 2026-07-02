using System.Text;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;
using VirtualDevTeam.Core.DevPlatform.Models;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// <see cref="IRepositoryContentService"/> for the local platform. Reads and writes
/// files in the local bare repo via temporary worktree checkouts.
/// </summary>
public sealed class LocalRepositoryContentService : IRepositoryContentService
{
    private readonly LocalPlatformContext _ctx;
    private readonly ILogger<LocalRepositoryContentService> _logger;

    public LocalRepositoryContentService(LocalPlatformContext ctx, ILogger<LocalRepositoryContentService> logger)
    {
        _ctx = ctx;
        _logger = logger;
    }

    public async Task<string?> GetFileContentAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        if (_ctx.BareRepo.BareRepoPath is null) return null;
        var refSpec = branch ?? _ctx.DefaultBranch;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"show {refSpec}:{path}")
            {
                WorkingDirectory = _ctx.BareRepo.BareRepoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["GIT_DIR"] = _ctx.BareRepo.BareRepoPath!;
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var contentTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            var content = await contentTask;
            _ = await stderrTask;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 ? content : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<byte[]?> GetFileBytesAsync(string path, string? branch = null, CancellationToken ct = default)
    {
        if (_ctx.BareRepo.BareRepoPath is null) return null;
        var refSpec = branch ?? _ctx.DefaultBranch;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"show {refSpec}:{path}")
            {
                WorkingDirectory = _ctx.BareRepo.BareRepoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["GIT_DIR"] = _ctx.BareRepo.BareRepoPath!;
            using var proc = System.Diagnostics.Process.Start(psi)!;
            using var ms = new MemoryStream();
            await proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task CreateOrUpdateFileAsync(
        string path, string content, string commitMessage,
        string? branch = null, CancellationToken ct = default)
    {
        var targetBranch = branch ?? _ctx.DefaultBranch;
        await CommitFileAsync(path, content, commitMessage, targetBranch, ct);
    }

    public async Task DeleteFileAsync(
        string path, string commitMessage,
        string? branch = null, CancellationToken ct = default)
    {
        if (_ctx.BareRepo.BareRepoPath is null) return;
        var targetBranch = branch ?? _ctx.DefaultBranch;

        var tempWorktree = Path.Combine(Path.GetTempPath(), $"vdt-content-{Guid.NewGuid():N}");
        try
        {
            await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree add \"{tempWorktree}\" {targetBranch}", ct);
            var filePath = Path.Combine(tempWorktree, path);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                await RunGitAsync(tempWorktree, "add -A", ct);
                await RunGitAsync(tempWorktree, $"commit -m \"{commitMessage.Replace("\"", "\\\"")}\"", ct);
            }
        }
        finally
        {
            try { await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree remove \"{tempWorktree}\" --force", CancellationToken.None); } catch { }
        }
    }

    public async Task BatchCommitFilesAsync(
        IReadOnlyList<PlatformFileCommit> files, string commitMessage,
        string branch, CancellationToken ct = default)
    {
        if (_ctx.BareRepo.BareRepoPath is null || files.Count == 0) return;

        var tempWorktree = Path.Combine(Path.GetTempPath(), $"vdt-batch-{Guid.NewGuid():N}");
        try
        {
            await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree add \"{tempWorktree}\" {branch}", ct);

            foreach (var file in files)
            {
                ValidateNoPathTraversal(tempWorktree, file.Path);
                var filePath = Path.Combine(tempWorktree, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await File.WriteAllTextAsync(filePath, file.Content, ct);
            }

            await RunGitAsync(tempWorktree, "add -A", ct);
            await RunGitAsync(tempWorktree, $"commit -m \"{commitMessage.Replace("\"", "\\\"")}\"", ct);

            _logger.LogDebug("Batch committed {Count} files to {Branch}", files.Count, branch);
        }
        finally
        {
            try { await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree remove \"{tempWorktree}\" --force", CancellationToken.None); } catch { }
        }
    }

    public async Task<string?> CommitBinaryFileAsync(
        string path, byte[] content, string commitMessage,
        string branch, CancellationToken ct = default)
    {
        if (_ctx.BareRepo.BareRepoPath is null) return null;

        var tempWorktree = Path.Combine(Path.GetTempPath(), $"vdt-binary-{Guid.NewGuid():N}");
        try
        {
            await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree add \"{tempWorktree}\" {branch}", ct);

            ValidateNoPathTraversal(tempWorktree, path);
            var filePath = Path.Combine(tempWorktree, path);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllBytesAsync(filePath, content, ct);
            await RunGitAsync(tempWorktree, "add -A", ct);
            await RunGitAsync(tempWorktree, $"commit -m \"{commitMessage.Replace("\"", "\\\"")}\"", ct);
            
            var sha = await _ctx.BareRepo.GetBranchHeadAsync(branch, ct);
            
            _logger.LogDebug("Binary file committed: {Path}", path);
            return sha;
        }
        catch (Exception) when (CleanupWorktreeOnError(_ctx.BareRepo.BareRepoPath, tempWorktree))
        {
            // Never reached — CleanupWorktreeOnError always returns false
            return null;
        }
        finally
        {
            try { await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree remove \"{tempWorktree}\" --force", CancellationToken.None); } catch { }
        }
    }

    public async Task<string?> UploadImageForCommentAsync(
        string filename, byte[] content, string contentType = "image/png",
        int? prNumber = null, CancellationToken ct = default)
    {
        await Task.Delay(0, ct);
        return null;
    }

    public async Task<IReadOnlyList<string>> GetRepositoryTreeAsync(string? branch = null, CancellationToken ct = default)
    {
        if (_ctx.BareRepo.BareRepoPath is null) return new List<string>();
        var refSpec = branch ?? _ctx.DefaultBranch;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"ls-tree -r --name-only {refSpec}")
            {
                WorkingDirectory = _ctx.BareRepo.BareRepoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["GIT_DIR"] = _ctx.BareRepo.BareRepoPath!;
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            var output = await outputTask;
            _ = await stderrTask;
            await proc.WaitForExitAsync(ct);
            
            if (proc.ExitCode != 0) return new List<string>();
            
            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public async Task<IReadOnlyList<string>> GetRepositoryTreeForCommitAsync(string commitSha, CancellationToken ct = default)
    {
        if (_ctx.BareRepo.BareRepoPath is null) return new List<string>();

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"ls-tree -r --name-only {commitSha}")
            {
                WorkingDirectory = _ctx.BareRepo.BareRepoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["GIT_DIR"] = _ctx.BareRepo.BareRepoPath!;
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var outputTask2 = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask2 = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            var output = await outputTask2;
            _ = await stderrTask2;
            await proc.WaitForExitAsync(ct);
            
            if (proc.ExitCode != 0) return new List<string>();
            
            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private async Task CommitFileAsync(string path, string content, string commitMessage, string branch, CancellationToken ct)
    {
        if (_ctx.BareRepo.BareRepoPath is null) return;

        var tempWorktree = Path.Combine(Path.GetTempPath(), $"vdt-commit-{Guid.NewGuid():N}");
        try
        {
            await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree add \"{tempWorktree}\" {branch}", ct);

            ValidateNoPathTraversal(tempWorktree, path);
            var filePath = Path.Combine(tempWorktree, path);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, content, ct);
            await RunGitAsync(tempWorktree, "add -A", ct);
            await RunGitAsync(tempWorktree, $"commit -m \"{commitMessage.Replace("\"", "\\\"")}\"", ct);
        }
        finally
        {
            try { await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree remove \"{tempWorktree}\" --force", CancellationToken.None); } catch { }
        }
    }

    private static void ValidateNoPathTraversal(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path traversal detected: {relativePath}");
    }

    /// <summary>Dummy filter method that always returns false — used only to attach a finally-like cleanup to a catch-when clause.</summary>
    private static bool CleanupWorktreeOnError(string bareRepoPath, string tempWorktree) => false;

    private static async Task RunGitAsync(string workDir, string args, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Allow bare repo operations (safe.bareRepository=explicit)
        if (workDir.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            psi.Environment["GIT_DIR"] = workDir;

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            // "nothing to commit" is expected when file content is unchanged
            if (args.StartsWith("commit", StringComparison.Ordinal)
                && (stdout.Contains("nothing to commit") || stderr.Contains("nothing to commit")))
                return;
            throw new InvalidOperationException($"git {args} failed (exit {proc.ExitCode}): {stderr}{stdout}");
        }
    }
}
