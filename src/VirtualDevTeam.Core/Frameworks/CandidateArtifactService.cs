using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Frameworks;

/// <summary>
/// Serves files from active candidate worktrees (the dirs under
/// <c>{workspace}/{agent}/{repo}/.candidates/specialist-{taskId}/{strategyId}/</c>) for the
/// dashboard's frameworks-artifact-clickable-preview + eval-horizontal-artifact-viewer features.
///
/// <para>
/// Operator workflow: <see cref="CandidateArtifactWatcher"/> emits
/// <c>FrameworkActivityEvent("artifact", "...", { "abspath": "C:\\...\\file.png", ... })</c>
/// during a candidate run. The Strategies page renders those entries with click-to-popup
/// thumbnails. Each render of a thumbnail calls back to the dashboard endpoint
/// <c>/api/dashboard/candidate-artifact?token={Base64Url}</c> which round-trips through
/// <see cref="ResolveAndOpenAsync"/> here, validates the path is inside an allowed root,
/// and streams the file bytes back.
/// </para>
///
/// <para>
/// Safety contract:
/// <list type="bullet">
///   <item>Tokens encode the absolute path. We canonicalize via <see cref="Path.GetFullPath(string)"/>
///         and require the result to start with one of the allowed roots
///         (<c>WorkspaceConfig.RootPath</c> + <c>.candidates</c> /
///         <c>strategy-artifacts</c> sub-trees).</item>
///   <item>Path traversal (<c>..</c>) is neutralized by canonicalization but explicitly
///         rejected pre-canonicalization too as a defense-in-depth.</item>
///   <item>File size is bounded — files larger than <see cref="MaxBytes"/> are 413.</item>
///   <item>HMAC isn't required (the path itself + the same-origin browser context is the auth
///         boundary; the dashboard is operator-only access). If we ever expose this endpoint
///         publicly we should add HMAC signing.</item>
/// </list>
/// </para>
/// </summary>
public sealed class CandidateArtifactService
{
    public const long MaxBytes = 50L * 1024 * 1024; // 50 MB max per artifact

    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly ILogger<CandidateArtifactService> _logger;

    public CandidateArtifactService(
        IOptionsMonitor<VirtualDevTeamConfig> config,
        ILogger<CandidateArtifactService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Token format: base64url(absolute path UTF-8). Reverse via <see cref="Decode"/>.</summary>
    public static string Encode(string absolutePath)
    {
        var bytes = Encoding.UTF8.GetBytes(absolutePath);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static string? Decode(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var b64 = token.Replace('-', '+').Replace('_', '/');
            var pad = (4 - b64.Length % 4) % 4;
            if (pad > 0) b64 += new string('=', pad);
            var bytes = Convert.FromBase64String(b64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolves a candidate-artifact token to a file path safe to serve. Returns null if
    /// the token is malformed, the path is outside allowed roots, or the file doesn't exist.
    /// </summary>
    public CandidateArtifactResolution? Resolve(string token)
    {
        var raw = Decode(token);
        if (string.IsNullOrEmpty(raw)) return null;

        // Defense-in-depth: explicit pre-canonicalization rejection of path traversal.
        if (raw.Contains("..", StringComparison.Ordinal))
        {
            _logger.LogWarning("CandidateArtifactService rejected path with '..': {Path}", raw);
            return null;
        }

        string canonical;
        try { canonical = Path.GetFullPath(raw); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CandidateArtifactService failed to canonicalize: {Path}", raw);
            return null;
        }

        if (!File.Exists(canonical))
        {
            _logger.LogDebug("CandidateArtifactService: file not found: {Path}", canonical);
            return null;
        }

        if (!IsInsideAllowedRoot(canonical))
        {
            _logger.LogWarning(
                "CandidateArtifactService rejected path outside allowed roots: {Path}",
                canonical);
            return null;
        }

        var fi = new FileInfo(canonical);
        if (fi.Length > MaxBytes)
        {
            _logger.LogWarning(
                "CandidateArtifactService rejected over-sized file ({Size} bytes > {Max}): {Path}",
                fi.Length, MaxBytes, canonical);
            return null;
        }

        return new CandidateArtifactResolution(canonical, fi.Length, GuessContentType(canonical));
    }

    /// <summary>
    /// Path is allowed when it lives under the configured workspace root AND falls inside
    /// either a <c>.candidates/</c> sub-tree (live candidate worktrees) or the
    /// <c>strategy-artifacts/</c> tree (durable post-evaluation copies). Anything else
    /// (e.g. the agent's main repo clone, scratch dirs, workspace root files) is refused.
    /// </summary>
    private bool IsInsideAllowedRoot(string canonicalPath)
    {
        var cfg = _config.CurrentValue;
        var workspaceRoot = cfg?.Workspace?.RootPath;
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return false;

        string fullRoot;
        try { fullRoot = Path.GetFullPath(workspaceRoot); }
        catch { return false; }

        if (!canonicalPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        var sep = Path.DirectorySeparatorChar;
        var afterRoot = canonicalPath[fullRoot.Length..];
        // Require at least one of these markers somewhere in the suffix.
        // .candidates → live worktrees from CandidateArtifactWatcher
        // strategy-artifacts → durable post-evaluation artifact dir from CandidateEvaluator
        var allowedMarkers = new[]
        {
            $"{sep}.candidates{sep}",
            $"{sep}strategy-artifacts{sep}",
        };
        foreach (var marker in allowedMarkers)
        {
            if (afterRoot.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".txt" or ".md" or ".log" => "text/plain",
            _ => "application/octet-stream",
        };
    }
}

public sealed record CandidateArtifactResolution(string FullPath, long SizeBytes, string ContentType);
