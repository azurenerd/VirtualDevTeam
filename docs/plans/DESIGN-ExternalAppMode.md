# External App Mode Design for VirtualDevTeam

This document describes the design for supporting Playwright testing and UI capture
against externally-running applications — apps already running on the developer's machine
or external servers that VirtualDevTeam should NOT launch.

## Problem Statement

**Current State:**
- PlaywrightRunner always launches the app from an AppStartCommand
- Assumes the app isn't running
- For large projects with apps already running (localhost:3000, localhost:5000), this causes:
  - Port conflicts
  - Unnecessary app restarts
  - Wasted startup time (30-60s per startup)
  - Complex teardown to avoid stale processes

**Goal:**
Support large projects where the dev/staging app is already running, and agents should:
1. Connect to existing running instances
2. Capture screenshots/video without launching anything
3. Handle multiple services (frontend, API, database)
4. Gracefully fall back if the external app restarts mid-capture
5. Work with MCP exploration (should work transparently)

---

## Design: Layered External App Mode

### Layer 1: Configuration (Workspace + Service Registry)

#### 1.1 WorkspaceConfig Extensions

Add to `WorkspaceConfig`:

```csharp
/// <summary>
/// When true, PlaywrightRunner skips app launch and connects to external running instances.
/// Requires ExternalAppUrl and (optionally) ExternalAppHealthCheckUrl to be configured.
/// </summary>
public bool ExternalAppMode { get; set; } = false;

/// <summary>
/// URL of the externally-running application (e.g., "http://localhost:3000").
/// Ignored when ExternalAppMode is false. Takes precedence over AppBaseUrl.
/// </summary>
public string? ExternalAppUrl { get; set; }

/// <summary>
/// Health check endpoint for external app (e.g., "/api/health", "/status", "/alive").
/// Used to verify the app is responding before capture starts.
/// If null, uses the root URL (GET / expecting HTTP 200).
/// </summary>
public string? ExternalAppHealthCheckUrl { get; set; }

/// <summary>
/// Timeout in seconds to wait for external app health check to pass.
/// Lower than AppStartupTimeoutSeconds because the app should already be running.
/// </summary>
public int ExternalAppHealthCheckTimeoutSeconds { get; set; } = 5;

/// <summary>
/// Companion external services by logical name (e.g., "api", "frontend", "admin").
/// Each service has its own URL for multi-service architecture.
/// Example: { "api" => "http://localhost:5000", "frontend" => "http://localhost:3000" }
/// </summary>
public Dictionary<string, string> ExternalServices { get; set; } = [];

/// <summary>
/// When true, if external app health check fails, fall back to attempting a launch
/// (use AppStartCommand). Useful when external app might not be running.
/// When false, fail immediately if health check fails.
/// </summary>
public bool FallbackToLaunchOnExternalFailure { get; set; } = true;
```

#### 1.2 develop-settings.json (runtime config)

Add optional section for projects that use external mode:

```json
{
  "VirtualDevTeam": {
    "Workspace": {
      "ExternalAppMode": true,
      "ExternalAppUrl": "http://localhost:3000",
      "ExternalAppHealthCheckUrl": "/api/health",
      "ExternalAppHealthCheckTimeoutSeconds": 5,
      "FallbackToLaunchOnExternalFailure": false,
      "ExternalServices": {
        "api": "http://localhost:5000",
        "frontend": "http://localhost:3000",
        "admin": "http://localhost:5001"
      }
    }
  }
}
```

### Layer 2: Service Registry (Multi-Service Discovery)

New class: `ExternalServiceRegistry`

```csharp
public sealed class ExternalServiceRegistry
{
    /// <summary>Service definitions keyed by logical name.</summary>
    private readonly Dictionary<string, ExternalService> _services = [];

    /// <summary>Primary service used for browser navigation (usually "frontend").</summary>
    public string PrimaryServiceName { get; init; } = "frontend";

    public sealed class ExternalService
    {
        public required string Name { get; init; }           // "api", "frontend", "admin"
        public required Uri BaseUrl { get; init; }           // http://localhost:5000
        public string? HealthCheckPath { get; init; }        // "/health", "/api/health"
        public int HealthCheckTimeoutSeconds { get; init; } = 5;
        public DateTime LastHealthCheckTime { get; private set; }
        public bool LastHealthCheckPassed { get; private set; }
        public string? LastHealthCheckError { get; private set; }

        /// <summary>Update health check status (called after every probe).</summary>
        public void UpdateHealthStatus(bool passed, string? error = null)
        {
            LastHealthCheckTime = DateTime.UtcNow;
            LastHealthCheckPassed = passed;
            LastHealthCheckError = error;
        }
    }

    /// <summary>Load from config and build the service registry.</summary>
    public static ExternalServiceRegistry Create(WorkspaceConfig config)
    {
        var registry = new ExternalServiceRegistry();

        if (config.ExternalServices.Count > 0)
        {
            foreach (var (name, url) in config.ExternalServices)
            {
                registry._services[name] = new ExternalService
                {
                    Name = name,
                    BaseUrl = new Uri(url),
                    HealthCheckPath = name == "api" ? "/health" : (name == "frontend" ? "/api/health" : null),
                    HealthCheckTimeoutSeconds = config.ExternalAppHealthCheckTimeoutSeconds
                };
            }
        }
        else if (config.ExternalAppUrl != null)
        {
            registry._services["primary"] = new ExternalService
            {
                Name = "primary",
                BaseUrl = new Uri(config.ExternalAppUrl),
                HealthCheckPath = config.ExternalAppHealthCheckUrl,
                HealthCheckTimeoutSeconds = config.ExternalAppHealthCheckTimeoutSeconds
            };
        }

        return registry;
    }

    /// <summary>Get a service by name (e.g., "api", "frontend").</summary>
    public ExternalService? GetService(string name) =>
        _services.TryGetValue(name, out var service) ? service : null;

    /// <summary>Get the primary service for browser navigation.</summary>
    public ExternalService? GetPrimaryService() =>
        GetService(PrimaryServiceName) ?? _services.Values.FirstOrDefault();

    /// <summary>Check health of a specific service or all services.</summary>
    public async Task<Dictionary<string, bool>> ProbeHealthAsync(string? serviceName = null)
    {
        // Returns { "api" => true, "frontend" => false, ... }
        // Updated service status on completion
    }
}
```

### Layer 3: AppLauncher Extensions

Modify `AppLauncher.LaunchVerifiedAppAsync()` to support external mode:

```csharp
public async Task<AppLaunchResult?> LaunchVerifiedAppAsync(
    string workspacePath,
    WorkspaceConfig config,
    Dictionary<string, string> envVars,
    CancellationToken ct)
{
    // ── EXTERNAL APP MODE ──
    if (config.ExternalAppMode)
    {
        var registry = ExternalServiceRegistry.Create(config);
        var primaryService = registry.GetPrimaryService();
        if (primaryService == null)
            throw new InvalidOperationException("ExternalAppMode enabled but no services configured");

        _logger.LogInformation(
            "AppLauncher: external app mode — connecting to {Url}",
            primaryService.BaseUrl);

        // Try to connect to external app with health check
        var health = await registry.ProbeHealthAsync();
        if (!health.Values.Any(h => h))
        {
            // All services down
            if (config.FallbackToLaunchOnExternalFailure)
            {
                _logger.LogWarning("AppLauncher: external app health check failed, falling back to launch");
                // Fall through to normal launch logic below
            }
            else
            {
                _logger.LogError("AppLauncher: external app health check failed and fallback disabled");
                return null;
            }
        }
        else
        {
            // At least one service is healthy — return a synthetic "launch result" 
            // that represents connection to external app
            return new AppLaunchResult
            {
                Process = null,  // No process — already running
                VerifiedUrl = primaryService.BaseUrl.ToString(),
                Port = primaryService.BaseUrl.Port,
                DetectedUrl = primaryService.BaseUrl.ToString(),
                UsedFallback = false,
                DiagnosticNotes = new List<string>
                {
                    $"Connected to external app at {primaryService.BaseUrl}",
                    $"Health check passed for {health.Count(h => h.Value)} service(s)",
                }
            };
        }
    }

    // ── NORMAL LAUNCH (existing logic) ──
    // [existing implementation continues]
}
```

### Layer 4: PlaywrightRunner Health Probing

New method: `PlaywrightRunner.HealthCheckExternalAppAsync()`

```csharp
public async Task<ExternalHealthCheckResult> HealthCheckExternalAppAsync(
    ExternalServiceRegistry registry,
    CancellationToken ct)
{
    var result = new ExternalHealthCheckResult();

    foreach (var service in registry.GetAllServices())
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(service.HealthCheckTimeoutSeconds));

            var healthUrl = service.HealthCheckPath != null
                ? new Uri(service.BaseUrl, service.HealthCheckPath).ToString()
                : service.BaseUrl.ToString();

            using var response = await _httpClient.GetAsync(healthUrl, cts.Token);
            var passed = response.IsSuccessStatusCode;

            service.UpdateHealthStatus(
                passed,
                passed ? null : $"HTTP {response.StatusCode}");

            result.ServiceResults[service.Name] = (passed, response.StatusCode);
            _logger.LogInformation("HealthCheck [{Service}]: {Url} → {StatusCode}",
                service.Name, healthUrl, response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            service.UpdateHealthStatus(false, "Timeout");
            result.ServiceResults[service.Name] = (false, 0);
            _logger.LogWarning("HealthCheck [{Service}]: timeout after {TimeoutSeconds}s",
                service.Name, service.HealthCheckTimeoutSeconds);
        }
        catch (HttpRequestException ex)
        {
            service.UpdateHealthStatus(false, ex.Message);
            result.ServiceResults[service.Name] = (false, 0);
            _logger.LogWarning("HealthCheck [{Service}]: connection error — {Error}",
                service.Name, ex.Message);
        }
    }

    return result;
}

public sealed record ExternalHealthCheckResult
{
    public Dictionary<string, (bool Passed, int? StatusCode)> ServiceResults { get; } = [];
    public bool AllPassed => ServiceResults.Values.All(r => r.Passed);
    public bool AnyPassed => ServiceResults.Values.Any(r => r.Passed);
}
```

### Layer 5: Restart Resilience (Mid-Capture Handling)

New class: `ExternalAppRestartWatcher`

```csharp
public sealed class ExternalAppRestartWatcher : IDisposable
{
    private readonly ExternalServiceRegistry _registry;
    private readonly ILogger<ExternalAppRestartWatcher> _logger;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public EventHandler<ExternalAppRestartedEventArgs>? OnRestartDetected;

    /// <summary>Start monitoring for app restart/crash (polls health every N seconds).</summary>
    public void StartMonitoring(int pollIntervalSeconds = 2)
    {
        _monitorCts = new CancellationTokenSource();
        _monitorTask = MonitorLoopAsync(_monitorCts.Token, pollIntervalSeconds);
    }

    /// <summary>Stop monitoring and return result (was restart detected?).</summary>
    public async Task<bool> StopMonitoringAsync()
    {
        if (_monitorCts != null)
        {
            _monitorCts.Cancel();
            if (_monitorTask != null)
                await _monitorTask;
        }
        return false; // or true if restart was detected
    }

    private async Task MonitorLoopAsync(CancellationToken ct, int pollIntervalSeconds)
    {
        var lastHealthStatus = await _registry.ProbeHealthAsync();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
                var currentHealth = await _registry.ProbeHealthAsync();

                // Detect state change (service went from up to down or vice versa)
                foreach (var (name, wasUp) in lastHealthStatus)
                {
                    if (currentHealth.TryGetValue(name, out var isUp) && wasUp != isUp)
                    {
                        _logger.LogWarning("ExternalAppRestartWatcher: {Service} changed state {From} → {To}",
                            name, wasUp ? "UP" : "DOWN", isUp ? "UP" : "DOWN");

                        OnRestartDetected?.Invoke(this, new ExternalAppRestartedEventArgs(
                            ServiceName = name,
                            WasUp = wasUp,
                            IsNowUp = isUp,
                            TimestampUtc = DateTime.UtcNow));
                    }
                }

                lastHealthStatus = currentHealth;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExternalAppRestartWatcher: error during monitoring");
            }
        }
    }

    public void Dispose()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorTask?.Dispose();
    }
}

public sealed record ExternalAppRestartedEventArgs(
    string ServiceName,
    bool WasUp,
    bool IsNowUp,
    DateTime TimestampUtc);
```

---

## Integration Points

### 1. MediaCaptureGate (No Changes Needed)

MediaCaptureGate.ShouldCapture() already works — it determines whether to capture based on
task description and file changes. It's agnostic to app mode.

### 2. CaptureMode + PlaywrightRunner

Both ScreenshotOnly and FullMedia modes work with external apps:
- **ScreenshotOnly**: Connects to external app, captures PNG, no MCP/video
- **FullMedia**: Connects to external app, runs MCP exploration, captures screenshots + video

```csharp
public async Task<AppInteractionResult?> RunUITestsAsync(
    string workspacePath,
    WorkspaceConfig config,
    List<string> testUrls,
    CaptureMode mode,
    CancellationToken ct)
{
    // Existing logic but with support for external app:
    var launchResult = await _appLauncher.LaunchVerifiedAppAsync(workspacePath, config, envVars, ct);
    
    if (launchResult == null)
        return null;

    // launchResult can now represent:
    // - A launched process (Process != null)
    // - An external connection (Process == null, but VerifiedUrl is set)
    // The rest of the capture pipeline is identical

    // Run capture with restart watcher
    using var restartWatcher = new ExternalAppRestartWatcher(_registry, _logger);
    if (config.ExternalAppMode)
        restartWatcher.StartMonitoring();

    try
    {
        // Capture screenshots/video/MCP (existing implementation)
        return await CaptureWithPlaywrightAsync(launchResult.BrowserUrl, testUrls, mode, ct);
    }
    finally
    {
        if (await restartWatcher.StopMonitoringAsync())
        {
            _logger.LogWarning("External app restarted during capture — results may be incomplete");
            // Return partial results or null depending on capture phase
        }

        // Cleanup: if we launched, kill the process. If external, don't touch it.
        if (launchResult.Process != null)
            await TerminateAppAsync(launchResult.Process);
    }
}
```

### 3. MCP Exploration (Automatic)

MCP exploration works unchanged — it receives a URL and navigates:
```csharp
// MCP prompt data includes the URL:
// "The application is running at http://localhost:3000"
// The MCP CLI session navigates and explores — no app launch needed
```

### 4. Dual Capture (Parallel Branches)

Both `CaptureDirect` and `CaptureMcp` branches work with external apps:
```
External App Mode ON
  ├─ AppLauncher.LaunchVerifiedAppAsync()
  │   └─ Connects to external app (no process spawned)
  │
  ├─ CaptureDirect (existing Playwright C# code)
  │   └─ Uses VerifiedUrl from connection result
  │
  └─ CaptureMcp (existing MCP code)
      └─ Uses VerifiedUrl from connection result (passed in prompt)
```

---

## Decision Tree: External vs Launch Mode

```
┌─ ExternalAppMode enabled?
│
├─ YES:
│  ├─ ExternalAppUrl configured?
│  │  ├─ YES: Health check ExternalAppUrl + ExternalServices
│  │  │   ├─ Health check passes: Connect (no process), proceed with capture
│  │  │   └─ Health check fails:
│  │  │       ├─ FallbackToLaunchOnExternalFailure=true: Fall through to launch
│  │  │       └─ FallbackToLaunchOnExternalFailure=false: Fail, return null
│  │  │
│  │  └─ NO: Error (ExternalAppMode requires config)
│  │
│  └─ [Proceed with capture using external URLs]
│
└─ NO:
   ├─ AppStartCommand configured?
   │  ├─ YES: Launch app normally
   │  └─ NO: Auto-detect and launch
   │
   └─ [Proceed with capture using launched process]
```

---

## Example Configurations

### Example 1: React Frontend Only (External)

```json
{
  "VirtualDevTeam": {
    "Workspace": {
      "ExternalAppMode": true,
      "ExternalAppUrl": "http://localhost:3000",
      "ExternalAppHealthCheckUrl": "/api/health",
      "FallbackToLaunchOnExternalFailure": false
    }
  }
}
```

**Behavior:**
- Connect to http://localhost:3000 (dev server already running)
- Health check via GET /api/health
- Screenshots: navigate to / and capture
- MCP exploration: use http://localhost:3000

### Example 2: Multi-Service (Frontend + API + Admin)

```json
{
  "VirtualDevTeam": {
    "Workspace": {
      "ExternalAppMode": true,
      "ExternalServices": {
        "frontend": "http://localhost:3000",
        "api": "http://localhost:5000",
        "admin": "http://localhost:5001"
      }
    }
  }
}
```

**Behavior:**
- Check health of all three services
- Primary service for browser: frontend (http://localhost:3000)
- Test URLs can reference other services: "http://localhost:5000/api/docs"
- MCP exploration uses frontend URL

### Example 3: Hybrid (External with Fallback)

```json
{
  "VirtualDevTeam": {
    "Workspace": {
      "ExternalAppMode": true,
      "ExternalAppUrl": "http://localhost:3000",
      "FallbackToLaunchOnExternalFailure": true,
      "AppStartCommand": "npm run dev"
    }
  }
}
```

**Behavior:**
- Try to connect to http://localhost:3000
- If not responding: fall back to `npm run dev`
- Useful for optional external app (sometimes running, sometimes not)

---

## FAQ

### Q: What if the external app restarts during capture?

**A:** `ExternalAppRestartWatcher` detects the restart and:
1. Logs a warning
2. Fires `OnRestartDetected` event
3. Capture pipeline can:
   - Abort with partial results
   - Retry the capture
   - Mark the run as incomplete

Current implementation: log and continue (assume brief restart < network timeout).

### Q: How do we know which URL to test when the project is huge?

**A:** Same as today:
1. Task description contains `## Visual Verification` section with URLs
2. Or Test Engineer generates URL list from app structure / API docs
3. Or default to primary service root URL
4. MCP exploration discovers additional URLs autonomously

### Q: Can we capture if only one service is up?

**A:** Yes. `ExternalServiceRegistry.ProbeHealthAsync()` returns per-service status.
- If primary service is up, proceed with capture
- If only secondary service is up, proceed if tasks don't depend on primary

### Q: What if external app URL changes between runs?

**A:** Update `develop-settings.json` and restart the runner. No code changes needed.

### Q: Does video recording work with external apps?

**A:** Yes — `MediaRecorder` captures video of browser navigation regardless of app source.
Same as launched apps.

### Q: What about cleanup?

**A:**
- **Launched process:** Kill on completion (existing behavior)
- **External connection:** Do not touch (app is user-owned)
- **Playwright temp files:** Clean up same as today

### Q: Can we mix external and launched apps in the same run?

**A:** Yes, but not recommended. Each service in `ExternalServices` or each agent workspace
should be consistent (all external or all launched). Mixing creates confusion about cleanup/restarts.

---

## Implementation Roadmap

### Phase 1: Foundation (minimal)
- Add WorkspaceConfig properties (ExternalAppMode, ExternalAppUrl, ExternalAppHealthCheckUrl)
- Extend AppLauncher.LaunchVerifiedAppAsync() to detect external mode and return synthetic result
- Parse develop-settings.json external app section

### Phase 2: Service Registry
- Implement ExternalServiceRegistry with health probing
- Support multiple services (frontend, api, admin)
- Integrate with AppLauncher and PlaywrightRunner

### Phase 3: Resilience
- Implement ExternalAppRestartWatcher
- Handle mid-capture restart gracefully
- Log diagnostics

### Phase 4: Documentation & Testing
- Update wizard to prompt for external app mode
- Add unit tests for health probing
- Integration test: external app mode vs launched mode

### Phase 5: Dashboard UI
- Add External App Mode section to Configuration page
- Show service health status in real-time
- Allow manual service health check

---

## Testing Strategy

### Unit Tests

1. **MediaCaptureGate**: No changes needed (already UI-agnostic)
2. **ExternalServiceRegistry**: 
   - Create from config ✓
   - Probe health (mock HttpClient) ✓
   - Fallback priority ✓
3. **ExternalAppRestartWatcher**:
   - Detect state change ✓
   - Event firing ✓
4. **AppLauncher**:
   - External mode vs launch mode ✓
   - Health check failure + fallback ✓

### Integration Tests

1. **PlaywrightRunner with external app**:
   - Start mock HTTP server on localhost:3000
   - Configure ExternalAppMode=true
   - Capture screenshots from running server ✓
2. **Multi-service**:
   - Run 3 mock services
   - Verify all health checks pass
   - Use frontend for browser, API for test URLs ✓

### E2E Tests

1. Real project with dev server running
2. Configure external app mode in develop-settings.json
3. Run agent workflow, verify screenshots taken without relaunching app

---

## Thread Safety & Concurrency

- `ExternalServiceRegistry`: Thread-safe (immutable services, atomic health status updates)
- `ExternalAppRestartWatcher`: One per capture session (not shared)
- HTTP health checks: Serialized per service to avoid connection saturation
- Dual capture: Both MCP and Direct branches can run in parallel against same external app

---

## Future Enhancements

1. **Service dependency graph** — specify that Admin requires API; fail gracefully if one is down
2. **Custom health check endpoints** — plugin architecture for non-standard services
3. **Load balancing** — round-robin between multiple instances of same service
4. **Metrics** — track health check latencies, restart frequency, capture success rates
5. **Circuit breaker** — stop trying to connect after N consecutive failures
6. **Caching** — cache health check results (30s TTL) to reduce probe frequency

