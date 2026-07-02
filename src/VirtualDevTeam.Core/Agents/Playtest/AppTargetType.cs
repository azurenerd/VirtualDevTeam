namespace VirtualDevTeam.Core.Agents.Playtest;

/// <summary>
/// Classifies the runtime shape of the application under test, allowing the
/// <see cref="IAppPlaytester"/> to choose the right warm-up strategy and adapter
/// defaults before any scenario-specific action plan is executed.
/// </summary>
public enum AppTargetType
{
    /// <summary>Browser-rendered UI served over HTTP/HTTPS (Playwright adapter).</summary>
    Web,

    /// <summary>REST, GraphQL, or gRPC service — no UI; accessed via HTTP(S) (HTTP adapter).</summary>
    Api,

    /// <summary>Command-line tool invoked as a child process (CLI adapter).</summary>
    Cli,

    /// <summary>Native desktop application (Win32, Electron, WPF, etc.).</summary>
    Desktop,

    /// <summary>Mobile application (iOS, Android).</summary>
    Mobile,

    /// <summary>
    /// Background service, scheduled job, webhook processor, or message consumer.
    /// No user-facing interface; verified via side-effects (DB rows, queue messages, log lines).
    /// </summary>
    Background,
}
