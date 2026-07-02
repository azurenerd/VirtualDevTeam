using VirtualDevTeam.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Prompts;

/// <summary>
/// Watches the prompts directory for file changes and invalidates the template cache.
/// Uses a 250ms debounce to batch rapid successive file changes into a single invalidation.
/// </summary>
public sealed class PromptFileWatcher : IHostedService, IDisposable
{
    private readonly IPromptTemplateService _templateService;
    private readonly ILogger<PromptFileWatcher> _logger;
    private readonly string _watchPath;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly HashSet<string> _pendingInvalidations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    private const int DebounceMs = 250;

    public PromptFileWatcher(
        IPromptTemplateService templateService,
        IOptions<VirtualDevTeamConfig> config,
        ILogger<PromptFileWatcher> logger)
    {
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _watchPath = Path.GetFullPath(config.Value.Prompts.BasePath);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_watchPath))
        {
            _logger.LogWarning("Prompts directory does not exist, file watcher disabled: {Path}", _watchPath);
            return Task.CompletedTask;
        }

        _watcher = new FileSystemWatcher(_watchPath)
        {
            Filter = "*.md",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;

        _logger.LogInformation("Prompt file watcher started on {Path}", _watchPath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _logger.LogInformation("Prompt file watcher stopped");
        return Task.CompletedTask;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        QueueInvalidation(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        QueueInvalidation(e.OldFullPath);
        QueueInvalidation(e.FullPath);
    }

    private void QueueInvalidation(string fullPath)
    {
        if (!fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return;

        var relativePath = Path.GetRelativePath(_watchPath, fullPath)
            .Replace('\\', '/')
            .Replace(".md", "", StringComparison.OrdinalIgnoreCase);

        lock (_lock)
        {
            _pendingInvalidations.Add(relativePath);
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(FlushInvalidations, null, DebounceMs, Timeout.Infinite);
        }
    }

    private void FlushInvalidations(object? state)
    {
        HashSet<string> paths;
        lock (_lock)
        {
            paths = new HashSet<string>(_pendingInvalidations, StringComparer.OrdinalIgnoreCase);
            _pendingInvalidations.Clear();
        }

        foreach (var path in paths)
        {
            _logger.LogInformation("Prompt template changed, invalidating cache: {Path}", path);
            _templateService.InvalidateCache(path);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
