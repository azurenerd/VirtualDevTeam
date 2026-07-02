namespace VirtualDevTeam.Dashboard.Services;

/// <summary>
/// Injected to indicate whether the dashboard is running standalone (HTTP mode)
/// or embedded in the Runner (in-process mode). Pages use this to hide
/// features that require Runner-only services (Configuration, Engineering Plan).
/// </summary>
public sealed record DashboardMode(bool IsStandalone);
