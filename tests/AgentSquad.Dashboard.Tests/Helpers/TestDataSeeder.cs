namespace AgentSquad.Dashboard.Tests.Helpers;

/// <summary>
/// Provides test data context information. The dashboard fetches data from the Runner API;
/// this helper detects whether the Runner is available and adjusts test expectations accordingly.
/// When Runner is not available, tests validate UI structure and interactions against empty states.
/// When Runner is available, tests validate data-rich scenarios.
/// </summary>
public static class TestDataSeeder
{
    /// <summary>Check if the Runner API is responding on port 5050.</summary>
    public static async Task<bool> IsRunnerAvailableAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync("http://localhost:5050/api/dashboard/agents");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Check if the dashboard has agents loaded (non-empty overview).</summary>
    public static async Task<bool> HasAgentDataAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetStringAsync("http://localhost:5050/api/dashboard/agents");
            return resp.Length > 10 && resp.Contains("agentId", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
