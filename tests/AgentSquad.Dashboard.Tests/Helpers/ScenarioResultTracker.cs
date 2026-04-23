using Microsoft.Data.Sqlite;

namespace AgentSquad.Dashboard.Tests.Helpers;

/// <summary>
/// SQLite-based pass/fail tracker for GIF scenario tests.
/// Stores results alongside GIF paths for the HTML report generator.
/// </summary>
public sealed class ScenarioResultTracker : IDisposable
{
    private readonly SqliteConnection _conn;

    public ScenarioResultTracker(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scenario_results (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                passed      INTEGER NOT NULL DEFAULT 0,
                error_msg   TEXT,
                gif_path    TEXT,
                video_path  TEXT,
                screenshot_paths TEXT,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                timestamp   TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Record(ScenarioResult result)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO scenario_results
                (id, name, passed, error_msg, gif_path, video_path, screenshot_paths, duration_ms, timestamp)
            VALUES
                ($id, $name, $passed, $error, $gif, $video, $screenshots, $duration, datetime('now'));
            """;
        cmd.Parameters.AddWithValue("$id", result.Id);
        cmd.Parameters.AddWithValue("$name", result.Name);
        cmd.Parameters.AddWithValue("$passed", result.Passed ? 1 : 0);
        cmd.Parameters.AddWithValue("$error", (object?)result.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gif", (object?)result.GifPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$video", (object?)result.VideoPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$screenshots", (object?)result.ScreenshotPaths ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration", result.DurationMs);
        cmd.ExecuteNonQuery();
    }

    public List<ScenarioResult> GetAll()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, passed, error_msg, gif_path, video_path, screenshot_paths, duration_ms, timestamp FROM scenario_results ORDER BY id";
        using var reader = cmd.ExecuteReader();

        var results = new List<ScenarioResult>();
        while (reader.Read())
        {
            results.Add(new ScenarioResult
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Passed = reader.GetInt32(2) == 1,
                ErrorMessage = reader.IsDBNull(3) ? null : reader.GetString(3),
                GifPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                VideoPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                ScreenshotPaths = reader.IsDBNull(6) ? null : reader.GetString(6),
                DurationMs = reader.GetInt64(7),
            });
        }
        return results;
    }

    public void Dispose() => _conn.Dispose();
}

public record ScenarioResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Passed { get; init; }
    public string? ErrorMessage { get; init; }
    public string? GifPath { get; init; }
    public string? VideoPath { get; init; }
    public string? ScreenshotPaths { get; init; }
    public long DurationMs { get; init; }
}
