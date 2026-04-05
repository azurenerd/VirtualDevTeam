namespace AgentSquad.Core.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class SemanticKernelExtensions
{
    /// <summary>
    /// Registers the <see cref="ModelRegistry"/> as a singleton, wired to the
    /// <see cref="AgentSquadConfig"/> options and the host's ILoggerFactory.
    /// </summary>
    public static IServiceCollection AddSemanticKernelModels(this IServiceCollection services)
    {
        services.AddSingleton<ModelRegistry>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<AgentSquadConfig>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new ModelRegistry(config, loggerFactory);
        });

        return services;
    }
}
