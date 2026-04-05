using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.Messaging;

/// <summary>
/// Channel-based in-process message bus. Each subscriber gets a bounded channel;
/// a background task drains the channel and dispatches to registered handlers.
/// </summary>
public sealed class InProcessMessageBus : IMessageBus, IDisposable
{
    private readonly ILogger<InProcessMessageBus> _logger;
    private readonly ConcurrentDictionary<string, AgentMailbox> _mailboxes = new();
    private readonly ConcurrentDictionary<SubscriptionKey, List<Delegate>> _typedHandlers = new();
    private readonly ConcurrentDictionary<string, List<Func<object, CancellationToken, Task>>> _catchAllHandlers = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    private const int ChannelCapacity = 1000;

    public InProcessMessageBus(ILogger<InProcessMessageBus> logger)
    {
        _logger = logger;
    }

    public async Task PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        var toAgentId = ExtractToAgentId(message);
        var isBroadcast = string.IsNullOrEmpty(toAgentId) || toAgentId == "*";

        if (isBroadcast)
        {
            _logger.LogDebug("Broadcasting {MessageType} to all agents", typeof(TMessage).Name);
            foreach (var kvp in _mailboxes)
            {
                await EnqueueAsync(kvp.Value, message, ct).ConfigureAwait(false);
            }
        }
        else
        {
            _logger.LogDebug("Routing {MessageType} to agent {AgentId}", typeof(TMessage).Name, toAgentId);
            var mailbox = GetOrCreateMailbox(toAgentId!);
            await EnqueueAsync(mailbox, message, ct).ConfigureAwait(false);
        }
    }

    public IDisposable Subscribe<TMessage>(string agentId, Func<TMessage, CancellationToken, Task> handler)
        where TMessage : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(handler);

        // Ensure the mailbox and its consumer loop exist
        GetOrCreateMailbox(agentId);

        var key = new SubscriptionKey(agentId, typeof(TMessage));
        var handlers = _typedHandlers.GetOrAdd(key, _ => new List<Delegate>());

        lock (handlers)
        {
            handlers.Add(handler);
        }

        _logger.LogDebug("Agent {AgentId} subscribed to {MessageType}", agentId, typeof(TMessage).Name);
        return new Subscription(() => RemoveTypedHandler(key, handler));
    }

    public IDisposable SubscribeAll(string agentId, Func<object, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(handler);

        GetOrCreateMailbox(agentId);

        var handlers = _catchAllHandlers.GetOrAdd(agentId, _ => new List<Func<object, CancellationToken, Task>>());

        lock (handlers)
        {
            handlers.Add(handler);
        }

        _logger.LogDebug("Agent {AgentId} subscribed to all message types", agentId);
        return new Subscription(() => RemoveCatchAllHandler(agentId, handler));
    }

    public int GetPendingCount(string agentId)
    {
        if (_mailboxes.TryGetValue(agentId, out var mailbox))
        {
            return mailbox.Channel.Reader.Count;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _disposeCts.Cancel();

        foreach (var kvp in _mailboxes)
        {
            kvp.Value.Channel.Writer.TryComplete();
        }

        _disposeCts.Dispose();
    }

    // --- internals ---

    private AgentMailbox GetOrCreateMailbox(string agentId)
    {
        return _mailboxes.GetOrAdd(agentId, id =>
        {
            var options = new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };

            var channel = Channel.CreateBounded<object>(options);
            var mailbox = new AgentMailbox(id, channel);

            // Start a background consumer for this mailbox
            _ = Task.Run(() => ConsumeAsync(mailbox, _disposeCts.Token));

            _logger.LogDebug("Created mailbox for agent {AgentId} (capacity {Capacity})", id, ChannelCapacity);
            return mailbox;
        });
    }

    private static async Task EnqueueAsync(AgentMailbox mailbox, object message, CancellationToken ct)
    {
        await mailbox.Channel.Writer.WriteAsync(message, ct).ConfigureAwait(false);
    }

    private async Task ConsumeAsync(AgentMailbox mailbox, CancellationToken ct)
    {
        var reader = mailbox.Channel.Reader;

        try
        {
            await foreach (var message in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await DispatchAsync(mailbox.AgentId, message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (ChannelClosedException)
        {
            // Channel was completed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consumer loop for agent {AgentId} failed", mailbox.AgentId);
        }
    }

    private async Task DispatchAsync(string agentId, object message, CancellationToken ct)
    {
        var messageType = message.GetType();

        // Dispatch to typed handlers — walk the type hierarchy so a handler for a base type fires too
        var type = messageType;
        while (type != null && type != typeof(object))
        {
            var key = new SubscriptionKey(agentId, type);
            if (_typedHandlers.TryGetValue(key, out var handlers))
            {
                Delegate[] snapshot;
                lock (handlers)
                {
                    snapshot = handlers.ToArray();
                }

                foreach (var handler in snapshot)
                {
                    try
                    {
                        var task = (Task)handler.DynamicInvoke(message, ct)!;
                        await task.ConfigureAwait(false);
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException is not null)
                    {
                        _logger.LogError(tie.InnerException,
                            "Handler for {MessageType} on agent {AgentId} threw an exception",
                            type.Name, agentId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Handler for {MessageType} on agent {AgentId} threw an exception",
                            type.Name, agentId);
                    }
                }
            }

            type = type.BaseType;
        }

        // Also check interfaces implemented by the message
        foreach (var iface in messageType.GetInterfaces())
        {
            var key = new SubscriptionKey(agentId, iface);
            if (_typedHandlers.TryGetValue(key, out var handlers))
            {
                Delegate[] snapshot;
                lock (handlers)
                {
                    snapshot = handlers.ToArray();
                }

                foreach (var handler in snapshot)
                {
                    try
                    {
                        var task = (Task)handler.DynamicInvoke(message, ct)!;
                        await task.ConfigureAwait(false);
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException is not null)
                    {
                        _logger.LogError(tie.InnerException,
                            "Handler for {MessageType} on agent {AgentId} threw an exception",
                            iface.Name, agentId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Handler for {MessageType} on agent {AgentId} threw an exception",
                            iface.Name, agentId);
                    }
                }
            }
        }

        // Dispatch to catch-all handlers
        if (_catchAllHandlers.TryGetValue(agentId, out var catchAlls))
        {
            Func<object, CancellationToken, Task>[] snapshot;
            lock (catchAlls)
            {
                snapshot = catchAlls.ToArray();
            }

            foreach (var handler in snapshot)
            {
                try
                {
                    await handler(message, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Catch-all handler on agent {AgentId} threw an exception",
                        agentId);
                }
            }
        }
    }

    /// <summary>
    /// Uses reflection / duck-typing to read a "ToAgentId" property from the message.
    /// Returns null when no such property exists.
    /// </summary>
    private static string? ExtractToAgentId(object message)
    {
        var prop = message.GetType().GetProperty("ToAgentId",
            BindingFlags.Public | BindingFlags.Instance);

        return prop?.GetValue(message) as string;
    }

    private void RemoveTypedHandler(SubscriptionKey key, Delegate handler)
    {
        if (_typedHandlers.TryGetValue(key, out var handlers))
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }
        }
    }

    private void RemoveCatchAllHandler(string agentId, Func<object, CancellationToken, Task> handler)
    {
        if (_catchAllHandlers.TryGetValue(agentId, out var handlers))
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }
        }
    }

    // --- nested types ---

    private sealed record SubscriptionKey(string AgentId, Type MessageType);

    private sealed class AgentMailbox(string agentId, Channel<object> channel)
    {
        public string AgentId { get; } = agentId;
        public Channel<object> Channel { get; } = channel;
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}
