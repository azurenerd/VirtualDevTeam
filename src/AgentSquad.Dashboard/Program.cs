using AgentSquad.Dashboard.Components;
using AgentSquad.Dashboard.Hubs;
using AgentSquad.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();
builder.Services.AddSingleton<DashboardDataService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardDataService>());
builder.Services.AddScoped<EngineeringPlanDataService>();
builder.Services.AddSingleton<DirectorCliService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapHub<AgentHub>("/agenthub");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
