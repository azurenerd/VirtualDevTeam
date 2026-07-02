namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// Configuration for large project support — service registry, incremental builds,
/// and external dev server integration. Bound from the "LargeProject" section.
/// </summary>
public class LargeProjectConfig
{
    /// <summary>Enable large project features (service scoping, incremental builds).</summary>
    public bool Enabled { get; set; }

    /// <summary>Registered services in the monorepo.</summary>
    public List<ServiceDefinition> Services { get; set; } = new();

    /// <summary>Incremental build configuration.</summary>
    public IncrementalBuildConfig IncrementalBuild { get; set; } = new();

    /// <summary>External dev server configuration (connect instead of launch).</summary>
    public DevServerConfig DevServer { get; set; } = new();

    /// <summary>
    /// When true, tasks are assigned to agents based on service scope expertise tags.
    /// When false, any engineer can work on any service.
    /// </summary>
    public bool ServiceScopedTaskAssignment { get; set; } = true;
}

/// <summary>
/// Defines a service within a monorepo — its path, build/test commands,
/// dev server config, and expertise tags for agent assignment.
/// </summary>
public class ServiceDefinition
{
    /// <summary>Short identifier for the service (e.g., "auth-api").</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable display name (e.g., "Auth API").</summary>
    public string? DisplayName { get; init; }

    /// <summary>Relative path from repo root (e.g., "src/services/auth").</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Build command for this service (e.g., "dotnet build Auth.csproj").
    /// When null, falls back to WorkspaceConfig.BuildCommand.
    /// </summary>
    public string? BuildCommand { get; init; }

    /// <summary>
    /// Test command for this service (e.g., "dotnet test Auth.Tests.csproj").
    /// When null, falls back to WorkspaceConfig.TestCommand.
    /// </summary>
    public string? TestCommand { get; init; }

    /// <summary>
    /// App start command for this service (e.g., "dotnet run --project Auth").
    /// When null, falls back to WorkspaceConfig.AppStartCommand.
    /// </summary>
    public string? AppStartCommand { get; init; }

    /// <summary>Port the service runs on for UI testing.</summary>
    public int? Port { get; init; }

    /// <summary>Health check URL for the service (e.g., "http://localhost:5001/health").</summary>
    public string? HealthUrl { get; init; }

    /// <summary>
    /// When true, connect to an already-running dev server instead of launching one.
    /// The operator starts the server independently (e.g., via Docker Compose).
    /// </summary>
    public bool UseExistingDevServer { get; init; }

    /// <summary>Tech stack identifier (e.g., "dotnet", "react+typescript", "python").</summary>
    public string? TechStack { get; init; }

    /// <summary>
    /// Additional sparse checkout paths needed for this service's builds.
    /// Merged with global SparseCheckoutPaths.
    /// </summary>
    public List<string> AdditionalSparsePaths { get; init; } = new();

    /// <summary>
    /// Expertise tags for agent assignment routing (e.g., ["dotnet", "auth", "security"]).
    /// </summary>
    public List<string> ExpertiseTags { get; init; } = new();

    /// <summary>Effective display name (falls back to Name).</summary>
    public string EffectiveDisplayName => DisplayName ?? Name;
}

/// <summary>
/// Configuration for an external dev server that VDT connects to
/// instead of launching its own app instance.
/// </summary>
public record DevServerConfig
{
    /// <summary>Base URL of the dev server (e.g., "http://localhost:3000").</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Health check URL (e.g., "http://localhost:3000/health").</summary>
    public string? HealthUrl { get; init; }

    /// <summary>
    /// When true, skip health checks and assume the server is always running.
    /// Useful for servers that don't have a health endpoint.
    /// </summary>
    public bool AssumeAlwaysUp { get; init; } = true;
}

/// <summary>
/// Configuration for incremental builds — only build what changed.
/// </summary>
public class IncrementalBuildConfig
{
    /// <summary>Enable incremental builds.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Strategy: "service-scope" (build assigned service only) or
    /// "changed-files" (map changed files to affected services).
    /// </summary>
    public string Strategy { get; set; } = "service-scope";

    /// <summary>Treat no-op builds (nothing to build) as success.</summary>
    public bool TreatNoOpAsSuccess { get; set; } = true;
}
