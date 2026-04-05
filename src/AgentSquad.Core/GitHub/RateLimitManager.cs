using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.GitHub;

/// <summary>
/// Wraps IGitHubService calls with rate-limit-aware queuing and exponential backoff.
/// </summary>
public class RateLimitManager
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<RateLimitManager> _logger;

    private int _remaining = int.MaxValue;
    private DateTime _resetAt = DateTime.MinValue;

    private const int SlowdownThreshold = 100;
    private const int BlockThreshold = 10;
    private const int MaxRetries = 5;
    private static readonly TimeSpan SlowdownDelay = TimeSpan.FromMilliseconds(500);

    public RateLimitManager(ILogger<RateLimitManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Execute a GitHub API call with rate-limit-aware throttling and retry logic.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> apiCall, CancellationToken ct = default)
    {
        var retryCount = 0;

        while (true)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                await ThrottleIfNeededAsync(ct);
                var result = await apiCall(ct);
                return result;
            }
            catch (Octokit.RateLimitExceededException ex)
            {
                _remaining = 0;
                _resetAt = ex.Reset.UtcDateTime;
                _logger.LogWarning("Rate limit exceeded. Resets at {ResetAt}", _resetAt);

                if (retryCount >= MaxRetries)
                    throw;

                var waitTime = _resetAt - DateTime.UtcNow;
                if (waitTime > TimeSpan.Zero)
                {
                    _logger.LogInformation("Waiting {Seconds}s for rate limit reset", waitTime.TotalSeconds);
                    await Task.Delay(waitTime, ct);
                }
            }
            catch (Octokit.AbuseException ex)
            {
                if (retryCount >= MaxRetries)
                    throw;

                var backoff = TimeSpan.FromSeconds(Math.Pow(2, retryCount) * (ex.RetryAfterSeconds ?? 1));
                _logger.LogWarning("Abuse detection triggered. Backing off for {Seconds}s", backoff.TotalSeconds);
                await Task.Delay(backoff, ct);
            }
            catch (Octokit.ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                if (retryCount >= MaxRetries)
                    throw;

                var backoff = TimeSpan.FromSeconds(Math.Pow(2, retryCount));
                _logger.LogWarning("403 Forbidden. Backing off for {Seconds}s (attempt {Attempt}/{Max})",
                    backoff.TotalSeconds, retryCount + 1, MaxRetries);
                await Task.Delay(backoff, ct);
            }
            finally
            {
                _semaphore.Release();
            }

            retryCount++;
        }
    }

    /// <summary>
    /// Execute a void GitHub API call with rate-limit-aware throttling and retry logic.
    /// </summary>
    public async Task ExecuteAsync(Func<CancellationToken, Task> apiCall, CancellationToken ct = default)
    {
        await ExecuteAsync<object?>(async token =>
        {
            await apiCall(token);
            return null;
        }, ct);
    }

    /// <summary>
    /// Update the tracked rate limit info (call after receiving rate limit data).
    /// </summary>
    public void UpdateRateLimit(int remaining, DateTime resetAt)
    {
        _remaining = remaining;
        _resetAt = resetAt;
    }

    private async Task ThrottleIfNeededAsync(CancellationToken ct)
    {
        if (_remaining < BlockThreshold && DateTime.UtcNow < _resetAt)
        {
            var waitTime = _resetAt - DateTime.UtcNow;
            _logger.LogWarning("Rate limit critically low ({Remaining}). Waiting {Seconds}s for reset",
                _remaining, waitTime.TotalSeconds);
            await Task.Delay(waitTime, ct);
        }
        else if (_remaining < SlowdownThreshold)
        {
            _logger.LogDebug("Rate limit low ({Remaining}). Adding delay", _remaining);
            await Task.Delay(SlowdownDelay, ct);
        }
    }
}
