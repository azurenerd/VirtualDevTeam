namespace VirtualDevTeam.Core.DevPlatform.Auth;

/// <summary>
/// Provides authentication tokens for the dev platform.
/// Implementations: PAT (both platforms), Azure CLI bearer (ADO), Service Principal (ADO future).
/// </summary>
public interface IDevPlatformAuthProvider
{
    /// <summary>Get a valid authentication token. Handles refresh internally.</summary>
    Task<string> GetTokenAsync(CancellationToken ct = default);

    /// <summary>Whether the current token needs refresh (e.g., nearing expiry).</summary>
    bool RequiresRefresh { get; }

    /// <summary>Authentication scheme: "token" for PAT, "Bearer" for OAuth/az CLI.</summary>
    string AuthScheme { get; }
}

/// <summary>
/// Simple PAT-based auth provider. Works for both GitHub and Azure DevOps.
/// </summary>
public sealed class PatAuthProvider : IDevPlatformAuthProvider
{
    private readonly string _token;

    public PatAuthProvider(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _token = token;
    }

    public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult(_token);
    public bool RequiresRefresh => false;
    public string AuthScheme => "token";
}

/// <summary>
/// Static bearer token provider for manually-supplied ADO bearer tokens.
/// Bearer tokens expire (~1 hour) — use for testing or short-lived runs.
/// For auto-refresh, use <see cref="VirtualDevTeam.Core.DevPlatform.Providers.AzureDevOps.AzureCliBearerProvider"/>.
/// </summary>
public sealed class StaticBearerAuthProvider : IDevPlatformAuthProvider
{
    private readonly string _token;

    public StaticBearerAuthProvider(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _token = token;
    }

    public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult(_token);
    public bool RequiresRefresh => false;
    public string AuthScheme => "Bearer";
}
