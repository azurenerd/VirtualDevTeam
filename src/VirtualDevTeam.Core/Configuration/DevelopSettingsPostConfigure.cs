using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Configuration;

/// <summary>
/// <see cref="IPostConfigureOptions{TOptions}"/> that merges the wizard-saved
/// <c>develop-settings.json</c> into every <see cref="VirtualDevTeamConfig"/> snapshot
/// the options framework builds — for <c>IOptions</c>, <c>IOptionsMonitor</c>, AND
/// <c>IOptionsSnapshot</c> consumers.
///
/// <para>
/// <b>Why this exists:</b> The Develop wizard saves project settings (GitHub repo, working
/// branch, AzureOpenAIImage endpoint + auth, gate preferences) to <c>develop-settings.json</c>.
/// Previously these were applied to the in-memory config only inside
/// <c>RunCoordinator.StartProject</c>. On a warm runner restart while a project was
/// mid-flight, <c>StartProject</c> didn't fire, so any consumer that resolved
/// <c>IOptionsMonitor&lt;VirtualDevTeamConfig&gt;.CurrentValue</c> after the restart
/// would see <c>AzureOpenAIImage = null</c> (the appsettings.json default) and the agentic
/// candidate sessions would see <c>ENDPOINT: ''</c> — leading to Pillow fallback art
/// instead of real gpt-image output. Running the merge as a PostConfigure makes it part
/// of the snapshot build itself so every snapshot is correct.
/// </para>
///
/// <para>
/// <b>Failure modes:</b> file missing → no-op (appsettings defaults remain). File present
/// but malformed → log warning + no-op. Both are intentional; we should never fail startup
/// because of a corrupt wizard file.
/// </para>
/// </summary>
public sealed class DevelopSettingsPostConfigure : IPostConfigureOptions<VirtualDevTeamConfig>
{
    private readonly ILogger<DevelopSettingsPostConfigure>? _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Resolves the develop-settings.json path the same way <see cref="DevelopSettingsService"/>
    /// does (relative to the entry-assembly's content root). Computed once at construction
    /// time so the merge stays fast on the hot path.
    /// </summary>
    private readonly string _settingsPath;

    public DevelopSettingsPostConfigure(ILogger<DevelopSettingsPostConfigure>? logger = null)
    {
        _logger = logger;
        _settingsPath = ResolveSettingsPath();
    }

    public void PostConfigure(string? name, VirtualDevTeamConfig options)
    {
        // Only post-configure the unnamed (default) options. The framework calls
        // PostConfigure with name=null for default options and name=non-null for named.
        if (!string.IsNullOrEmpty(name)) return;

        try
        {
            if (!File.Exists(_settingsPath)) return;

            var json = File.ReadAllText(_settingsPath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var settings = JsonSerializer.Deserialize<DevelopSettings>(json, _jsonOptions);
            if (settings is null) return;

            // Delegate to the existing merge logic so we stay in sync with the runtime path.
            // DevelopSettingsService.MergeIntoConfig is a pure mutator over the passed config.
            DevelopSettingsService.MergeIntoConfigStatic(options, settings);

            _logger?.LogDebug(
                "DevelopSettingsPostConfigure: merged settings into VirtualDevTeamConfig snapshot " +
                "(AzureOpenAIImage.IsConfigured={IsConfigured}, GitHubRepo={Repo})",
                options.AzureOpenAIImage?.IsConfigured() == true,
                options.Project?.GitHubRepo ?? "(none)");
        }
        catch (Exception ex)
        {
            // Never throw from PostConfigure — that would fail startup. Log and leave the
            // snapshot at appsettings.json defaults so the runner can still start.
            _logger?.LogWarning(ex,
                "DevelopSettingsPostConfigure: failed to merge develop-settings.json — VirtualDevTeamConfig snapshot will use appsettings.json defaults");
        }
    }

    private static string ResolveSettingsPath()
    {
        var cwd = Directory.GetCurrentDirectory();
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in BuildCandidatePaths(cwd, baseDir))
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* ignore — fall through to next candidate */ }
        }
        // Last resort: the CWD canonical path (matches DevelopSettingsService), even if missing,
        // so PostConfigure's File.Exists check returns false cleanly.
        return Path.Combine(cwd, "develop-settings.json");
    }

    /// <summary>
    /// Builds the ordered list of candidate <c>develop-settings.json</c> paths for a given
    /// current working directory and bin <paramref name="baseDir"/>. Pure (no filesystem access)
    /// so resolution order can be unit-tested.
    /// </summary>
    /// <remarks>
    /// CRITICAL ordering rules:
    /// <list type="number">
    ///   <item><description>The CWD path is FIRST — it matches what <see cref="DevelopSettingsService"/>
    ///   reads/writes (<c>Path.Combine(Directory.GetCurrentDirectory(), "develop-settings.json")</c>).
    ///   If these two consumers disagree on the file, operator intent silently drifts.</description></item>
    ///   <item><description>The bin output dir itself (<c>AppContext.BaseDirectory</c>) is NEVER a
    ///   candidate. The wizard can write a <c>develop-settings.json</c> into bin when launched with
    ///   CWD=bin; that stale copy must not override the operator's source copy on every
    ///   options-snapshot rebuild. Observed 2026-06: a stale <c>bin\Debug\net8.0\develop-settings.json</c>
    ///   (TestEngineerReviews=true) overrode the source copy (false), stalling the Testing gate.</description></item>
    ///   <item><description>Walk-up paths from bin reach the dev-time project/source dir as a fallback.</description></item>
    /// </list>
    /// </remarks>
    internal static IReadOnlyList<string> BuildCandidatePaths(string cwd, string baseDir)
    {
        var list = new List<string> { Path.Combine(cwd, "develop-settings.json") };
        try { list.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "develop-settings.json"))); } catch { }
        try { list.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "develop-settings.json"))); } catch { }
        return list;
    }
}
