using System.Reflection;

namespace VirtualDevTeam.E2E.Tests.Infrastructure;

/// <summary>
/// Loads pre-built content files from the Content/ directory for E2E tests.
/// These represent the pre-determined outputs that agents would normally generate via LLM.
/// </summary>
public static class E2EContentLoader
{
    private static readonly string ContentDir = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "Content");

    public static string LoadResearch() => LoadFile("HelloWorldResearch.md");
    public static string LoadPMSpec() => LoadFile("HelloWorldPMSpec.md");
    public static string LoadArchitecture() => LoadFile("HelloWorldArchitecture.md");
    public static string LoadEngineeringPlan() => LoadFile("HelloWorldEngineeringPlan.md");
    public static string LoadEngineeringPlanSplit() => LoadFile("HelloWorldEngineeringPlan_Split.md");

    /// <summary>
    /// Get the path to the HelloWorldApp directory (real buildable .NET webapp).
    /// Used by Scenario 3 for real Playwright screenshots.
    /// </summary>
    public static string GetHelloWorldAppPath() =>
        Path.Combine(ContentDir, "HelloWorldApp");

    /// <summary>
    /// Get all files in the HelloWorldApp as a dictionary of relative path → content.
    /// Used to populate InMemoryGitHubService with the code files.
    /// </summary>
    public static Dictionary<string, string> LoadHelloWorldAppFiles()
    {
        var appDir = GetHelloWorldAppPath();
        var files = new Dictionary<string, string>();

        foreach (var file in Directory.GetFiles(appDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(appDir, file).Replace('\\', '/');
            // Skip binary files
            if (IsBinaryFile(file)) continue;
            files[relativePath] = File.ReadAllText(file);
        }

        return files;
    }

    private static string LoadFile(string fileName)
    {
        var path = Path.Combine(ContentDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"E2E content file not found: {path}");
        return File.ReadAllText(path);
    }

    private static bool IsBinaryFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".ico" or ".png" or ".jpg" or ".gif" or ".woff" or ".woff2" or ".ttf" or ".eot";
    }
}
