using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Resolves build, test, and run commands based on service context.
/// Resolution order: ServiceDefinition → WorkspaceConfig → auto-detect.
/// </summary>
public class ServiceContextResolver
{
    private readonly IOptionsMonitor<VirtualDevTeamConfig> _config;
    private readonly ILogger<ServiceContextResolver> _logger;

    public ServiceContextResolver(
        IOptionsMonitor<VirtualDevTeamConfig> config,
        ILogger<ServiceContextResolver> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Resolve the build command and working directory for a service or global context.
    /// </summary>
    public BuildSpec GetBuildSpec(string workspacePath, ServiceDefinition? service = null)
    {
        var wsConfig = _config.CurrentValue.Workspace;

        if (service is not null && !string.IsNullOrWhiteSpace(service.BuildCommand))
        {
            return new BuildSpec(
                service.BuildCommand,
                Path.Combine(workspacePath, service.Path),
                null,
                wsConfig.BuildTimeoutSeconds);
        }

        return new BuildSpec(
            wsConfig.BuildCommand,
            workspacePath,
            null,
            wsConfig.BuildTimeoutSeconds);
    }

    /// <summary>
    /// Resolve the test command and working directory for a service.
    /// </summary>
    public BuildSpec GetTestSpec(string workspacePath, ServiceDefinition? service = null)
    {
        var wsConfig = _config.CurrentValue.Workspace;

        if (service is not null && !string.IsNullOrWhiteSpace(service.TestCommand))
        {
            return new BuildSpec(
                service.TestCommand,
                Path.Combine(workspacePath, service.Path),
                null,
                wsConfig.TestTimeoutSeconds);
        }

        return new BuildSpec(
            wsConfig.TestCommand,
            workspacePath,
            null,
            wsConfig.TestTimeoutSeconds);
    }

    /// <summary>
    /// Resolve the app start command for a service.
    /// </summary>
    public BuildSpec? GetRunSpec(string workspacePath, ServiceDefinition? service = null)
    {
        var wsConfig = _config.CurrentValue.Workspace;

        if (service is not null && !string.IsNullOrWhiteSpace(service.AppStartCommand))
        {
            return new BuildSpec(
                service.AppStartCommand,
                Path.Combine(workspacePath, service.Path),
                null,
                wsConfig.AppStartupTimeoutSeconds);
        }

        if (!string.IsNullOrWhiteSpace(wsConfig.AppStartCommand))
        {
            return new BuildSpec(
                wsConfig.AppStartCommand,
                workspacePath,
                null,
                wsConfig.AppStartupTimeoutSeconds);
        }

        return null;
    }

    /// <summary>
    /// Find a service definition by name (case-insensitive).
    /// </summary>
    public ServiceDefinition? FindService(string serviceName)
    {
        var largeProject = _config.CurrentValue.LargeProject;
        if (largeProject is null || !largeProject.Enabled)
            return null;

        return largeProject.Services.FirstOrDefault(
            s => s.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Find a service that owns the given file path (longest prefix match).
    /// </summary>
    public ServiceDefinition? FindServiceForPath(string relativePath)
    {
        var largeProject = _config.CurrentValue.LargeProject;
        if (largeProject is null || !largeProject.Enabled)
            return null;

        return largeProject.Services
            .Where(s => relativePath.StartsWith(s.Path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Path.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Get all registered services.
    /// </summary>
    public IReadOnlyList<ServiceDefinition> GetServices()
    {
        var largeProject = _config.CurrentValue.LargeProject;
        if (largeProject is null || !largeProject.Enabled)
            return Array.Empty<ServiceDefinition>();

        return largeProject.Services;
    }
}

/// <summary>
/// Resolved command specification with working directory and timeout.
/// </summary>
public record BuildSpec(
    string Command,
    string WorkingDirectory,
    IDictionary<string, string>? Env,
    int TimeoutSeconds);
