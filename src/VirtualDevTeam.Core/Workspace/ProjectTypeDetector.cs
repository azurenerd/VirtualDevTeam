namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Detects what kind of project lives in a directory and returns build/test commands
/// that match. Used by <see cref="BuildRunner"/> and <see cref="TestRunner"/> so they
/// no longer assume <c>dotnet build</c> for every workspace, and by the engineer flow
/// to skip build/test entirely when there's nothing buildable (pure-asset PRs, doc PRs,
/// pure-image PRs, etc.).
///
/// <para>
/// Generality rule (per 2026-05-12 user direction): NEVER use the agent's role identity
/// to skip build/test. The signal must come from the WORKSPACE STATE — look at what's
/// on disk and decide whether there's anything to build/test based on the files alone.
/// An Artist task that touches client/src/foo.tsx still needs npm build/test; a
/// Software Engineer task that only touches Architecture.md still skips build.
/// </para>
/// </summary>
public static class ProjectTypeDetector
{
    public enum ProjectType
    {
        /// <summary>Workspace contains a .sln file or .csproj — use `dotnet build` / `dotnet test`.</summary>
        DotNet,
        /// <summary>Workspace contains a package.json — use `npm run build` / `npm test`.</summary>
        Node,
        /// <summary>Workspace contains a pyproject.toml or requirements.txt — use `python -m build` / `pytest`.</summary>
        Python,
        /// <summary>Workspace contains a go.mod — use `go build ./...` / `go test ./...`.</summary>
        Go,
        /// <summary>Workspace has a Cargo.toml — use `cargo build` / `cargo test`.</summary>
        Rust,
        /// <summary>No buildable code detected — pure-asset / pure-doc / pure-image PR.</summary>
        NoBuildableCode,
    }

    /// <summary>
    /// Returns the detected project type for <paramref name="workspacePath"/>. Probes
    /// by file existence — fast, no execution. Returns <see cref="ProjectType.NoBuildableCode"/>
    /// when only assets / docs / images are present.
    /// </summary>
    public static ProjectType Detect(string workspacePath)
    {
        if (string.IsNullOrEmpty(workspacePath) || !Directory.Exists(workspacePath))
            return ProjectType.NoBuildableCode;

        // Probe for marker files. Search up to 3 levels deep so monorepos with
        // src/Foo.csproj or services/api/package.json are detected.
        try
        {
            if (HasMarkerFile(workspacePath, "*.sln", maxDepth: 2)) return ProjectType.DotNet;
            if (HasMarkerFile(workspacePath, "*.csproj", maxDepth: 3)) return ProjectType.DotNet;
            if (HasMarkerFile(workspacePath, "package.json", maxDepth: 2)) return ProjectType.Node;
            if (HasMarkerFile(workspacePath, "pyproject.toml", maxDepth: 2)
                || HasMarkerFile(workspacePath, "requirements.txt", maxDepth: 2)
                || HasMarkerFile(workspacePath, "setup.py", maxDepth: 2)) return ProjectType.Python;
            if (HasMarkerFile(workspacePath, "go.mod", maxDepth: 2)) return ProjectType.Go;
            if (HasMarkerFile(workspacePath, "Cargo.toml", maxDepth: 2)) return ProjectType.Rust;
        }
        catch
        {
            // I/O errors during enumeration shouldn't bubble — treat as "can't tell, assume nothing".
        }
        return ProjectType.NoBuildableCode;
    }

    /// <summary>
    /// Returns the build command for the detected project type, or null if no build is needed.
    /// Use this when callers have not been provided an explicit build command in config.
    /// </summary>
    public static string? GetDefaultBuildCommand(ProjectType type) => type switch
    {
        ProjectType.DotNet => "dotnet build",
        ProjectType.Node => "npm run build",
        ProjectType.Python => null, // most python projects don't have a build step at the workspace root
        ProjectType.Go => "go build ./...",
        ProjectType.Rust => "cargo build",
        _ => null,
    };

    /// <summary>
    /// Returns the test command for the detected project type, or null if no test runner is needed.
    /// </summary>
    public static string? GetDefaultTestCommand(ProjectType type) => type switch
    {
        ProjectType.DotNet => "dotnet test",
        ProjectType.Node => "npm test",
        ProjectType.Python => "pytest",
        ProjectType.Go => "go test ./...",
        ProjectType.Rust => "cargo test",
        _ => null,
    };

    private static bool HasMarkerFile(string root, string pattern, int maxDepth)
    {
        // Bounded BFS over directories (excluding well-known "scaffolding" dirs)
        // to keep the probe cheap on workspaces with deep node_modules / bin trees.
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();
            try
            {
                if (Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).Any())
                    return true;
                if (depth >= maxDepth) continue;
                foreach (var sub in Directory.EnumerateDirectories(path))
                {
                    var name = Path.GetFileName(sub);
                    if (string.IsNullOrEmpty(name)) continue;
                    // Skip noise dirs that never contain top-level project markers
                    if (name is "node_modules" or "bin" or "obj" or ".git" or ".vs"
                            or ".candidates" or ".candidates-eval" or ".agents"
                            or "dist" or "build" or "target") continue;
                    if (name.StartsWith('.')) continue; // hidden trees
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch
            {
                // Ignore unreadable dirs and continue.
            }
        }
        return false;
    }
}
