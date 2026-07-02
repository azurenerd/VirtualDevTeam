using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace VirtualDevTeam.Core.Prompts;

/// <summary>
/// Manages prompt template deployment from embedded resources to the user-data directory.
/// 
/// Strategy:
/// 1. Prompt .md files are embedded in the assembly as resources (read-only defaults).
/// 2. On first run, they're extracted to the user-data prompts directory (editable).
/// 3. The dashboard prompt editor reads/writes the user-data copies.
/// 4. On update, only unmodified files are overwritten — user edits are preserved.
/// 5. A .hash sidecar file tracks the last-deployed version of each prompt.
/// </summary>
public class PromptDeploymentService
{
    private readonly ILogger<PromptDeploymentService> _logger;
    private const string HashExtension = ".deployed-hash";

    public PromptDeploymentService(ILogger<PromptDeploymentService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Deploy embedded prompt templates to the target directory.
    /// Preserves user-modified files; only overwrites unmodified defaults.
    /// </summary>
    /// <param name="targetDir">User-data prompts directory (e.g., %LOCALAPPDATA%\VirtualDevTeam\prompts)</param>
    /// <param name="sourceAssembly">Assembly containing embedded prompt resources. Null = use Core assembly.</param>
    /// <returns>Number of files deployed (new or updated).</returns>
    public int DeployPrompts(string targetDir, Assembly? sourceAssembly = null)
    {
        sourceAssembly ??= typeof(PromptDeploymentService).Assembly;
        Directory.CreateDirectory(targetDir);

        var prefix = "VirtualDevTeam.Core.prompts.";
        var resources = sourceAssembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                     && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (resources.Count == 0)
        {
            _logger.LogDebug("No embedded prompt resources found in {Assembly}", sourceAssembly.GetName().Name);
            return 0;
        }

        var deployed = 0;
        foreach (var resourceName in resources)
        {
            try
            {
                // Convert resource name to file path: VirtualDevTeam.Core.prompts.pm.spec-system.md → pm/spec-system.md
                var relativePath = ResourceNameToPath(resourceName, prefix);
                var targetPath = Path.Combine(targetDir, relativePath);
                var hashPath = targetPath + HashExtension;

                using var stream = sourceAssembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;

                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                var contentHash = ComputeHash(content);

                // Check if file exists and whether user modified it
                if (File.Exists(targetPath))
                {
                    // Read the deployed hash — if it matches content hash, file is unmodified by user
                    if (File.Exists(hashPath))
                    {
                        var deployedHash = File.ReadAllText(hashPath).Trim();
                        var currentFileHash = ComputeHash(File.ReadAllText(targetPath));

                        if (currentFileHash != deployedHash)
                        {
                            // User modified this file — preserve their changes
                            _logger.LogDebug("Preserving user-modified prompt: {Path}", relativePath);
                            continue;
                        }

                        if (contentHash == deployedHash)
                        {
                            // File unchanged in both source and user-data — skip
                            continue;
                        }
                    }
                    else
                    {
                        // No hash file = legacy file or user-created. Preserve it.
                        _logger.LogDebug("Preserving existing prompt (no hash): {Path}", relativePath);
                        continue;
                    }
                }

                // Deploy the file
                var dir = Path.GetDirectoryName(targetPath);
                if (dir is not null) Directory.CreateDirectory(dir);
                File.WriteAllText(targetPath, content);
                File.WriteAllText(hashPath, contentHash);
                deployed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deploy prompt: {Resource}", resourceName);
            }
        }

        if (deployed > 0)
            _logger.LogInformation("Deployed {Count} prompt template(s) to {Dir}", deployed, targetDir);

        return deployed;
    }

    /// <summary>
    /// Convert embedded resource name to a relative file path.
    /// Dots are directory separators except the last one (file extension).
    /// Handles the convention: role.template-name.md → role/template-name.md
    /// </summary>
    internal static string ResourceNameToPath(string resourceName, string prefix)
    {
        var stripped = resourceName[prefix.Length..]; // e.g., "pm.spec-system.md"
        // Split on dots, rejoin with path separator, keeping .md extension
        var parts = stripped.Split('.');
        if (parts.Length <= 2)
            return stripped; // Simple: "file.md"

        // Last part is extension, second-to-last is filename, rest are directories
        // e.g., "pm.spec-system.md" → ["pm", "spec-system", "md"] → "pm/spec-system.md"
        var dirs = parts[..^2]; // all except last 2
        var fileName = parts[^2] + "." + parts[^1]; // "spec-system.md"
        return Path.Combine(Path.Combine(dirs), fileName);
    }

    private static string ComputeHash(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
