using System.Text;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.DevPlatform.Capabilities;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// <see cref="IBranchService"/> wrapping the local bare git repository.
/// Branch operations go directly to the bare repo via git commands.
/// </summary>
public sealed class LocalBranchService : IBranchService
{
    private readonly LocalPlatformContext _ctx;
    private readonly ILogger<LocalBranchService> _logger;

    public LocalBranchService(LocalPlatformContext ctx, ILogger<LocalBranchService> logger)
    {
        _ctx = ctx;
        _logger = logger;
    }

    public async Task CreateAsync(string branchName, string? fromBranch = null, CancellationToken ct = default)
    {
        // Force context initialization (bare repo + DB) if not yet done
        using (_ctx.CreateConnection()) { }

        if (_ctx.BareRepo.BareRepoPath is null)
            throw new InvalidOperationException("Bare repo not initialized");

        var fromRef = fromBranch ?? _ctx.DefaultBranch;
        await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"branch {branchName} {fromRef}", ct);

        var sha = await _ctx.BareRepo.GetBranchHeadAsync(branchName, ct);
        _logger.LogDebug("Created local branch {Branch} from {Ref} at {Sha}", branchName, fromRef, sha);
    }

    public async Task<bool> ExistsAsync(string branchName, CancellationToken ct = default)
    {
        using (_ctx.CreateConnection()) { } // Force init
        var branches = await _ctx.BareRepo.ListBranchesAsync(ct);
        return branches.Contains(branchName);
    }

    public async Task DeleteAsync(string branchName, CancellationToken ct = default)
    {
        await _ctx.BareRepo.DeleteBranchAsync(branchName, ct);
        _logger.LogDebug("Deleted local branch {Branch}", branchName);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string? prefix = null, CancellationToken ct = default)
    {
        var branches = await _ctx.BareRepo.ListBranchesAsync(ct);
        if (prefix is null) return branches;
        return branches.Where(b => b.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    public async Task CleanToBaselineAsync(
        IReadOnlyList<string> preserveFiles, string commitMessage,
        string? branch = null, CancellationToken ct = default)
    {
        if (_ctx.BareRepo.BareRepoPath is null)
            throw new InvalidOperationException("Bare repo not initialized");

        var targetBranch = branch ?? _ctx.DefaultBranch;
        
        var tempWorktree = Path.Combine(Path.GetTempPath(), $"vdt-clean-{Guid.NewGuid():N}");
        try
        {
            await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree add \"{tempWorktree}\" {targetBranch}", ct);
            
            // Get all files in the repo
            var di = new DirectoryInfo(tempWorktree);
            var allFiles = di.EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains(".git"))
                .ToList();

            // Delete files not in preserve list
            foreach (var file in allFiles)
            {
                var relPath = Path.GetRelativePath(tempWorktree, file.FullName);
                if (!preserveFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase))
                {
                    file.Delete();
                }
            }

            // Clean up empty directories
            foreach (var dir in di.EnumerateDirectories("*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.FullName.Length))
            {
                try
                {
                    if (dir.GetDirectories().Length == 0 && dir.GetFiles().Length == 0)
                        dir.Delete();
                }
                catch
                {
                    // Ignore errors on cleanup
                }
            }

            await RunGitAsync(tempWorktree, "add -A", ct);
            await RunGitAsync(tempWorktree, $"commit -m \"{commitMessage.Replace("\"", "\\\"")}\"", ct);
        }
        finally
        {
            try { await RunGitAsync(_ctx.BareRepo.BareRepoPath, $"worktree remove \"{tempWorktree}\" --force", CancellationToken.None); } catch { }
        }
    }

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
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(CancellationToken.None);
            throw new InvalidOperationException($"git {args} failed (exit {proc.ExitCode}): {stderr}");
        }
    }
}
