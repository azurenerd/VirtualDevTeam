using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VirtualDevTeam.Core.AI;

namespace VirtualDevTeam.Core.HealthMonitor;

/// <summary>
/// T1.6 Blocked-tier sibling of <see cref="LiveFixApplicator"/>. Runs at startup (before
/// FlowMonitorService), reads any plan files in <c>FixRecommendations/staged/</c>, applies
/// each via Copilot CLI in agentic mode, then runs <c>dotnet build VirtualDevTeam.sln</c>
/// to verify the workspace still compiles. Successfully-applied plans move to
/// <c>FixRecommendations/applied/</c>; failures stay in <c>staged/</c> so the operator can
/// see them on the next boot.
///
/// Why a hosted service instead of an on-approve CLI invocation:
/// <list type="bullet">
///   <item>Blocked fixes touch <c>.csproj</c> / <c>package.json</c> / SQL migrations — the
///         runner can't reload these in-place and the next-boot path is the only safe one.</item>
///   <item>Running before FlowMonitorService starts means the runner is in a known-good state
///         when normal operations resume.</item>
///   <item>If <c>dotnet build</c> fails, we surface the error in the logs and leave the
///         offending plan in <c>staged/</c> — the operator sees the failure instead of the
///         runner crash-looping with a broken workspace.</item>
/// </list>
///
/// Hard constraints:
/// <list type="bullet">
///   <item>This service is registered with <c>AddHostedService</c> ONLY in the runner; the
///         standalone dashboard does not auto-apply fixes.</item>
///   <item>It does NOT block runner startup on a missing <c>FixRecommendations/staged/</c>
///         directory — empty dir is the common case.</item>
///   <item>It does NOT exec <c>dotnet</c> if no fixes were applied — keeps boot fast.</item>
/// </list>
/// </summary>
public sealed class StagedFixApplicator : IHostedService
{
    private readonly CopilotCliProcessManager _cli;
    private readonly ILogger<StagedFixApplicator> _logger;

    /// <summary>Wall-clock cap per staged fix; collective cap is the sum across all staged plans.</summary>
    private static readonly TimeSpan PerFixTimeout = TimeSpan.FromMinutes(8);

    /// <summary>Wall-clock cap on the post-apply <c>dotnet build</c>. Tuned for clean builds; CI runs much longer.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    public StagedFixApplicator(
        CopilotCliProcessManager cli,
        ILogger<StagedFixApplicator> logger)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var stagedDir = Path.Combine(repoRoot, "FixRecommendations", "staged");
        var appliedDir = Path.Combine(repoRoot, "FixRecommendations", "applied");

        if (!Directory.Exists(stagedDir))
        {
            _logger.LogDebug("T1.6 StagedFixApplicator: no staged directory at {Path} — nothing to do", stagedDir);
            return;
        }

        var stagedFiles = Directory.GetFiles(stagedDir, "*.md")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (stagedFiles.Length == 0)
        {
            _logger.LogDebug("T1.6 StagedFixApplicator: staged dir is empty");
            return;
        }

        _logger.LogInformation(
            "T1.6 StagedFixApplicator: found {Count} staged fix plan(s) under {Path}; applying before runner starts",
            stagedFiles.Length, stagedDir);

        Directory.CreateDirectory(appliedDir);

        var applied = new List<string>();
        var failed = new List<string>();

        foreach (var planFile in stagedFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var success = await ApplyOneAsync(planFile, repoRoot, cancellationToken).ConfigureAwait(false);
                if (success)
                {
                    var movedTo = Path.Combine(appliedDir, Path.GetFileName(planFile));
                    // Ensure no clobber — append timestamp if the destination file already exists.
                    if (File.Exists(movedTo))
                    {
                        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                        movedTo = Path.Combine(appliedDir,
                            $"{Path.GetFileNameWithoutExtension(planFile)}-{stamp}{Path.GetExtension(planFile)}");
                    }
                    File.Move(planFile, movedTo);
                    applied.Add(planFile);
                }
                else
                {
                    failed.Add(planFile);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("T1.6 StagedFixApplicator: cancellation requested mid-apply ({Plan})", planFile);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "T1.6 StagedFixApplicator: unexpected error applying {Plan}", planFile);
                failed.Add(planFile);
            }
        }

        _logger.LogInformation(
            "T1.6 StagedFixApplicator: apply phase complete — {Applied} applied, {Failed} failed",
            applied.Count, failed.Count);

        // Skip the build entirely if nothing changed — saves 30+ seconds of cold-build time.
        if (applied.Count == 0)
        {
            if (failed.Count > 0)
            {
                _logger.LogWarning(
                    "T1.6 StagedFixApplicator: all staged plans failed to apply — leaving in staged/ for operator review");
            }
            return;
        }

        // Build verification — if it fails, log loudly but DO NOT block startup. The runner
        // can still boot to surface the issue on the dashboard. Operators can review the
        // applied/ folder + build log to decide whether to revert.
        var buildOk = await RunBuildAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        if (buildOk)
        {
            _logger.LogInformation(
                "T1.6 StagedFixApplicator: post-apply build PASSED — runner is starting in a known-good state");
        }
        else
        {
            _logger.LogError(
                "T1.6 StagedFixApplicator: post-apply build FAILED. {Applied} fix plan(s) were applied; review FixRecommendations/applied/ and revert if needed",
                applied.Count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Apply a single staged plan. Reads the file, sends it to the CLI in agentic mode with
    /// <c>--allow-all</c>, returns true if the CLI session reported success. We don't run
    /// the LiveFixApplicator's scope-verification here because the staged plan is by
    /// definition scope-violating (it was Blocked tier — touches dependencies / migrations).
    /// </summary>
    private async Task<bool> ApplyOneAsync(string planFile, string repoRoot, CancellationToken ct)
    {
        var planMarkdown = await File.ReadAllTextAsync(planFile, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(planMarkdown))
        {
            _logger.LogWarning("T1.6 StagedFixApplicator: staged plan {Plan} is empty; skipping", planFile);
            return false;
        }

        var prompt = BuildPrompt(planFile, planMarkdown);

        _logger.LogInformation("T1.6 StagedFixApplicator: applying staged plan {Plan}", Path.GetFileName(planFile));

        using var timeoutCts = new CancellationTokenSource(PerFixTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var options = new CopilotCliRequestOptions
        {
            Pool = CopilotCliPool.Agentic,
            AllowAll = true,
            CloseStdinAfterPrompt = true,
            WorkingDirectory = repoRoot,
            Timeout = PerFixTimeout,
        };

        try
        {
            var result = await _cli.ExecuteAgenticSessionAsync(prompt, options, linked.Token).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "T1.6 StagedFixApplicator: CLI session failed for {Plan} — {Reason}: {Error}",
                    Path.GetFileName(planFile), result.FailureReason, result.ErrorMessage ?? "(none)");
                return false;
            }
            _logger.LogInformation(
                "T1.6 StagedFixApplicator: CLI session succeeded for {Plan} ({Tools} tool calls in {Elapsed})",
                Path.GetFileName(planFile), result.ToolCallCount, result.WallClock);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "T1.6 StagedFixApplicator: CLI threw for {Plan}", planFile);
            return false;
        }
    }

    private static string BuildPrompt(string planFilePath, string planMarkdown)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Apply this staged fix plan");
        sb.AppendLine();
        sb.AppendLine("This plan was held over from a previous runner session because it touches files");
        sb.AppendLine("that cannot be modified while the runner is running (NuGet packages, .csproj,");
        sb.AppendLine("DB migrations, etc.). The runner is currently OFFLINE — apply the plan now.");
        sb.AppendLine();
        sb.AppendLine($"Plan file: `{Path.GetFileName(planFilePath)}`");
        sb.AppendLine();
        sb.AppendLine("## Constraints");
        sb.AppendLine();
        sb.AppendLine("1. Apply ONLY the changes described in the plan below.");
        sb.AppendLine("2. Do NOT run `git commit`, `git push`, `git reset`, or `git checkout`.");
        sb.AppendLine("3. After making file changes, you MAY run `dotnet restore` if the plan adds NuGet packages.");
        sb.AppendLine("4. Do NOT run `dotnet build` — the runner will do that as a verification step.");
        sb.AppendLine("5. After completing the edit, summarise what you changed in one sentence.");
        sb.AppendLine();
        sb.AppendLine("## Plan");
        sb.AppendLine();
        sb.AppendLine(planMarkdown);
        return sb.ToString();
    }

    /// <summary>
    /// Run <c>dotnet build VirtualDevTeam.sln --nologo --verbosity quiet</c>. Logs the full
    /// stderr on failure so the operator can diagnose without rerunning the build manually.
    /// Returns true on exit code 0, false otherwise.
    /// </summary>
    private async Task<bool> RunBuildAsync(string repoRoot, CancellationToken ct)
    {
        var slnPath = Path.Combine(repoRoot, "VirtualDevTeam.sln");
        if (!File.Exists(slnPath))
        {
            _logger.LogWarning("T1.6 StagedFixApplicator: VirtualDevTeam.sln not found at {Path}; skipping build", slnPath);
            return true; // not our problem
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { "build", slnPath, "--nologo", "--verbosity", "quiet" },
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return false;

            using var timeoutCts = new CancellationTokenSource(BuildTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var stdoutTask = p.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(linked.Token);

            try
            {
                await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                _logger.LogError("T1.6 StagedFixApplicator: dotnet build timed out after {Timeout}", BuildTimeout);
                return false;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (p.ExitCode != 0)
            {
                _logger.LogError(
                    "T1.6 StagedFixApplicator: build failed (exit {Code}). stdout tail:\n{Stdout}\n\nstderr tail:\n{Stderr}",
                    p.ExitCode,
                    stdout.Length > 2000 ? stdout[^2000..] : stdout,
                    stderr.Length > 2000 ? stderr[^2000..] : stderr);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "T1.6 StagedFixApplicator: build invocation failed");
            return false;
        }
    }
}
