using System.Net;
using System.Net.Http.Json;
using VirtualDevTeam.Dashboard.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace VirtualDevTeam.Dashboard.Unit.Tests;

public class HttpStrategiesDataServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) };

        public readonly List<HttpRequestMessage> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add(req);
            return Task.FromResult(Respond(req));
        }
    }

    private static HttpStrategiesDataService Build(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://runner.local") };
        return new HttpStrategiesDataService(http, NullLogger<HttpStrategiesDataService>.Instance);
    }

    [Fact]
    public async Task GetActiveTasks_calls_active_endpoint()
    {
        var handler = new StubHandler();
        var svc = Build(handler);

        await svc.GetActiveTasksAsync();
        Assert.Single(handler.Requests);
        Assert.Equal("/api/strategies/active", handler.Requests[0].RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task GetRecentTasks_includes_limit_query_string()
    {
        var handler = new StubHandler();
        var svc = Build(handler);

        await svc.GetRecentTasksAsync(limit: 25);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("/api/strategies/recent", req.RequestUri?.AbsolutePath);
        Assert.Contains("limit=25", req.RequestUri?.Query);
    }

    [Fact]
    public async Task GetEnabledAsync_calls_enabled_endpoint_and_deserializes()
    {
        var handler = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EnabledStrategiesInfo(true, new[] { "baseline" })),
            },
        };
        var svc = Build(handler);

        var info = await svc.GetEnabledAsync();

        Assert.True(info.MasterEnabled);
        Assert.Equal(new[] { "baseline" }, info.EnabledStrategies);
    }

    [Fact]
    public async Task Network_failure_on_active_returns_empty_list_instead_of_throwing()
    {
        var handler = new StubHandler { Respond = _ => throw new HttpRequestException("connection refused") };
        var svc = Build(handler);
        Assert.Empty(await svc.GetActiveTasksAsync());
    }

    [Fact]
    public async Task Network_failure_on_recent_returns_empty_list()
    {
        var handler = new StubHandler { Respond = _ => throw new HttpRequestException() };
        var svc = Build(handler);
        Assert.Empty(await svc.GetRecentTasksAsync());
    }

    [Fact]
    public async Task Network_failure_on_enabled_returns_framework_off_info()
    {
        var handler = new StubHandler { Respond = _ => throw new HttpRequestException() };
        var svc = Build(handler);
        var info = await svc.GetEnabledAsync();
        Assert.False(info.MasterEnabled);
        Assert.Empty(info.EnabledStrategies);
    }

    [Fact]
    public async Task Null_response_body_is_treated_as_empty()
    {
        var handler = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            },
        };
        var svc = Build(handler);
        Assert.Empty(await svc.GetActiveTasksAsync());
        Assert.Empty(await svc.GetRecentTasksAsync());
    }

    [Fact]
    public async Task Server_error_is_treated_as_empty_not_thrown()
    {
        var handler = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            },
        };
        var svc = Build(handler);
        Assert.Empty(await svc.GetActiveTasksAsync());
    }
}
