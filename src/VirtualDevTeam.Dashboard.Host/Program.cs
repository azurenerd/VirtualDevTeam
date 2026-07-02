using VirtualDevTeam.Core.GitHub;
using VirtualDevTeam.Core.Persistence;
using VirtualDevTeam.Dashboard.Components;
using VirtualDevTeam.Dashboard.Host;
using VirtualDevTeam.Dashboard.Hubs;
using VirtualDevTeam.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Runner API base URL — defaults to the Runner's port
var runnerUrl = builder.Configuration.GetValue("RunnerUrl", "http://localhost:5050")!;
Console.WriteLine($"🔗 Connecting to Runner API at {runnerUrl}");

// Dashboard port — check VirtualDevTeam config first, then standalone override, then default
var dashboardPort = builder.Configuration.GetValue("VirtualDevTeam:Dashboard:StandalonePort",
    builder.Configuration.GetValue("DashboardPort", 5051));
builder.WebHost.UseUrls($"http://localhost:{dashboardPort}");

// Always resolve RCL static web assets — needed for _content/ paths on all machines
builder.WebHost.UseStaticWebAssets();

// Blazor Server + SignalR
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 512 * 1024; // 512KB — matches Runner config
});

// HTTP-based dashboard data service (talks to Runner REST API)
builder.Services.AddHttpClient("RunnerApi", client =>
{
    client.BaseAddress = new Uri(runnerUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<HttpDashboardDataService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("RunnerApi");
    var logger = sp.GetRequiredService<ILogger<HttpDashboardDataService>>();
    return new HttpDashboardDataService(client, logger);
});
builder.Services.AddSingleton<IDashboardDataService>(sp => sp.GetRequiredService<HttpDashboardDataService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HttpDashboardDataService>());
builder.Services.AddSingleton<IPlatformLinkService, PlatformLinkService>();

// HTTP-based configuration service (talks to Runner REST API)
// Uses IHttpClientFactory directly (fresh client per request) to avoid stale connection issues
// when the polling service shares the same handler pool.
// Falls back to direct file I/O when the Runner is unreachable.
var runnerAppSettingsPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath, "..", "VirtualDevTeam.Runner", "appsettings.json"));
builder.Services.AddSingleton<IConfigurationService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetRequiredService<ILogger<HttpConfigurationService>>();
    return new HttpConfigurationService(factory, "RunnerApi", logger, runnerAppSettingsPath);
});

// HTTP-based notification service (polls Runner for gate notifications)
builder.Services.AddSingleton<HttpGateNotificationService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("RunnerApi");
    var logger = sp.GetRequiredService<ILogger<HttpGateNotificationService>>();
    var svc = new HttpGateNotificationService(client, logger);
    svc.Start();
    return svc;
});

// HTTP-based strategies data service (polls Runner for strategy execution data)
builder.Services.AddSingleton<IStrategiesDataService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("RunnerApi");
    var logger = sp.GetRequiredService<ILogger<HttpStrategiesDataService>>();
    return new HttpStrategiesDataService(client, logger);
});

// Director CLI — runs local copilot processes, no Runner dependency
builder.Services.AddSingleton<DirectorCliService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DirectorCliService>());

// Stub services for pages that inject orchestrator types not available standalone
builder.Services.AddStandaloneStubs();

// Mark standalone mode so pages can detect it
builder.Services.AddSingleton(new DashboardMode(IsStandalone: true));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

// ── Diagnostic endpoint to test HTTP calls outside Blazor context ──
app.MapGet("/api/diag/ping-runner", async (IHttpClientFactory factory) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var client = factory.CreateClient("RunnerApi");
    var resp = await client.GetAsync("/api/configuration/current");
    sw.Stop();
    return Results.Ok(new { Status = resp.StatusCode.ToString(), ElapsedMs = sw.ElapsedMilliseconds });
});

app.MapPost("/api/diag/test-save", async (IHttpClientFactory factory) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        using var client = factory.CreateClient("RunnerApi");
        // Step 1: Get current config
        var config = await client.GetFromJsonAsync<VirtualDevTeam.Core.Configuration.VirtualDevTeamConfig>(
            "/api/configuration/current");
        var getMs = sw.ElapsedMilliseconds;

        // Step 2: Save it back (same as what Blazor page does)
        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/configuration/save", content);
        sw.Stop();

        return Results.Ok(new
        {
            Status = resp.StatusCode.ToString(),
            GetMs = getMs,
            SaveMs = sw.ElapsedMilliseconds - getMs,
            TotalMs = sw.ElapsedMilliseconds,
            BodySize = json.Length
        });
    }
    catch (Exception ex)
    {
        sw.Stop();
        return Results.Ok(new { Error = ex.GetType().Name + ": " + ex.Message, ElapsedMs = sw.ElapsedMilliseconds });
    }
});

app.MapHub<AgentHub>("/agenthub");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

Console.WriteLine($"🚀 Dashboard running at http://localhost:{dashboardPort}");
Console.WriteLine($"   Runner API: {runnerUrl}");
Console.WriteLine("   Restart this process freely — agents keep running in the Runner.");

app.Run();

