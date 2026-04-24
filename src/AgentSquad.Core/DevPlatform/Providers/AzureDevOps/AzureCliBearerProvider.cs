using System.Diagnostics;
using AgentSquad.Core.DevPlatform.Auth;
using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.DevPlatform.Providers.AzureDevOps;

/// <summary>
/// Acquires bearer tokens for Azure DevOps using the Azure CLI.
/// Uses: az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798
/// Auto-refreshes tokens 5 minutes before expiry.
/// This is a dev/personal fallback — production should use service principals.
/// </summary>
public sealed class AzureCliBearerProvider : IDevPlatformAuthProvider, IDisposable
{
    private readonly ILogger<AzureCliBearerProvider> _logger;
    private readonly string? _tenantId;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedToken;
    private DateTime _expiresAtUtc = DateTime.MinValue;
    private bool _disposed;

    /// <summary>ADO resource ID for token scope.</summary>
    private const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    public AzureCliBearerProvider(ILogger<AzureCliBearerProvider> logger, string? tenantId = null)
    {
        _logger = logger;
        _tenantId = tenantId;
    }

    public string ProviderName => "AzureCliBearer";
    public string AuthScheme => "Bearer";
    public bool RequiresRefresh => _cachedToken is null || DateTime.UtcNow >= _expiresAtUtc.AddMinutes(-5);

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Return cached token if still valid (with 5-minute buffer)
        if (_cachedToken is not null && DateTime.UtcNow < _expiresAtUtc.AddMinutes(-5))
            return _cachedToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedToken is not null && DateTime.UtcNow < _expiresAtUtc.AddMinutes(-5))
                return _cachedToken;

            _logger.LogInformation("Refreshing Azure CLI bearer token for ADO");
            var (token, expiresAt) = await AcquireTokenAsync(ct);
            _cachedToken = token;
            _expiresAtUtc = expiresAt;
            _logger.LogInformation("Azure CLI token acquired, expires at {ExpiresAt:u}", _expiresAtUtc);
            return _cachedToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await GetTokenAsync(ct);
            return !string.IsNullOrEmpty(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure CLI bearer token validation failed");
            return false;
        }
    }

    private async Task<(string Token, DateTime ExpiresAt)> AcquireTokenAsync(CancellationToken ct)
    {
        // Step 1: Ensure we're logged into the right tenant
        if (!string.IsNullOrEmpty(_tenantId))
        {
            await RunAzCommandAsync($"login --tenant {_tenantId} --allow-no-subscriptions", ct, timeoutSeconds: 60);
        }

        // Step 2: Get the access token
        var token = await RunAzCommandAsync(
            $"account get-access-token --resource {AdoResourceId} --query accessToken -o tsv",
            ct, timeoutSeconds: 30);

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Azure CLI returned empty token. Run 'az login' manually and retry.");

        // ADO bearer tokens typically expire in 1 hour
        var expiresAt = DateTime.UtcNow.AddMinutes(55);

        return (token.Trim(), expiresAt);
    }

    private static async Task<string> RunAzCommandAsync(string args, CancellationToken ct, int timeoutSeconds = 30)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "az",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'az' CLI. Is Azure CLI installed?");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);

        await process.WaitForExitAsync(timeoutCts.Token);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Azure CLI failed (exit {process.ExitCode}): {stderr.Trim()}");
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
