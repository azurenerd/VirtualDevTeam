using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.DevPlatform.Providers.Local;

/// <summary>
/// SQLite schema and migrations for the LocalDevPlatform. All tables are run-scoped
/// so multiple runs can coexist in the same database without interference.
/// Git is the authority for code; SQLite is the metadata index (PRs, labels, reviews, issues).
/// </summary>
public sealed class LocalPlatformSchema
{
    private readonly ILogger<LocalPlatformSchema> _logger;

    public LocalPlatformSchema(ILogger<LocalPlatformSchema> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Ensure all tables exist. Idempotent — safe to call on every startup.
    /// </summary>
    public void EnsureCreated(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("LocalPlatformSchema: ensured all tables exist");
    }

    // PR/work-item number generation is done atomically inside the INSERT statement
    // in LocalPullRequestService.CreateAsync / LocalWorkItemService.CreateAsync using
    // (SELECT COALESCE(MAX(number),0)+1 FROM ...) to eliminate TOCTOU races.

    private const string Schema = """
        -- Pull requests: mirrors PlatformPullRequest with local git backing
        CREATE TABLE IF NOT EXISTS local_pull_requests (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            number INTEGER NOT NULL,
            title TEXT NOT NULL,
            body TEXT,
            state TEXT NOT NULL DEFAULT 'open',
            head_branch TEXT NOT NULL,
            head_sha TEXT,
            base_branch TEXT NOT NULL,
            base_sha TEXT,
            assigned_agent TEXT,
            is_draft INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            merged_at TEXT,
            closed_at TEXT,
            UNIQUE(run_id, number)
        );

        -- PR labels (many-to-many, atomic replacement per PR)
        CREATE TABLE IF NOT EXISTS local_pr_labels (
            pr_id INTEGER NOT NULL REFERENCES local_pull_requests(id) ON DELETE CASCADE,
            label TEXT NOT NULL,
            PRIMARY KEY(pr_id, label)
        );

        -- PR comments (issue-level, not inline)
        CREATE TABLE IF NOT EXISTS local_pr_comments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            pr_id INTEGER NOT NULL REFERENCES local_pull_requests(id) ON DELETE CASCADE,
            author TEXT NOT NULL,
            body TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        -- PR reviews (approve/request changes/comment)
        CREATE TABLE IF NOT EXISTS local_pr_reviews (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            pr_id INTEGER NOT NULL REFERENCES local_pull_requests(id) ON DELETE CASCADE,
            reviewer TEXT NOT NULL,
            state TEXT NOT NULL,
            body TEXT,
            created_at TEXT NOT NULL
        );

        -- PR review threads (inline comments on specific files/lines)
        CREATE TABLE IF NOT EXISTS local_pr_threads (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            pr_id INTEGER NOT NULL REFERENCES local_pull_requests(id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            line INTEGER,
            commit_sha TEXT,
            body TEXT NOT NULL,
            author TEXT,
            resolved INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL
        );

        -- PR changed files (computed from git diff, cached for API)
        CREATE TABLE IF NOT EXISTS local_pr_files (
            pr_id INTEGER NOT NULL REFERENCES local_pull_requests(id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            status TEXT NOT NULL,
            additions INTEGER NOT NULL DEFAULT 0,
            deletions INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(pr_id, path)
        );

        -- Work items (issues): mirrors PlatformWorkItem
        CREATE TABLE IF NOT EXISTS local_work_items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            number INTEGER NOT NULL,
            title TEXT NOT NULL,
            body TEXT,
            state TEXT NOT NULL DEFAULT 'open',
            assigned_agent TEXT,
            labels_json TEXT NOT NULL DEFAULT '[]',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            closed_at TEXT,
            UNIQUE(run_id, number)
        );

        -- Work item comments
        CREATE TABLE IF NOT EXISTS local_work_item_comments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            work_item_id INTEGER NOT NULL REFERENCES local_work_items(id) ON DELETE CASCADE,
            author TEXT NOT NULL,
            body TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        -- Work item ↔ PR links
        CREATE TABLE IF NOT EXISTS local_work_item_links (
            work_item_id INTEGER NOT NULL REFERENCES local_work_items(id) ON DELETE CASCADE,
            linked_pr_number INTEGER NOT NULL,
            link_type TEXT NOT NULL DEFAULT 'closes',
            PRIMARY KEY(work_item_id, linked_pr_number)
        );

        -- Branch tracking (metadata only — git is the authority)
        CREATE TABLE IF NOT EXISTS local_branches (
            name TEXT NOT NULL,
            run_id TEXT NOT NULL,
            head_sha TEXT,
            created_at TEXT NOT NULL,
            PRIMARY KEY(name, run_id)
        );

        -- Indexes for common queries
        CREATE INDEX IF NOT EXISTS idx_pr_run_state ON local_pull_requests(run_id, state);
        CREATE INDEX IF NOT EXISTS idx_pr_run_number ON local_pull_requests(run_id, number);
        CREATE INDEX IF NOT EXISTS idx_wi_run_state ON local_work_items(run_id, state);
        CREATE INDEX IF NOT EXISTS idx_wi_run_number ON local_work_items(run_id, number);
        """;
}
