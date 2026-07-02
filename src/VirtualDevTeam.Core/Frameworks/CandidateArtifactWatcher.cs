using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Frameworks;

/// <summary>
/// Polls a candidate worktree for newly-created or modified files (especially
/// generated assets like PNGs, JSONs, manifests) and emits a
/// <see cref="FrameworkActivityEvent"/> for each one as it appears. Lets the
/// operator watch image-gen tasks land artifacts in real time on the Frameworks
/// dashboard instead of waiting for the post-execution patch summary.
///
/// <para>
/// Usage: instantiate, call <see cref="Start"/> with the worktree path, sink,
/// and cancellation token. Dispose (or cancel the token) when the framework
/// finishes — the watcher then emits one final summary event with totals.
/// </para>
///
/// <para>
/// Polling-based (5s default interval) rather than FileSystemWatcher because
/// FSW on Windows under a worktree with many subprocess writers is unreliable
/// (event coalescing, missed events on large bursts). Polling once every few
/// seconds is sufficient for image-gen at ~30s/image cadence and zero risk of
/// missing events. Excludes scaffolding paths (.git, .squad, .copilot, .sandbox,
/// node_modules, bin, obj) so the operator only sees real candidate output.
/// </para>
/// </summary>
public sealed class CandidateArtifactWatcher : IAsyncDisposable
{
    private static readonly HashSet<string> _excludedTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".squad", ".copilot", ".claude", ".sandbox", ".vs",
        "node_modules", "bin", "obj", "TestResults", "test-results",
    };

    private static readonly HashSet<string> _interestingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".mp4", ".webm",
        ".json", ".md", ".csv", ".html", ".css", ".js", ".ts", ".tsx",
        ".cs", ".py", ".sh", ".ps1", ".yaml", ".yml", ".toml", ".xml",
    };

    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _worktreePath;
    private IProgress<FrameworkActivityEvent>? _sink;
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.OrdinalIgnoreCase);
    private int _emittedCount;

    public CandidateArtifactWatcher(ILogger logger, TimeSpan? pollInterval = null)
    {
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Starts polling. Safe to call only once per instance — subsequent calls are no-ops.
    /// </summary>
    public void Start(string worktreePath, IProgress<FrameworkActivityEvent>? sink, CancellationToken parentCt)
    {
        if (_loop is not null) return;
        if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
        {
            _logger.LogDebug(
                "CandidateArtifactWatcher: worktree {Path} doesn't exist — skipping",
                worktreePath);
            return;
        }
        _worktreePath = worktreePath;
        _sink = sink;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        // Snapshot initial state so we don't spam events for the worktree's
        // pre-existing base files (the framework's setup commit).
        try
        {
            foreach (var f in EnumerateInteresting(worktreePath))
                _seen[f.FullName] = f.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CandidateArtifactWatcher initial snapshot failed");
        }
        _loop = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException) { return; }

            if (_worktreePath is null) return;
            try
            {
                foreach (var f in EnumerateInteresting(_worktreePath))
                {
                    if (_seen.TryGetValue(f.FullName, out var prev) && prev >= f.LastWriteTimeUtc)
                        continue;
                    _seen[f.FullName] = f.LastWriteTimeUtc;

                    var rel = Path.GetRelativePath(_worktreePath, f.FullName).Replace('\\', '/');
                    var sizeKb = f.Length / 1024d;
                    var ext = f.Extension?.ToLowerInvariant() ?? "";
                    var kind = IsImageExtension(ext) ? "image"
                             : (ext is ".mp4" or ".webm" or ".mov" or ".mkv" or ".avi") ? "video"
                             : (ext is ".mp3" or ".wav" or ".ogg" or ".m4a" or ".flac") ? "audio"
                             : "file";
                    var icon = kind switch { "image" => "🎨", "video" => "🎬", "audio" => "🔊", _ => "📄" };
                    var msg = $"{icon} {rel} ({sizeKb:F1} KB)";
                    var meta = new Dictionary<string, object>
                    {
                        // 2026-05-12 (frameworks-artifact-clickable-preview): include absolute path +
                        // structured fields so the dashboard can build click-to-popup links and serve
                        // hover-thumbnails via the candidate-artifact endpoint. Path is the source-of-truth
                        // for the per-request safety check (must canonicalize inside workspace root).
                        ["abspath"] = f.FullName,
                        ["relpath"] = rel,
                        ["sizeKb"] = sizeKb,
                        ["kind"] = kind,
                        ["ext"] = ext,
                    };
                    _sink?.Report(new FrameworkActivityEvent("artifact", msg, meta));
                    _emittedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CandidateArtifactWatcher poll iteration failed");
            }
        }
    }

    private static IEnumerable<FileInfo> EnumerateInteresting(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (_excludedTopLevel.Contains(name)) continue;
            foreach (var f in EnumerateInterestingRecursive(dir))
                yield return f;
        }
        // Also pick up files at the worktree root.
        foreach (var f in Directory.EnumerateFiles(root))
        {
            var fi = new FileInfo(f);
            if (_interestingExtensions.Contains(fi.Extension))
                yield return fi;
        }
    }

    private static IEnumerable<FileInfo> EnumerateInterestingRecursive(string dir)
    {
        IEnumerable<string> files;
        IEnumerable<string> subdirs;
        try
        {
            files = Directory.EnumerateFiles(dir);
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (Exception)
        {
            yield break;
        }
        foreach (var f in files)
        {
            FileInfo fi;
            try { fi = new FileInfo(f); }
            catch { continue; }
            if (_interestingExtensions.Contains(fi.Extension))
                yield return fi;
        }
        foreach (var sd in subdirs)
        {
            var name = Path.GetFileName(sd);
            if (_excludedTopLevel.Contains(name)) continue;
            foreach (var f in EnumerateInterestingRecursive(sd))
                yield return f;
        }
    }

    private static bool IsImageExtension(string ext) =>
        ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp";

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        try { _cts.Cancel(); } catch { }
        if (_loop is not null)
        {
            try { await _loop; } catch { }
        }
        _cts.Dispose();

        if (_emittedCount > 0 && _sink is not null)
        {
            _sink.Report(new FrameworkActivityEvent("artifact-summary",
                $"📦 Total artifacts emitted live: {_emittedCount}"));
        }
    }
}
