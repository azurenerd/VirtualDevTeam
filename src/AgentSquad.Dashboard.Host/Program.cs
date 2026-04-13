using AgentSquad.Dashboard.Components;
using AgentSquad.Dashboard.Host;
using AgentSquad.Dashboard.Hubs;
using AgentSquad.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Runner API base URL — defaults to the Runner's port
var runnerUrl = builder.Configuration.GetValue("RunnerUrl", "http://localhost:5050")!;
Console.WriteLine($"🔗 Connecting to Runner API at {runnerUrl}");

// Dashboard port — use a different port than the Runner
var dashboardPort = builder.Configuration.GetValue("DashboardPort", 5051);
builder.WebHost.UseUrls($"http://localhost:{dashboardPort}");

// Ensure RCL static web assets are served
if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

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
builder.Services.AddSingleton<IConfigurationService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("RunnerApi");
    var logger = sp.GetRequiredService<ILogger<HttpConfigurationService>>();
    return new HttpConfigurationService(client, logger);
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

app.MapHub<AgentHub>("/agenthub");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

Console.WriteLine($"🚀 Dashboard running at http://localhost:{dashboardPort}");
Console.WriteLine($"   Runner API: {runnerUrl}");
Console.WriteLine("   Restart this process freely — agents keep running in the Runner.");

app.Run();

