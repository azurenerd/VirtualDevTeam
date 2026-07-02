namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// Classifies the initiating mechanism of a scenario journey, enabling
/// journey-kind-specific observation surface strategies (DOM/canvas for UI,
/// HTTP/DB for API, stdout/exit-code for CLI, etc.).
/// </summary>
public enum JourneyKind
{
    /// <summary>A user interacting with a rendered UI (web, desktop, game canvas).</summary>
    UiInteraction,

    /// <summary>An authenticated or anonymous caller hitting a REST/GraphQL/gRPC endpoint.</summary>
    ApiCall,

    /// <summary>A time-based trigger (cron job, timer, scheduler) initiating a batch run.</summary>
    ScheduledJob,

    /// <summary>An event arriving from an external system (domain event, integration event).</summary>
    EventArrival,

    /// <summary>An inbound HTTP webhook (Stripe, GitHub, Slack, etc.).</summary>
    Webhook,

    /// <summary>A message consumed from a queue or topic (RabbitMQ, Service Bus, Kafka, etc.).</summary>
    MessageConsume,

    /// <summary>A command invoked from a terminal by a human or automation script.</summary>
    CliInvocation,

    /// <summary>An action initiated by the system itself (startup, health-check, background worker).</summary>
    SystemInitiated,

    /// <summary>A data pipeline run (ETL, import, export, transform batch).</summary>
    DataPipeline,
}
