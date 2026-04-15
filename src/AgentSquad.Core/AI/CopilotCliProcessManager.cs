using System.Diagnostics;
using System.Text;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.AI;

/// <summary>
/// Manages execution of copilot CLI processes in non-interactive mode.
/// Each AI request spawns a fresh <c>copilot -p</c> process with auto-permissions.
/// Uses SemaphoreSlim for concurrency limiting.
/// </summary>
public sealed class CopilotCliProcessManager : IHostedService, IDisposable
{
    private readonly CopilotCliConfig _config;
    private readonly ILogger<CopilotCliProcessManager> _logger;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly CliInteractiveWatchdog _watchdog;
    private bool _copilotAvailable;
    private bool _disposed;

    public CopilotCliProcessManager(
        IOptions<AgentSquadConfig> config,
        ILogger<CopilotCliProcessManager> logger)
    {
        _config = config.Value.CopilotCli;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(_config.MaxConcurrentRequests, _config.MaxConcurrentRequests);
        _watchdog = new CliInteractiveWatchdog(logger, _config.AutoApprovePrompts);
    }

    /// <summary>Whether the copilot CLI was detected and is available for use.</summary>
    public bool IsAvailable => _copilotAvailable && !_disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _copilotAvailable = await VerifyCopilotInstalledAsync(cancellationToken);

        if (_copilotAvailable)
            _logger.LogInformation(
                "Copilot CLI available at '{Path}'. Max concurrent requests: {Max}",
                _config.ExecutablePath, _config.MaxConcurrentRequests);
        else
            _logger.LogWarning(
                "Copilot CLI not found at '{Path}'. Agents will use API-key fallback",
                _config.ExecutablePath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Execute a prompt via the copilot CLI in non-interactive mode.
    /// Spawns a fresh process, pipes the prompt via stdin, reads the response from stdout.
    /// </summary>
    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        CancellationToken ct = default)
    {
        return await ExecutePromptAsync(prompt, modelOverride: null, ct);
    }

    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        string? modelOverride,
        CancellationToken ct = default)
    {
        return await ExecutePromptAsync(prompt, modelOverride, sessionId: null, ct);
    }

    public async Task<CopilotCliResult> ExecutePromptAsync(
        string prompt,
        string? modelOverride,
        string? sessionId,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_copilotAvailable)
            return CopilotCliResult.Failure("Copilot CLI is not available");

        // Wait for a concurrency slot
        await _concurrencyLimiter.WaitAsync(ct);
        try
        {
            return await RunProcessAsync(prompt, modelOverride, sessionId, ct);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task<CopilotCliResult> RunProcessAsync(string prompt, string? modelOverride, string? sessionId, CancellationToken ct)
    {
        var args = BuildArguments(modelOverride, sessionId);

        var psi = new ProcessStartInfo
        {
            FileName = _config.ExecutablePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Set working directory if configured
        if (!string.IsNullOrEmpty(_config.WorkingDirectory))
            psi.WorkingDirectory = _config.WorkingDirectory;

        // Environment overrides
        psi.Environment["NO_COLOR"] = "1";

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_config.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Process process;
        try
        {
            process = new Process { StartInfo = psi };
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start copilot process");
            return CopilotCliResult.Failure($"Failed to start copilot: {ex.Message}");
        }

        using (process)
        {
            try
            {
                // Pipe the prompt via stdin and close to signal EOF
                await process.StandardInput.WriteAsync(prompt.AsMemory(), linked.Token);
                process.StandardInput.Close();

                // Read stdout and stderr concurrently
                var stdoutTask = ReadOutputWithWatchdogAsync(
                    process, process.StandardOutput, linked.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

                // Wait for process to exit
                await process.WaitForExitAsync(linked.Token);

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "Copilot process exited with code {Code}. stderr: {Stderr}",
                        process.ExitCode, stderr.Length > 500 ? stderr[..500] : stderr);

                    // Still return stdout if there's content — partial responses can be useful
                    if (!string.IsNullOrWhiteSpace(stdout))
                        return CopilotCliResult.Success(stdout, process.ExitCode);

                    return CopilotCliResult.Failure(
                        $"Copilot exited with code {process.ExitCode}: {stderr}");
                }

                return CopilotCliResult.Success(stdout, process.ExitCode);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                KillProcessSafely(process);
                return CopilotCliResult.Failure(
                    $"Copilot request timed out after {_config.RequestTimeoutSeconds}s");
            }
            catch (OperationCanceledException)
            {
                KillProcessSafely(process);
                throw; // Caller-initiated cancellation — propagate
            }
            catch (Exception ex)
            {
                KillProcessSafely(process);
                _logger.LogError(ex, "Error during copilot process execution");
                return CopilotCliResult.Failure($"Process error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads stdout while monitoring for interactive prompts via the watchdog.
    /// If a prompt is detected, auto-responds via stdin.
    /// </summary>
    private async Task<string> ReadOutputWithWatchdogAsync(
        Process process, StreamReader stdout, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];

        while (!ct.IsCancellationRequested)
        {
            var readTask = stdout.ReadAsync(buffer, ct);
            int charsRead;

            try
            {
                charsRead = await readTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (charsRead == 0)
                break; // EOF

            var chunk = new string(buffer, 0, charsRead);
            output.Append(chunk);

            // Check each line in the chunk for interactive prompts
            var lines = chunk.Split('\n');
            foreach (var line in lines)
            {
                var action = _watchdog.DetectPrompt(line);
                if (action == null) continue;

                if (action.Type == WatchdogActionType.FailFast)
                {
                    _logger.LogError("Watchdog fail-fast: {Reason}", action.Reason);
                    KillProcessSafely(process);
                    throw new CopilotCliException(action.Reason);
                }

                if (action.Type == WatchdogActionType.Respond && !process.HasExited)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync(
                            action.Response.AsMemory(), ct);
                        await process.StandardInput.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to write watchdog response to stdin");
                    }
                }
            }
        }

        return output.ToString();
    }

    private string BuildArguments(string? modelOverride = null, string? sessionId = null)
    {
        var args = new StringBuilder();

        // Core flags for non-interactive autonomous operation
        // NOTE: We intentionally omit --allow-all so the CLI cannot use file-creation
        // tools. Our agents need TEXT responses, not local file operations.
        args.Append("--no-ask-user ");
        args.Append("--no-auto-update ");
        args.Append("--no-custom-instructions ");

        // Session resume for conversational continuity across calls
        if (!string.IsNullOrEmpty(sessionId))
            args.Append($"--resume={sessionId} ");

        if (_config.SilentMode)
            args.Append("--silent ");

        args.Append("--no-color ");

        if (_config.JsonOutput)
            args.Append("--output-format json ");

        // Model selection (per-agent override takes precedence)
        var model = modelOverride ?? _config.ModelName;
        args.Append($"--model {model} ");

        // Reasoning effort
        if (!string.IsNullOrEmpty(_config.ReasoningEffort))
            args.Append($"--effort {_config.ReasoningEffort} ");

        // Excluded tools
        foreach (var tool in _config.ExcludedTools)
            args.Append($"--excluded-tools {tool} ");

        // Additional user-specified args
        if (!string.IsNullOrEmpty(_config.AdditionalArgs))
            args.Append(_config.AdditionalArgs);

        // MCP servers from the current agent's role configuration
        var mcpServers = AgentCallContext.McpServers;
        if (mcpServers is { Count: > 0 })
        {
            foreach (var server in mcpServers)
            {
                if (!string.IsNullOrWhiteSpace(server))
                    args.Append($" --mcp-server {server}");
            }
        }

        return args.ToString().Trim();
    }

    private async Task<bool> VerifyCopilotInstalledAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var psi = new ProcessStartInfo
            {
                FileName = _config.ExecutablePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode == 0)
            {
                _logger.LogDebug("Copilot CLI version: {Version}", output.Trim());
                return true;
            }

            _logger.LogDebug("copilot --version exited with code {Code}", process.ExitCode);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Copilot CLI verification timed out after 10s — treating as unavailable");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Copilot CLI not found at '{Path}'", _config.ExecutablePath);
            return false;
        }
    }

    private void KillProcessSafely(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error killing copilot process");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _concurrencyLimiter.Dispose();
    }
}

/// <summary>Result of a copilot CLI execution.</summary>
public class CopilotCliResult
{
    public bool IsSuccess { get; init; }
    public string Output { get; init; } = "";
    public string? Error { get; init; }
    public int ExitCode { get; init; }

    public static CopilotCliResult Success(string output, int exitCode = 0) =>
        new() { IsSuccess = true, Output = output, ExitCode = exitCode };

    public static CopilotCliResult Failure(string error) =>
        new() { IsSuccess = false, Error = error, ExitCode = -1 };
}

/// <summary>Thrown when the copilot CLI encounters an unrecoverable error (e.g., credential prompt).</summary>
public class CopilotCliException : Exception
{
    public CopilotCliException(string message) : base(message) { }
    public CopilotCliException(string message, Exception inner) : base(message, inner) { }
}
