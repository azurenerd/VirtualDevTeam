namespace VirtualDevTeam.Core.MissingWork;
public sealed class MissingWorkConfig
{
    public int IntervalMinutes { get; set; } = 10;
    public int PerDetectorTimeoutSeconds { get; set; } = 15;
    public double PlannerConfidenceThreshold { get; set; } = 0.6;
}
