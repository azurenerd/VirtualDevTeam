using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AgentSquad.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentSquad.Core.Prompts;

/// <summary>
/// Loads prompt templates from .md files, parses YAML frontmatter,
/// performs {{variable}} substitution and {{> fragment}} includes.
/// Thread-safe via ConcurrentDictionary cache.
/// </summary>
public partial class PromptTemplateService : IPromptTemplateService
{
    private readonly string _basePath;
    private readonly int _maxIncludeDepth;
    private readonly ILogger<PromptTemplateService> _logger;
    private readonly ConcurrentDictionary<string, PromptTemplate> _cache = new();

    public PromptTemplateService(
        IOptions<AgentSquadConfig> config,
        ILogger<PromptTemplateService> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var promptsConfig = config.Value.Prompts;
        _basePath = Path.GetFullPath(promptsConfig.BasePath);
        _maxIncludeDepth = promptsConfig.MaxIncludeDepth;
    }

    public async Task<string?> RenderAsync(
        string templatePath,
        Dictionary<string, string> variables,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(templatePath);
        ArgumentNullException.ThrowIfNull(variables);

        var template = await LoadTemplateAsync(templatePath, ct);
        if (template is null)
            return null;

        // Resolve fragment includes first, then substitute variables
        var body = await ResolveIncludesAsync(template.Body, [], 0, ct);
        return SubstituteVariables(body, variables, templatePath);
    }

    public async Task<PromptMetadata?> GetMetadataAsync(
        string templatePath,
        CancellationToken ct = default)
    {
        var template = await LoadTemplateAsync(templatePath, ct);
        return template?.Metadata;
    }

    public IReadOnlyList<string> ListTemplates(string role)
    {
        ArgumentNullException.ThrowIfNull(role);

        var roleDir = Path.Combine(_basePath, role);
        if (!Directory.Exists(roleDir))
            return [];

        return Directory.GetFiles(roleDir, "*.md")
            .Select(f => $"{role}/{Path.GetFileNameWithoutExtension(f)}")
            .OrderBy(x => x)
            .ToList();
    }

    public async Task<string?> GetRawContentAsync(string templatePath, CancellationToken ct = default)
    {
        var filePath = ResolveFilePath(templatePath);
        if (!File.Exists(filePath))
            return null;

        return await File.ReadAllTextAsync(filePath, ct);
    }

    public async Task SaveRawContentAsync(string templatePath, string content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(templatePath);
        ArgumentNullException.ThrowIfNull(content);

        var filePath = ResolveFilePath(templatePath);
        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(filePath, content, ct);
        InvalidateCache(templatePath);
    }

    public void InvalidateCache(string? templatePath = null)
    {
        if (templatePath is null)
        {
            _cache.Clear();
            _logger.LogDebug("Prompt template cache cleared entirely");
        }
        else
        {
            var key = NormalizePath(templatePath);
            if (_cache.TryRemove(key, out _))
                _logger.LogDebug("Prompt template cache invalidated for {TemplatePath}", key);
        }
    }

    private async Task<PromptTemplate?> LoadTemplateAsync(string templatePath, CancellationToken ct)
    {
        var key = NormalizePath(templatePath);

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var filePath = ResolveFilePath(key);
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Prompt template '{TemplatePath}' not found at {FilePath}", key, filePath);
            return null;
        }

        var rawContent = await File.ReadAllTextAsync(filePath, ct);
        var (metadata, body) = ParseFrontmatter(rawContent);

        var template = new PromptTemplate
        {
            Metadata = metadata,
            Body = body,
            LoadedAt = DateTimeOffset.UtcNow
        };

        _cache.TryAdd(key, template);
        return template;
    }

    private async Task<string> ResolveIncludesAsync(
        string body, HashSet<string> includeStack, int depth, CancellationToken ct)
    {
        if (depth > _maxIncludeDepth)
            throw new InvalidOperationException(
                $"Maximum include depth of {_maxIncludeDepth} exceeded. Include chain: {string.Join(" → ", includeStack)}");

        return await IncludeRegex().ReplaceAsync(body, async match =>
        {
            var fragmentPath = match.Groups[1].Value.Trim();
            var key = NormalizePath(fragmentPath);

            if (!includeStack.Add(key))
                throw new InvalidOperationException(
                    $"Circular include detected: {string.Join(" → ", includeStack)} → {key}");

            var fragment = await LoadTemplateAsync(key, ct);
            if (fragment is null)
            {
                _logger.LogWarning("Fragment '{FragmentPath}' not found, rendering as empty", key);
                includeStack.Remove(key);
                return "";
            }

            var resolved = await ResolveIncludesAsync(fragment.Body, includeStack, depth + 1, ct);
            includeStack.Remove(key);
            return resolved;
        }, ct);
    }

    private string SubstituteVariables(string body, Dictionary<string, string> variables, string templatePath)
    {
        return VariableRegex().Replace(body, match =>
        {
            var varName = match.Groups[1].Value.Trim();
            if (variables.TryGetValue(varName, out var value))
                return value;

            _logger.LogWarning(
                "Undefined variable '{VarName}' in template '{TemplatePath}'",
                varName, templatePath);
            return match.Value; // Leave as-is
        });
    }

    internal static (PromptMetadata metadata, string body) ParseFrontmatter(string rawContent)
    {
        if (!rawContent.StartsWith("---"))
            return (new PromptMetadata(), rawContent.TrimStart());

        var endIndex = rawContent.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return (new PromptMetadata(), rawContent.TrimStart());

        var yamlSection = rawContent[3..endIndex].Trim();
        var body = rawContent[(endIndex + 4)..].TrimStart();

        var metadata = ParseYamlMetadata(yamlSection);
        return (metadata, body);
    }

    private static PromptMetadata ParseYamlMetadata(string yaml)
    {
        string? version = null;
        string? description = null;
        var variables = new List<string>();
        var tags = new List<string>();
        List<string>? currentList = null;

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            // List item (continuation of previous key)
            if (line.TrimStart().StartsWith("- ") && currentList is not null)
            {
                currentList.Add(line.TrimStart()[2..].Trim().Trim('"', '\''));
                continue;
            }

            // Reset current list context
            currentList = null;

            // Inline list: key: [item1, item2]
            if (line.Contains('[') && line.Contains(']'))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx < 0) continue;
                var key = line[..colonIdx].Trim();
                var listContent = line[(line.IndexOf('[') + 1)..line.IndexOf(']')];
                var items = listContent.Split(',')
                    .Select(s => s.Trim().Trim('"', '\''))
                    .Where(s => s.Length > 0)
                    .ToList();

                switch (key)
                {
                    case "variables": variables = items; break;
                    case "tags": tags = items; break;
                }
                continue;
            }

            // Key-value pair
            var kvSep = line.IndexOf(':');
            if (kvSep < 0) continue;

            var k = line[..kvSep].Trim();
            var v = line[(kvSep + 1)..].Trim().Trim('"', '\'');

            switch (k)
            {
                case "version": version = v; break;
                case "description": description = v; break;
                case "variables":
                    currentList = variables;
                    if (!string.IsNullOrEmpty(v)) variables.Add(v);
                    break;
                case "tags":
                    currentList = tags;
                    if (!string.IsNullOrEmpty(v)) tags.Add(v);
                    break;
            }
        }

        return new PromptMetadata
        {
            Version = version,
            Description = description,
            Variables = variables,
            Tags = tags
        };
    }

    private string ResolveFilePath(string templatePath)
    {
        var normalized = NormalizePath(templatePath);
        var filePath = Path.Combine(_basePath, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            filePath += ".md";
        return filePath;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/').Replace(".md", "", StringComparison.OrdinalIgnoreCase);

    // {{> shared/fragment-name}} — fragment include
    [GeneratedRegex(@"\{\{>\s*([^}]+?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex IncludeRegex();

    // {{variable_name}} — variable substitution (but NOT {{> includes}})
    [GeneratedRegex(@"\{\{(?!>)(\s*[^}]+?\s*)\}\}", RegexOptions.Compiled)]
    private static partial Regex VariableRegex();
}

/// <summary>
/// Extension to support async Regex.Replace (needed for include resolution).
/// </summary>
internal static class RegexExtensions
{
    public static async Task<string> ReplaceAsync(
        this Regex regex, string input, Func<Match, Task<string>> replacer, CancellationToken ct = default)
    {
        var matches = regex.Matches(input);
        if (matches.Count == 0) return input;

        var sb = new System.Text.StringBuilder();
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(input, lastIndex, match.Index - lastIndex);
            sb.Append(await replacer(match));
            lastIndex = match.Index + match.Length;
        }

        sb.Append(input, lastIndex, input.Length - lastIndex);
        return sb.ToString();
    }
}
