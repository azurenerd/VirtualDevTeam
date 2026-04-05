using Microsoft.Extensions.DependencyInjection;

namespace AgentSquad.Core.Messaging;

/// <summary>
/// Extension methods for registering the in-process message bus with DI.
/// </summary>
public static class MessageBusExtensions
{
    /// <summary>
    /// Registers <see cref="InProcessMessageBus"/> as a singleton <see cref="IMessageBus"/>.
    /// </summary>
    public static IServiceCollection AddInProcessMessageBus(this IServiceCollection services)
    {
        services.AddSingleton<IMessageBus, InProcessMessageBus>();
        return services;
    }
}
