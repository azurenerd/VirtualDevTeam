using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Checkpoints;

/// <summary>
/// Captures and restores pipeline state using PowerShell scripts.
/// Wraps the existing capture-state.ps1 / setup.ps1 infrastructure
/// with C# lifecycle management, LRU eviction, and manifest tracking.
/// </summary>
public sealed class PipelineCheckpointService : IPipelineCheckpointService
{
    private readonly ILogger<PipelineCheckpointService> _logger;
    private readonly CheckpointConfig _config;
    private readonly string _runnerDir;
    private readonly string _checkpointsDir;
    private readonly string _captureScriptPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PipelineCheckpointService(
        IOptions<VirtualDevTeamConfig> config,
        ILogger<PipelineCheckpointService> logger)
    {
        _logger = logger;
        _config = config.Value.Checkpoints;

        _runnerDir = AppContext.BaseDirectory;
        // Walk up from bin/Debug/net8.0 to the Runner project dir
        var projectDir = Path.GetFullPath(Path.Combine(_runnerDir, "..", "..", ".."));
        if (File.Exists(Path.Combine(projectDir, "VirtualDevTeam.Runner.csproj")))
            _runnerDir = projectDir;

        _checkpointsDir = Path.Combine(
            config.Value.Workspace.RootPath ?? Path.Combine(_runnerDir, ".agents"),
            ".checkpoints");

        // The capture script lives in tests/temp/ alongside existing snapshots
        var repoRoot = Path.GetFullPath(Path.Combine(_runnerDir, "..", ".."));
        _captureScriptPath = Path.Combine(repoRoot, "tests", "temp", "capture-state.ps1");
    }

    public async Task<CheckpointResult> CaptureAsync(string name, CheckpointTrigger trigger, CancellationToken ct = default)
    {
        if (!_config.Enabled)
            return CheckpointResult.Failure("Checkpoints are disabled", TimeSpan.Zero);

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var sw = Stopwatch.StartNew();

        await _lock.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Capturing checkpoint '{Name}' (trigger={Trigger})", name, trigger);

            // Evict if at capacity
            var evicted = await EvictIfNeededAsync(ct);

            if (!File.Exists(_captureScriptPath))
            {
                return CheckpointResult.Failure(
                    $"Capture script not found: {_captureScriptPath}", sw.Elapsed);
            }

            var result = await RunPowerShellAsync(
                $"-ExecutionPolicy Bypass -File \"{_captureScriptPath}\" -StateName \"{name}\" -SkipRunnerStop",
                TimeSpan.FromMinutes(5), ct);

            if (!result.Succeeded)
            {
                _logger.LogWarning("Checkpoint capture failed: {Error}", result.Error);
                return CheckpointResult.Failure(result.Error ?? "Unknown error", sw.Elapsed);
            }

            // Write manifest
            var checkpointDir = Path.Combine(
                Path.GetDirectoryName(_captureScriptPath)!, name);
            var info = new CheckpointInfo
            {
                Name = name,
                CapturedAt = DateTimeOffset.UtcNow,
                Trigger = trigger,
                Phase = "Unknown", // Caller should set this
                DiskSizeBytes = GetDirectorySize(checkpointDir),
            };

            var manifestPath = Path.Combine(checkpointDir, "checkpoint-manifest.json");
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, json, ct);

            _logger.LogInformation(
                "Checkpoint '{Name}' captured in {Elapsed:F1}s ({Size}MB)",
                name, sw.Elapsed.TotalSeconds, info.DiskSizeBytes / (1024 * 1024));

            return CheckpointResult.Success(info, sw.Elapsed, evicted);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RestoreResult> RestoreAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var sw = Stopwatch.StartNew();

        await _lock.WaitAsync(ct);
        try
        {
            var setupScript = Path.Combine(
                Path.GetDirectoryName(_captureScriptPath)!, name, "setup.ps1");

            if (!File.Exists(setupScript))
                return RestoreResult.Failure($"Setup script not found: {setupScript}", sw.Elapsed);

            _logger.LogInformation("Restoring checkpoint '{Name}'", name);

            var result = await RunPowerShellAsync(
                $"-ExecutionPolicy Bypass -File \"{setupScript}\" -Force",
                TimeSpan.FromMinutes(10), ct);

            if (!result.Succeeded)
                return RestoreResult.Failure(result.Error ?? "Restore failed", sw.Elapsed);

            _logger.LogInformation("Checkpoint '{Name}' restored in {Elapsed:F1}s", name, sw.Elapsed.TotalSeconds);
            return RestoreResult.Success(sw.Elapsed);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<IReadOnlyList<CheckpointInfo>> ListAsync(CancellationToken ct = default)
    {
        var snapshotsDir = Path.GetDirectoryName(_captureScriptPath)!;
        var checkpoints = new List<CheckpointInfo>();

        if (!Directory.Exists(snapshotsDir))
            return Task.FromResult<IReadOnlyList<CheckpointInfo>>(checkpoints);

        foreach (var dir in Directory.GetDirectories(snapshotsDir))
        {
            var manifestPath = Path.Combine(dir, "checkpoint-manifest.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var info = JsonSerializer.Deserialize<CheckpointInfo>(json);
                if (info is not null)
                    checkpoints.Add(info);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read checkpoint manifest: {Path}", manifestPath);
            }
        }

        var result = checkpoints.OrderByDescending(c => c.CapturedAt).ToList();
        return Task.FromResult<IReadOnlyList<CheckpointInfo>>(result);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetDirectoryName(_captureScriptPath)!, name);
        if (!Directory.Exists(dir))
            return Task.FromResult(false);

        try
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Deleted checkpoint '{Name}'", name);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete checkpoint '{Name}'", name);
            return Task.FromResult(false);
        }
    }

    public async Task<CheckpointInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        var all = await ListAsync(ct);
        return all.FirstOrDefault();
    }

    private async Task<string?> EvictIfNeededAsync(CancellationToken ct)
    {
        var all = await ListAsync(ct);
        if (all.Count < _config.MaxCheckpoints)
            return null;

        // Evict oldest
        var oldest = all.Last();
        _logger.LogInformation("Evicting oldest checkpoint '{Name}' to make room", oldest.Name);
        await DeleteAsync(oldest.Name, ct);
        return oldest.Name;
    }

    private async Task<(bool Succeeded, string? Error)> RunPowerShellAsync(
        string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pwsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            return (false, "Failed to start pwsh");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var error = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                return (false, $"Exit code {process.ExitCode}: {error.Trim()}");
            }

            return (true, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (false, $"Timed out after {timeout.TotalSeconds}s");
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }
}
