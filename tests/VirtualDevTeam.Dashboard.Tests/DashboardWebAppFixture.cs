using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VirtualDevTeam.Dashboard.Tests;

/// <summary>
/// Ensures the Dashboard is running for Playwright tests. Checks if it's already up;
/// if not, starts it as a real process. Uses fast health checks (200ms intervals, 60s max).
/// Cleans up the process on dispose if it started one.
/// </summary>
public class DashboardWebAppFixture : IAsyncLifetime
{
    private Process? _dashboardProcess;
    private bool _weStartedIt;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    public string BaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Try the default standalone port first
        var port = 5051;
        BaseUrl = $"http://localhost:{port}";

        // Check if dashboard is already running
        if (await IsRespondingAsync(BaseUrl))
            return; // Already up - use it

        // Not running - start it ourselves
        var dashboardDll = FindDashboardDll();
        if (dashboardDll is null)
            throw new InvalidOperationException(
                "Could not find VirtualDevTeam.Dashboard.dll. Build the dashboard project first.");

        // Pick a free port to avoid conflicts
        port = GetFreePort();
        BaseUrl = $"http://localhost:{port}";

        _dashboardProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{dashboardDll}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        // Use the real Runner appsettings.json so the dashboard has real GitHub config.
        // Only fall back to dummy config if appsettings.json isn't found.
        _dashboardProcess.StartInfo.Environment["ASPNETCORE_URLS"] = BaseUrl;
        _dashboardProcess.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        _dashboardProcess.StartInfo.Environment["VirtualDevTeam__Dashboard__StandalonePort"] = port.ToString();

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        var appSettings = repoRoot is not null
            ? Path.Combine(repoRoot, "src", "VirtualDevTeam.Runner", "appsettings.json")
            : null;

        if (appSettings is null || !File.Exists(appSettings))
        {
            // No real config available — use minimal dummy values so the app can start
            _dashboardProcess.StartInfo.Environment["VirtualDevTeam__Project__GitHubRepo"] = "test-owner/test-repo";
            _dashboardProcess.StartInfo.Environment["VirtualDevTeam__Project__GitHubToken"] = "ghp_placeholder_for_ui_tests";
        }

        // Drain stdout/stderr async so the child process doesn't deadlock on a
        // full pipe buffer (~4KB on Windows). Without this, the dashboard blocks
        // on its first chunky log batch and the fixture times out.
        _dashboardProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_stdout) _stdout.AppendLine(e.Data);
        };
        _dashboardProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data);
        };

        _dashboardProcess.Start();
        _dashboardProcess.BeginOutputReadLine();
        _dashboardProcess.BeginErrorReadLine();
        _weStartedIt = true;

        // Fast health check: 200ms intervals, 60s timeout (Blazor cold start on
        // Windows with SignalR + full DI graph can take >15s on a fresh machine).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_dashboardProcess.HasExited)
            {
                throw new InvalidOperationException(
                    $"Dashboard process exited with code {_dashboardProcess.ExitCode}. " +
                    $"Stderr:\n{SnapshotStderr()}\nStdout:\n{SnapshotStdout()}");
            }

            if (await IsRespondingAsync(BaseUrl))
                return; // Dashboard is up and responding

            await Task.Delay(200);
        }

        throw new InvalidOperationException(
            $"Dashboard did not become ready at {BaseUrl} within 60 seconds (PID: {_dashboardProcess.Id}).\n" +
            $"Captured stdout:\n{SnapshotStdout()}\nCaptured stderr:\n{SnapshotStderr()}");
    }

    private string SnapshotStdout() { lock (_stdout) return _stdout.ToString(); }
    private string SnapshotStderr() { lock (_stderr) return _stderr.ToString(); }

    public Task DisposeAsync()
    {
        if (_weStartedIt && _dashboardProcess is { HasExited: false })
        {
            try
            {
                _dashboardProcess.Kill(entireProcessTree: true);
                _dashboardProcess.WaitForExit(3000);
            }
            catch { }
        }
        _dashboardProcess?.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<bool> IsRespondingAsync(string url)
    {
        try
        {
            // Generous timeout: the dashboard root pre-renders Blazor + makes
            // RunnerApi calls to localhost:5050 which will be absent in tests,
            // so the first response can take 5-10s while those HTTP calls
            // fail over. A short timeout here makes the probe impossible.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var resp = await http.GetAsync(url);
            return (int)resp.StatusCode < 500; // Any non-5xx means the app is serving
        }
        catch
        {
            return false;
        }
    }

    private static string? FindDashboardDll()
    {
        // Walk up from test output dir to find the Dashboard build output
        var testDir = AppContext.BaseDirectory;
        var repoRoot = FindRepoRoot(testDir);
        if (repoRoot is null) return null;

        // Check common build output locations
        var candidates = new[]
        {
            Path.Combine(repoRoot, "src", "VirtualDevTeam.Dashboard", "bin", "Debug", "net8.0", "VirtualDevTeam.Dashboard.dll"),
            Path.Combine(repoRoot, "src", "VirtualDevTeam.Dashboard", "bin", "Release", "net8.0", "VirtualDevTeam.Dashboard.dll"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = startDir;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "VirtualDevTeam.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
