using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.DevPlatform.Auth;

/// <summary>
/// Acquires GitHub tokens using the gh CLI (GitHub CLI).
/// Uses: gh auth token
/// Designed for EMU (Enterprise Managed User) accounts where PATs are disabled.
/// The gh CLI manages token refresh internally — we cache and periodically re-fetch.
/// </summary>
public sealed class GhCliAuthProvider : IDevPlatformAuthProvider, IDisposable
{
    private readonly ILogger<GhCliAuthProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedToken;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private bool _disposed;

    /// <summary>Cache duration. gh CLI tokens are long-lived but we re-fetch periodically.</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(50);

    public GhCliAuthProvider(ILogger<GhCliAuthProvider> logger)
    {
        _logger = logger;
    }

    public string AuthScheme => "token";
    public bool RequiresRefresh => _cachedToken is null || DateTime.UtcNow >= _cachedAtUtc.Add(CacheDuration);

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Return cached token if still valid
        if (_cachedToken is not null && DateTime.UtcNow < _cachedAtUtc.Add(CacheDuration))
            return _cachedToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedToken is not null && DateTime.UtcNow < _cachedAtUtc.Add(CacheDuration))
                return _cachedToken;

            _logger.LogInformation("Fetching GitHub token from gh CLI");
            var token = await RunGhAuthTokenAsync(ct);

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "gh CLI returned empty token. Run 'gh auth login' to authenticate.");

            _cachedToken = token.Trim();
            _cachedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("GitHub CLI token acquired, cached for {Minutes} minutes", CacheDuration.TotalMinutes);
            return _cachedToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Validate that the gh CLI is authenticated and can provide a token.
    /// </summary>
    public async Task<(bool Success, string? Username, string? Error)> ValidateAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await GetTokenAsync(ct);
            if (string.IsNullOrEmpty(token))
                return (false, null, "gh CLI returned empty token");

            // Get the authenticated username
            var username = await RunGhCommandAsync("api user --jq .login", ct, timeoutSeconds: 15);
            return (true, username?.Trim(), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub CLI auth validation failed");
            return (false, null, ex.Message);
        }
    }

    private static async Task<string> RunGhAuthTokenAsync(CancellationToken ct)
    {
        return await RunGhCommandAsync("auth token", ct, timeoutSeconds: 10);
    }

    private static async Task<string> RunGhCommandAsync(string args, CancellationToken ct, int timeoutSeconds = 10)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Failed to start 'gh' CLI. Is GitHub CLI installed? Install from https://cli.github.com/");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);

        await process.WaitForExitAsync(timeoutCts.Token);

        if (process.ExitCode != 0)
        {
            var message = stderr.Trim();
            if (message.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "gh CLI is not authenticated. Run 'gh auth login' to authenticate.");

            throw new InvalidOperationException(
                $"gh CLI failed (exit {process.ExitCode}): {message}");
        }

        return stdout;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshLock.Dispose();
    }
}
