namespace VirtualDevTeam.Core.Strategies.Preview;

/// <summary>
/// Shared directory-name filters for candidate <b>preview</b> discovery
/// (<see cref="ImageAssetCandidatePreviewProducer"/> and
/// <see cref="DiagramCandidatePreviewProducer"/>).
/// </summary>
/// <remarks>
/// <para>
/// A candidate preview thumbnail is meant to represent the <b>visual deliverable VDT
/// produced</b> — a running app capture, a sprite/art sheet the agent generated, or a
/// diagram the agent authored. It must NOT surface <b>input artifacts that already
/// existed</b> and were merely copied into the worktree (e.g. a user-provided Data Flow
/// Diagram dropped into <c>tests/.../TestData/</c>, reference images, or seed fixtures).
/// Such files are byte-for-byte copies of inputs and showing them as a candidate's
/// "generated" preview is misleading.
/// </para>
/// <para>
/// <b>Why a directory filter (not just a git-diff filter):</b> when an agent copies a
/// user-supplied asset into the worktree it <i>commits</i> that file, so a
/// <c>git diff</c> against the baseline reports it as the candidate's own change — the
/// git filter cannot tell a verbatim input copy apart from generated work. Excluding the
/// conventional fixture/test-data/sample directories where inputs land is the reliable
/// signal that an asset is reference/input data rather than a generated deliverable.
/// </para>
/// </remarks>
internal static class PreviewDiscoveryFilters
{
    /// <summary>
    /// Build-output, tool-cache, and VCS directories that never contain a candidate's
    /// visual deliverable.
    /// </summary>
    private static readonly string[] BuildAndToolDirs =
    {
        ".git", "node_modules", "bin", "obj", ".candidates", ".vs", "packages",
    };

    /// <summary>
    /// Fixture / test-data / sample directories. Images and diagrams found under any of
    /// these are treated as <b>inputs or fixtures</b>, not generated output, and are
    /// excluded from candidate previews. Match is case-insensitive on the directory's own
    /// name (the whole subtree is pruned).
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT include <c>reference-images</c> — that is a documented,
    /// supported preview root for <see cref="ImageAssetCandidatePreviewProducer"/>
    /// (<c>AgentDocs/&lt;*&gt;/reference-images/</c>), where agents place images they
    /// intentionally produced/collected as deliverables.
    /// </remarks>
    private static readonly string[] FixtureAndInputDirs =
    {
        "testdata", "test-data", "testassets", "test-assets",
        "fixtures", "__fixtures__", "testfixtures", "test-fixtures",
        "__snapshots__",
        "seeddata", "seed-data", "sampledata", "sample-data",
    };

    /// <summary>
    /// All directory names excluded from candidate preview discovery — the union of
    /// build/tool directories and fixture/input directories.
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedDirectoryNames =
        new HashSet<string>(
            BuildAndToolDirs.Concat(FixtureAndInputDirs),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="directoryName"/> (a single path segment, not a full path)
    /// should be skipped during preview discovery.
    /// </summary>
    public static bool IsExcludedDirectory(string? directoryName) =>
        !string.IsNullOrEmpty(directoryName) && ExcludedDirectoryNames.Contains(directoryName);
}
