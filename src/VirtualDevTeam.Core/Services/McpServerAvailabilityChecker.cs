using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Services;

/// <summary>
/// Checks whether MCP server prerequisites are available on the host machine.
/// Validates runtime requirements (node, python, docker) and command availability.
/// </summary>
public class McpServerAvailabilityChecker
{
    private readonly McpServerRegistry _registry;
    private readonly ILogger<McpServerAvailabilityChecker> _logger;

    public McpServerAvailabilityChecker(McpServerRegistry registry, ILogger<McpServerAvailabilityChecker> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Check availability of a specific MCP server by name.</summary>
    public async Task<McpAvailabilityResult> CheckAsync(string serverName, CancellationToken ct = default)
    {
        var definition = _registry.Get(serverName);
        if (definition is null)
            return McpAvailabilityResult.NotRegistered(serverName);

        return await CheckDefinitionAsync(definition, ct);
    }

    /// <summary>Check availability of an MCP server from its definition.</summary>
    public async Task<McpAvailabilityResult> CheckDefinitionAsync(McpServerDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // Check required runtimes
        foreach (var runtime in definition.RequiredRuntimes)
        {
            if (!await IsRuntimeAvailableAsync(runtime, ct))
            {
                _logger.LogWarning("MCP server {Name} requires runtime {Runtime} which is not available",
                    definition.Name, runtime);
                return McpAvailabilityResult.MissingRuntime(definition.Name, runtime);
            }
        }

        // Check if the launch command exists (if specified)
        if (!string.IsNullOrWhiteSpace(definition.Command))
        {
            if (!await IsCommandAvailableAsync(definition.Command, ct))
            {
                _logger.LogWarning("MCP server {Name} requires command {Command} which is not available",
                    definition.Name, definition.Command);
                return McpAvailabilityResult.MissingCommand(definition.Name, definition.Command);
            }
        }

        return McpAvailabilityResult.Available(definition.Name);
    }

    /// <summary>Check all registered MCP servers and return their availability status.</summary>
    public async Task<IReadOnlyList<McpAvailabilityResult>> CheckAllAsync(CancellationToken ct = default)
    {
        var results = new List<McpAvailabilityResult>();
        foreach (var (name, _) in _registry.GetAll())
        {
            results.Add(await CheckAsync(name, ct));
        }
        return results;
    }

    private async Task<bool> IsRuntimeAvailableAsync(string runtime, CancellationToken ct)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = runtime,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Runtime {Runtime} not available", runtime);
            return false;
        }
    }

    private async Task<bool> IsCommandAvailableAsync(string command, CancellationToken ct)
    {
        try
        {
            // On Windows, use 'where'; on Unix, use 'which'
            var checkCommand = OperatingSystem.IsWindows() ? "where" : "which";
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = checkCommand,
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Command {Command} not available", command);
            return false;
        }
    }
}

/// <summary>Result of an MCP server availability check.</summary>
public record McpAvailabilityResult
{
    public required string ServerName { get; init; }
    public required bool IsAvailable { get; init; }
    public string? MissingDependency { get; init; }
    public McpUnavailableReason Reason { get; init; } = McpUnavailableReason.None;

    public static McpAvailabilityResult Available(string serverName)
        => new() { ServerName = serverName, IsAvailable = true };

    public static McpAvailabilityResult NotRegistered(string serverName)
        => new() { ServerName = serverName, IsAvailable = false, Reason = McpUnavailableReason.NotRegistered };

    public static McpAvailabilityResult MissingRuntime(string serverName, string runtime)
        => new() { ServerName = serverName, IsAvailable = false, MissingDependency = runtime, Reason = McpUnavailableReason.MissingRuntime };

    public static McpAvailabilityResult MissingCommand(string serverName, string command)
        => new() { ServerName = serverName, IsAvailable = false, MissingDependency = command, Reason = McpUnavailableReason.MissingCommand };
}

public enum McpUnavailableReason
{
    None,
    NotRegistered,
    MissingRuntime,
    MissingCommand
}
