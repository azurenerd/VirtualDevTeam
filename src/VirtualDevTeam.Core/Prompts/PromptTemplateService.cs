using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.Scenarios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualDevTeam.Core.Prompts;

/// <summary>
/// Loads prompt templates from .md files, parses YAML frontmatter,
/// performs {{variable}} substitution and {{> fragment}} includes.
/// Thread-safe via ConcurrentDictionary cache.
/// </summary>
/// <remarks>
/// When <paramref name="scenarioRegistry"/> is provided, the service auto-fills four
/// well-known variables that callers frequently omit:
/// <list type="bullet">
///   <item><term>project_description</term><description>From <see cref="ProjectConfig.Description"/>.</description></item>
///   <item><term>scenarios_yaml_block</term><description>YAML serialization of <see cref="IScenarioRegistry.Current"/>.</description></item>
///   <item><term>approved_scenarios_yaml</term><description>Same as <c>scenarios_yaml_block</c>.</description></item>
///   <item><term>scenarios_json</term><description>JSON serialization of <see cref="IScenarioRegistry.Current"/>.</description></item>
/// </list>
/// Caller-supplied values always take precedence over auto-resolved values.
/// </remarks>
public partial class PromptTemplateService : IPromptTemplateService
{
    private readonly string _basePath;
    private readonly int _maxIncludeDepth;
    private readonly ILogger<PromptTemplateService> _logger;
    private readonly IOptions<VirtualDevTeamConfig> _config;
    private readonly IScenarioRegistry? _scenarioRegistry;
    private readonly ConcurrentDictionary<string, PromptTemplate> _cache = new();

    public PromptTemplateService(
        IOptions<VirtualDevTeamConfig> config,
        ILogger<PromptTemplateService> logger,
        IScenarioRegistry? scenarioRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config;
        _scenarioRegistry = scenarioRegistry;

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

        // Auto-fill well-known variables not supplied by the caller.
        var effectiveVars = AutoFillWellKnownVariables(variables);

        // Resolve fragment includes first, then substitute variables.
        var body = await ResolveIncludesAsync(template.Body, [], 0, ct);
        return SubstituteVariables(body, effectiveVars, templatePath);
    }

    /// <summary>
    /// Returns a variable dictionary that includes auto-resolved values for the four well-known
    /// scenario/project variables when the caller did not supply them.
    /// Caller-supplied values always win — no existing key is overwritten.
    /// </summary>
    private Dictionary<string, string> AutoFillWellKnownVariables(Dictionary<string, string> callerVars)
    {
        var needsDescription  = !callerVars.ContainsKey("project_description");
        var needsYaml         = !callerVars.ContainsKey("scenarios_yaml_block");
        var needsApprovedYaml = !callerVars.ContainsKey("approved_scenarios_yaml");
        var needsJson         = !callerVars.ContainsKey("scenarios_json");
        var needsContext      = !callerVars.ContainsKey("existing_project_context");

        if (!needsDescription && !needsYaml && !needsApprovedYaml && !needsJson && !needsContext)
            return callerVars; // Nothing to add — return as-is to avoid allocation.

        var merged = new Dictionary<string, string>(callerVars);

        // Always set existing_project_context — empty string for new/greenfield projects.
        // Never skip: leaving the placeholder unresolved leaks literal {{existing_project_context}}
        // into rendered prompts, confusing the LLM.
        if (needsContext)
        {
            merged["existing_project_context"] = _config.Value.Project.ExistingProjectContext ?? "";
        }

        if (needsDescription)
        {
            // Use resolved description (condensed doc summary) when available.
            // Agents that need the full raw description (PM, Researcher, Architect)
            // explicitly pass project_description in their template vars, overriding this.
            var desc = _config.Value.Project.ResolvedDescription
                    ?? _config.Value.Project.Description;
            if (!string.IsNullOrEmpty(desc))
                merged["project_description"] = desc;
        }

        if ((needsYaml || needsApprovedYaml || needsJson) && _scenarioRegistry is not null)
        {
            var scenarios = _scenarioRegistry.Current;

            if (needsYaml || needsApprovedYaml)
            {
                var yaml = ScenarioYamlSerializer.Serialize(scenarios);
                if (needsYaml)         merged["scenarios_yaml_block"]   = yaml;
                if (needsApprovedYaml) merged["approved_scenarios_yaml"] = yaml;
            }

            if (needsJson)
                merged["scenarios_json"] = ScenarioJsonSerializer.Serialize(scenarios);
        }

        return merged;
    }

    public async Task<PromptMetadata?> GetMetadataAsync(
        string templatePath,
        CancellationToken ct = default)
    {
        var template = await LoadTemplateAsync(templatePath, ct);
        return template?.Metadata;
    }

    public IReadOnlyList<string> ListRoles()
    {
        if (!Directory.Exists(_basePath))
            return [];

        return Directory.GetDirectories(_basePath)
            .Select(Path.GetFileName)
            .Where(d => d is not null)
            .Select(d => d!)
            .OrderBy(x => x)
            .ToList();
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
        // Phase 1: Process conditional blocks {{#var}}...{{/var}}
        // If var is non-empty, keep inner content; if empty/missing, remove the block entirely.
        body = ConditionalBlockRegex().Replace(body, match =>
        {
            var varName = match.Groups[1].Value.Trim();
            if (variables.TryGetValue(varName, out var value) && !string.IsNullOrWhiteSpace(value))
                return match.Groups[2].Value; // Keep inner content (still has {{var}} for Phase 2)
            return ""; // Remove entire block
        });

        // Phase 2: Simple variable substitution {{var}}
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

    // {{variable_name}} — variable substitution (but NOT {{> includes}} or {{# / {{/ conditionals)
    [GeneratedRegex(@"\{\{(?![>/\#])(\s*[^}]+?\s*)\}\}", RegexOptions.Compiled)]
    private static partial Regex VariableRegex();

    // {{#var_name}}...{{/var_name}} — conditional block (include content only if var is non-empty)
    [GeneratedRegex(@"\{\{#\s*(\w+)\s*\}\}([\s\S]*?)\{\{/\s*\1\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex ConditionalBlockRegex();
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
