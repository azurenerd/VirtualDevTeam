using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualDevTeam.Core.CompletionManifest;

/// <summary>
/// Root manifest record produced by an engineer agent at PR-completion time.
/// Describes which exports the agent claims to have fully implemented, which
/// are intentional stubs, and which BDD scenarios / steps were addressed.
///
/// <para>Sidecar location:</para>
/// <list type="bullet">
///   <item>Workspace mode: <c>{worktreePath}/.completion-manifests/pr-{N}.json</c></item>
///   <item>API-only mode: <c>{storagePath}/.completion-manifests/pr-{N}.json</c></item>
/// </list>
///
/// <para>Use <see cref="CompletionManifestWriter.WriteAsync"/> and
/// <see cref="CompletionManifestReader.ReadAsync"/> for I/O. The JSON format uses
/// snake_case property names via <see cref="JsonPropertyNameAttribute"/>.</para>
/// </summary>
public sealed record CompletionManifest
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("agent_id")]
    public required string AgentId { get; init; }

    [JsonPropertyName("pr_number")]
    public required int PrNumber { get; init; }

    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    /// <summary>The list of exported symbols the agent declares ownership over.</summary>
    [JsonPropertyName("exports")]
    public required IReadOnlyList<ManifestExport> Exports { get; init; }

    /// <summary>BDD scenario IDs (e.g. "S03", "S04") that were implemented in this PR.</summary>
    [JsonPropertyName("scenarios_implemented")]
    public IReadOnlyList<string> ScenariosImplemented { get; init; } = Array.Empty<string>();

    /// <summary>Per-scenario step ownership claimed by this agent.</summary>
    [JsonPropertyName("scenarios_steps_owned")]
    public IReadOnlyList<ManifestScenarioSteps> ScenariosStepsOwned { get; init; } = Array.Empty<ManifestScenarioSteps>();

    [JsonPropertyName("generated_at")]
    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Declares the implementation status of a single exported symbol.
/// If <see cref="FullyImplemented"/> is <c>false</c> and <see cref="StubOk"/> is <c>false</c>,
/// <see cref="CompletionManifestEnforcement.Check"/> will return
/// <see cref="EnforcementResult.BlockedByStub"/> and the PR must not be marked ready-for-review.
/// </summary>
public sealed record ManifestExport
{
    /// <summary>Relative file path inside the repo (forward-slash separator).</summary>
    [JsonPropertyName("file")]
    public required string File { get; init; }

    /// <summary>Export symbol name (function, class, constant, etc.).</summary>
    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    /// <summary>
    /// <c>true</c> if the symbol has a complete, working implementation.
    /// <c>false</c> if it is intentionally or accidentally left as a stub.
    /// </summary>
    [JsonPropertyName("fully_implemented")]
    public required bool FullyImplemented { get; init; }

    /// <summary>
    /// <c>true</c> if a partial/stub implementation is explicitly sanctioned by the task
    /// spec or a human operator (equivalent to the <c>// STUB_OK:</c> code annotation).
    /// Only meaningful when <see cref="FullyImplemented"/> is <c>false</c>.
    /// </summary>
    [JsonPropertyName("stub_ok")]
    public bool StubOk { get; init; }

    /// <summary>Free-form explanation when <see cref="StubOk"/> is <c>true</c> or when
    /// the export is only partially implemented.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Records which steps within a BDD scenario this PR's agent is responsible for.
/// </summary>
public sealed record ManifestScenarioSteps
{
    [JsonPropertyName("scenario")]
    public required string Scenario { get; init; }

    [JsonPropertyName("steps")]
    public required IReadOnlyList<int> Steps { get; init; }
}

// ── JSON options (file-scoped, not part of the public API) ───────────────────

file static class ManifestJsonOptions
{
    internal static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ── Reader ────────────────────────────────────────────────────────────────────

/// <summary>
/// Reads a <see cref="CompletionManifest"/> from its sidecar JSON file.
/// </summary>
public static class CompletionManifestReader
{
    /// <summary>
    /// Reads and deserializes the manifest at <paramref name="path"/>.
    /// Returns <c>null</c> if the file does not exist.
    /// </summary>
    /// <exception cref="JsonException">Thrown when the file exists but cannot be parsed.</exception>
    public static async Task<CompletionManifest?> ReadAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<CompletionManifest>(
                   stream, ManifestJsonOptions.Default, ct)
               ?? throw new JsonException($"Manifest at '{path}' deserialized to null.");
    }
}

// ── Writer ────────────────────────────────────────────────────────────────────

/// <summary>
/// Writes a <see cref="CompletionManifest"/> to its sidecar JSON file.
/// Creates the parent directory if it does not exist.
/// </summary>
public static class CompletionManifestWriter
{
    /// <summary>
    /// Serializes <paramref name="manifest"/> and writes it to <paramref name="path"/>,
    /// overwriting any existing file. Parent directories are created automatically.
    /// </summary>
    public static async Task WriteAsync(string path, CompletionManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(manifest);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions.Default, ct);
        await stream.FlushAsync(ct);
    }
}

// ── Path resolver ─────────────────────────────────────────────────────────────

/// <summary>
/// Resolves the sidecar path for a completion manifest given context about the
/// agent's runtime mode (workspace vs. API-only).
/// </summary>
public static class CompletionManifestPathResolver
{
    private const string ManifestDirName = ".completion-manifests";

    /// <summary>
    /// Returns the canonical path for a PR's completion manifest.
    /// </summary>
    /// <param name="prNumber">The PR number (used in the filename).</param>
    /// <param name="worktreePath">
    /// Absolute path to the agent's local worktree clone.
    /// If non-null, the manifest is co-located with the code:
    /// <c>{worktreePath}/.completion-manifests/pr-{N}.json</c>.
    /// </param>
    /// <param name="storagePath">
    /// Fallback storage path used in API-only mode (no worktree).
    /// Must be non-null when <paramref name="worktreePath"/> is null.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when both <paramref name="worktreePath"/> and <paramref name="storagePath"/> are null.
    /// </exception>
    public static string Resolve(int prNumber, string? worktreePath, string? storagePath = null)
    {
        var root = worktreePath ?? storagePath
            ?? throw new InvalidOperationException(
                "Either worktreePath or storagePath must be provided to resolve a completion manifest path.");

        return Path.Combine(root, ManifestDirName, $"pr-{prNumber}.json");
    }

    /// <summary>
    /// Returns the directory that holds all manifests for the given root path.
    /// </summary>
    public static string GetDirectory(string rootPath)
        => Path.Combine(rootPath, ManifestDirName);
}
