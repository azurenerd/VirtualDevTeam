using Microsoft.Extensions.Logging;

namespace AgentSquad.Core.GitHub;

/// <summary>
/// Wraps GitHub API calls with rate-limit-aware pausing and retry.
/// Strategy: use GitHub's reset timestamp to sleep the exact right amount of time,
/// rather than blind exponential backoff that could spiral to absurd waits.
/// When remaining quota is low, proactively slow down to avoid hitting the wall.
/// </summary>
public class RateLimitManager
{
    private readonly SemaphoreSlim _semaphore = new(10, 10); // Allow 10 concurrent API calls
    private readonly ILogger<RateLimitManager> _logger;
    private readonly object _stateLock = new();

    private volatile int _remaining = int.MaxValue;
    private DateTime _resetAt = DateTime.MinValue;
    private DateTime _pauseUntil = DateTime.MinValue; // Shared pause: all callers wait

    // Thresholds for proactive throttling
    private const int SlowdownThreshold = 200;   // Add delay between calls
    private const int HeavySlowdownThreshold = 50; // Longer delay
    private const int BlockThreshold = 10;        // Full pause until reset

    private const int MaxRetries = 3;
    private static readonly TimeSpan ResetBuffer = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowdownDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HeavySlowdownDelay = TimeSpan.FromSeconds(2);

    public RateLimitManager(ILogger<RateLimitManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Current remaining API calls (thread-safe read).</summary>
    public int Remaining => _remaining;

    /// <summary>When the current rate limit window resets.</summary>
    public DateTime ResetAtUtc => _resetAt;

    /// <summary>True when all API calls are paused due to rate limiting.</summary>
    public bool IsRateLimited => _pauseUntil > DateTime.UtcNow;

    /// <summary>
    /// Execute a GitHub API call with rate-limit-aware throttling and retry.
    /// On rate limit: waits until the exact reset timestamp + buffer, then retries.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> apiCall, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                // If a prior call triggered a global pause, wait it out
                await WaitForPauseAsync(ct);
                await ThrottleIfNeededAsync(ct);

                var result = await apiCall(ct);
                return result;
            }
            catch (Octokit.RateLimitExceededException ex)
            {
                // Primary rate limit: GitHub tells us exactly when it resets
                var resetAt = ex.Reset.UtcDateTime;
                SetGlobalPause(resetAt, 0);

                if (attempt >= MaxRetries) throw;

                var waitTime = resetAt - DateTime.UtcNow + ResetBuffer;
                if (waitTime > TimeSpan.Zero)
                {
                    _logger.LogWarning(
                        "Rate limit exceeded ({Remaining} remaining). Pausing ALL API calls for {Minutes:F1} minutes until {ResetAt:HH:mm:ss} UTC",
                        0, waitTime.TotalMinutes, resetAt + ResetBuffer);
                    await Task.Delay(waitTime, ct);
                }
            }
            catch (Octokit.AbuseException ex)
            {
                // Secondary rate limit: use Retry-After if provided, else 60s
                if (attempt >= MaxRetries) throw;

                var retryAfter = TimeSpan.FromSeconds(ex.RetryAfterSeconds ?? 60);
                var pauseUntil = DateTime.UtcNow + retryAfter;
                SetGlobalPause(pauseUntil, _remaining);

                _logger.LogWarning(
                    "Secondary rate limit (abuse detection). Pausing for {Seconds}s (attempt {Attempt}/{Max})",
                    retryAfter.TotalSeconds, attempt + 1, MaxRetries);
                await Task.Delay(retryAfter, ct);
            }
            catch (Octokit.ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Generic 403 — may be rate limit without proper headers.
                // Try to get reset time from state; if unknown, wait 60s.
                if (attempt >= MaxRetries) throw;

                TimeSpan waitTime;
                lock (_stateLock)
                {
                    waitTime = _resetAt > DateTime.UtcNow
                        ? _resetAt - DateTime.UtcNow + ResetBuffer
                        : TimeSpan.FromSeconds(60);
                }
                var pauseUntil = DateTime.UtcNow + waitTime;
                SetGlobalPause(pauseUntil, 0);

                _logger.LogWarning(
                    "403 Forbidden (likely rate limit). Pausing for {Minutes:F1} minutes (attempt {Attempt}/{Max})",
                    waitTime.TotalMinutes, attempt + 1, MaxRetries);
                await Task.Delay(waitTime, ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        throw new InvalidOperationException("Exhausted all retries (should not reach here)");
    }

    /// <summary>
    /// Execute a void GitHub API call with rate-limit-aware throttling.
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
    /// Update tracked rate limit from response headers. Call after each successful API call.
    /// </summary>
    public void UpdateRateLimit(int remaining, DateTime resetAtUtc)
    {
        lock (_stateLock)
        {
            _remaining = remaining;
            _resetAt = resetAtUtc;
        }
    }

    /// <summary>
    /// Sets a global pause that causes ALL callers to wait before making API calls.
    /// </summary>
    private void SetGlobalPause(DateTime pauseUntilUtc, int remaining)
    {
        lock (_stateLock)
        {
            _remaining = remaining;
            // Only extend the pause, never shorten it
            if (pauseUntilUtc > _pauseUntil)
                _pauseUntil = pauseUntilUtc;
            if (pauseUntilUtc > _resetAt)
                _resetAt = pauseUntilUtc;
        }
    }

    /// <summary>
    /// If a global pause is active, wait until it expires.
    /// </summary>
    private async Task WaitForPauseAsync(CancellationToken ct)
    {
        DateTime pauseUntil;
        lock (_stateLock)
        {
            pauseUntil = _pauseUntil;
        }

        if (pauseUntil > DateTime.UtcNow)
        {
            var wait = pauseUntil - DateTime.UtcNow;
            _logger.LogDebug("Global API pause active. Waiting {Seconds:F0}s", wait.TotalSeconds);
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>
    /// Proactive throttling: slow down when remaining quota is low to avoid hitting the wall.
    /// </summary>
    private async Task ThrottleIfNeededAsync(CancellationToken ct)
    {
        var remaining = _remaining;

        if (remaining < BlockThreshold && DateTime.UtcNow < _resetAt)
        {
            var waitTime = _resetAt - DateTime.UtcNow + ResetBuffer;
            _logger.LogWarning(
                "Rate limit critically low ({Remaining} remaining). Pausing {Minutes:F1} min until reset",
                remaining, waitTime.TotalMinutes);
            SetGlobalPause(_resetAt + ResetBuffer, remaining);
            await Task.Delay(waitTime, ct);
        }
        else if (remaining < HeavySlowdownThreshold)
        {
            await Task.Delay(HeavySlowdownDelay, ct);
        }
        else if (remaining < SlowdownThreshold)
        {
            await Task.Delay(SlowdownDelay, ct);
        }
    }
}
