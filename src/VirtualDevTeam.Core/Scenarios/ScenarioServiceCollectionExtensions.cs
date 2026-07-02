using Microsoft.Extensions.DependencyInjection;

namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Extension methods for registering Scenarios services in a dependency injection container.
/// </summary>
public static class ScenarioServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ScenarioRegistry"/> as the singleton implementation of
    /// <see cref="IScenarioRegistry"/>.
    /// </summary>
    /// <remarks>
    /// <c>ProjectFileManager</c> must be registered in the container separately (it is
    /// typically registered by the Runner's <c>Program.cs</c> setup).
    /// </remarks>
    public static IServiceCollection AddScenarios(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IScenarioRegistry, ScenarioRegistry>();
        services.AddSingleton<SmokeTestGenerator>();
        return services;
    }
}
