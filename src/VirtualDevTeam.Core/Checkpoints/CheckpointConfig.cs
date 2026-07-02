namespace VirtualDevTeam.Core.Checkpoints;

/// <summary>
/// Configuration for automatic pipeline checkpoints.
/// </summary>
public class CheckpointConfig
{
    /// <summary>Whether the checkpoint system is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum number of checkpoints to keep. Oldest evicted first (LRU).</summary>
    public int MaxCheckpoints { get; set; } = 5;

    /// <summary>Maximum total disk usage for all checkpoints in MB. LRU eviction when exceeded.</summary>
    public int MaxDiskMB { get; set; } = 2000;

    /// <summary>Compress checkpoint data (tar.gz). Reduces size ~60% but increases capture/restore time.</summary>
    public bool CompressSnapshots { get; set; }

    /// <summary>
    /// Exclude worktree backups from checkpoints. Worktrees can be recreated from branches
    /// but excluding them saves significant disk space for large projects.
    /// </summary>
    public bool ExcludeWorktrees { get; set; }

    /// <summary>Auto-capture trigger configuration.</summary>
    public AutoCaptureConfig AutoCapture { get; set; } = new();
}

/// <summary>Which events automatically trigger a checkpoint capture.</summary>
public class AutoCaptureConfig
{
    /// <summary>Capture when a PR is marked ready-for-review.</summary>
    public bool OnPRReady { get; set; } = true;

    /// <summary>Capture when a PR is merged.</summary>
    public bool OnPRMerge { get; set; } = true;

    /// <summary>Capture when all tasks in a wave are complete.</summary>
    public bool OnWaveComplete { get; set; } = true;

    /// <summary>Capture on workflow phase transitions (e.g., Research → Architecture).</summary>
    public bool OnPhaseTransition { get; set; }
}
