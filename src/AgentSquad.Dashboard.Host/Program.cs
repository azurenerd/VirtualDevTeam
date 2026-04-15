using AgentSquad.Core.GitHub;
using AgentSquad.Core.Persistence;
using AgentSquad.Dashboard.Components;
using AgentSquad.Dashboard.Host;
using AgentSquad.Dashboard.Hubs;
using AgentSquad.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Runner API base URL — defaults to the Runner's port
var runnerUrl = builder.Configuration.GetValue("RunnerUrl", "http://localhost:5050")!;
Console.WriteLine($"🔗 Connecting to Runner API at {runnerUrl}");

// Dashboard port — check AgentSquad config first, then standalone override, then default
var dashboardPort = builder.Configuration.GetValue("AgentSquad:Dashboard:StandalonePort",
    builder.Configuration.GetValue("DashboardPort", 5051));
builder.WebHost.UseUrls($"http://localhost:{dashboardPort}");

// Always resolve RCL static web assets — needed for _content/ paths on all machines
builder.WebHost.UseStaticWebAssets();

// Blazor Server + SignalR
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();

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

// HTTP-based configuration service (talks to Runner REST API)
// Uses IHttpClientFactory directly (fresh client per request) to avoid stale connection issues
// when the polling service shares the same handler pool.
builder.Services.AddSingleton<IConfigurationService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetRequiredService<ILogger<HttpConfigurationService>>();
    return new HttpConfigurationService(factory, "RunnerApi", logger);
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

// Director CLI — runs local copilot processes, no Runner dependency
builder.Services.AddSingleton<DirectorCliService>();

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
        var config = await client.GetFromJsonAsync<AgentSquad.Core.Configuration.AgentSquadConfig>(
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

