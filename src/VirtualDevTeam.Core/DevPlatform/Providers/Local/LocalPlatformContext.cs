using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// Shared context for all Local platform services. Provides a SQLite connection factory,
/// bare repo manager, run ID, and schema helper. Registered as a singleton.
/// Auto-initializes on first use from VirtualDevTeamConfig when Platform=Local.
/// </summary>
public sealed class LocalPlatformContext : IDisposable
{
    private readonly ILogger<LocalPlatformContext> _logger;
    private readonly IOptions<VirtualDevTeamConfig> _config;
    private string? _connectionString;
    private bool _disposed;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public LocalPlatformContext(
        LocalBareRepoManager bareRepo,
        LocalPlatformSchema schema,
        IOptions<VirtualDevTeamConfig> config,
        ILogger<LocalPlatformContext> logger)
    {
        BareRepo = bareRepo;
        Schema = schema;
        _config = config;
        _logger = logger;
    }

    public LocalBareRepoManager BareRepo { get; }
    public LocalPlatformSchema Schema { get; }
    public string RunId { get; private set; } = "";
    public string RepoName { get; private set; } = "";
    public string DefaultBranch { get; private set; } = "main";

    /// <summary>
    /// Create a new SQLite connection from the stored connection string.
    /// Auto-initializes on first call if not yet initialized.
    /// </summary>
    public SqliteConnection CreateConnection()
    {
        if (_connectionString is null)
            EnsureInitializedSync();

        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // Enable FK constraints — SQLite does not enforce them by default
        using var fkCmd = conn.CreateCommand();
        fkCmd.CommandText = "PRAGMA foreign_keys = ON;";
        fkCmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Lazy auto-initialization from config. Called on first CreateConnection if not explicitly initialized.
    /// Uses workspace root (not AppContext.BaseDirectory) and persists RunId to disk for restart survival.
    /// </summary>
    private void EnsureInitializedSync()
    {
        if (_initialized) return;
        _initLock.Wait();
        try
        {
            if (_initialized) return;

            var cfg = _config.Value;
            var repoName = cfg.Project.GitHubRepo?.Split('/').LastOrDefault() ?? "local-project";

            // Use workspace root (resolved from config), not AppContext.BaseDirectory
            var workspaceRoot = cfg.Workspace?.RootPath ?? ".agents";
            if (!Path.IsPathRooted(workspaceRoot))
            {
                // Resolve relative paths against the Runner project directory
                var runnerDir = AppContext.BaseDirectory;
                // Walk up from bin/Debug/net8.0/ to project root
                var candidates = new[]
                {
                    Path.GetFullPath(Path.Combine(runnerDir, "..", "..", "..", workspaceRoot)),
                    Path.GetFullPath(Path.Combine(runnerDir, workspaceRoot)),
                };
                workspaceRoot = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
            }

            var ldpPath = Path.Combine(workspaceRoot, "local-platform");
            Directory.CreateDirectory(ldpPath);

            // Build upstream URL using credential helpers — never embed tokens in URLs
            string? upstreamUrl = null;
            if (!string.IsNullOrWhiteSpace(cfg.Project.GitHubRepo))
                upstreamUrl = $"https://github.com/{cfg.Project.GitHubRepo}.git";

            RepoName = repoName;
            DefaultBranch = cfg.Project.DefaultBranch;

            var dbFile = Path.Combine(ldpPath, $"local_platform_{repoName}.db");
            _connectionString = $"Data Source={dbFile}";

            // Persist RunId in DB so it survives restarts
            RunId = LoadOrCreateRunId(dbFile);

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var walCmd = conn.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            walCmd.ExecuteNonQuery();
            Schema.EnsureCreated(conn);

            BareRepo.InitializeAsync(ldpPath, repoName, upstreamUrl, CancellationToken.None)
                .GetAwaiter().GetResult();

            _initialized = true;
            _logger.LogInformation("LocalPlatformContext: auto-initialized for {Repo} at {Path} (RunId={RunId})",
                repoName, dbFile, RunId);
        }
        finally { _initLock.Release(); }
    }

    /// <summary>
    /// Explicit initialization with known run parameters. Preferred over auto-init.
    /// </summary>
    public async Task InitializeAsync(
        string dbPath, string bareRepoBasePath, string repoName,
        string? upstreamUrl, string runId, string defaultBranch = "main",
        CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            RepoName = repoName;
            DefaultBranch = defaultBranch;

            var dbFile = Path.Combine(dbPath, $"local_platform_{repoName}.db");
            Directory.CreateDirectory(dbPath);
            _connectionString = $"Data Source={dbFile}";

            // Persist RunId in DB for restart survival
            if (string.IsNullOrEmpty(runId))
                runId = LoadOrCreateRunId(dbFile);
            else
            {
                // Explicit RunId — store it
                using var metaConn = new SqliteConnection(_connectionString);
                metaConn.Open();
                using var createCmd = metaConn.CreateCommand();
                createCmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS local_run_metadata (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    )
                    """;
                createCmd.ExecuteNonQuery();
                PersistRunId(metaConn, runId);
            }
            RunId = runId;

            using var conn = CreateConnection();
            using var walCmd = conn.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await walCmd.ExecuteNonQueryAsync(ct);
            Schema.EnsureCreated(conn);

            await BareRepo.InitializeAsync(bareRepoBasePath, repoName, upstreamUrl, ct);
            _initialized = true;
            _logger.LogInformation("LocalPlatformContext: initialized for {Repo} at {Path} (RunId={RunId})",
                repoName, dbFile, RunId);
        }
        finally { _initLock.Release(); }
    }

    /// <summary>Load persisted RunId from the DB metadata table, or create a new one and persist it.</summary>
    private string LoadOrCreateRunId(string dbFile)
    {
        var connStr = $"Data Source={dbFile}";
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        // Ensure metadata table exists
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS local_run_metadata (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                created_at TEXT NOT NULL
            )
            """;
        createCmd.ExecuteNonQuery();

        // Try to load the most recent RunId
        using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT run_id FROM local_run_metadata ORDER BY id DESC LIMIT 1";
        var existing = selectCmd.ExecuteScalar() as string;
        if (!string.IsNullOrEmpty(existing))
            return existing;

        // First run — generate and persist
        var newId = Guid.NewGuid().ToString("N")[..8];
        PersistRunId(conn, newId);
        return newId;
    }

    private static void PersistRunId(SqliteConnection conn, string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO local_run_metadata (run_id, created_at) VALUES (@runId, @now)";
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
    }
}
