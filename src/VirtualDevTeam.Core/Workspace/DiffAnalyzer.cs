using System.Text.RegularExpressions;

namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Pure static analysis of a git diff (patch) string.
/// Extracts routes, components, form elements, and UI patterns
/// without any LLM calls — fast regex-based parsing.
/// </summary>
public static class DiffAnalyzer
{
    // Blazor route: @page "/some-path"
    private static readonly Regex BlazorRouteRx = new(
        @"^\+.*@page\s+""(/[^""]*)""\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // React/Next.js route patterns
    private static readonly Regex ReactRouteRx = new(
        @"^\+.*(?:path:\s*['""]|<Route\s+path=['""])(/[^'""]*)['""]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // ASP.NET MapGet/MapPost/app.Map* route patterns
    private static readonly Regex AspNetRouteRx = new(
        @"^\+.*\.(?:MapGet|MapPost|MapPut|MapDelete|Map)\([""'](/[^""']*)[""']",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // HTML/Blazor form inputs: <input, <InputText, <InputNumber, <InputSelect, <select, <textarea
    private static readonly Regex FormInputRx = new(
        @"^\+.*<(?:input|InputText|InputNumber|InputDate|InputSelect|InputCheckbox|InputRadio|InputTextArea|select|textarea)\b([^>]*?)(?:/>|>)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Extract type="..." from input attributes
    private static readonly Regex InputTypeRx = new(
        @"type=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Extract placeholder="..." from input attributes
    private static readonly Regex PlaceholderRx = new(
        @"placeholder=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Extract @bind-Value="..." or @bind="..." or bind-value="..."
    private static readonly Regex BindRx = new(
        @"(?:@bind(?:-[Vv]alue)?|bind-value)=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Extract label text from preceding <label> or for="" associations
    private static readonly Regex LabelRx = new(
        @"^\+.*<(?:label|Label)[^>]*>([^<]+)<", RegexOptions.Multiline | RegexOptions.Compiled);

    // Required attribute or data annotation
    private static readonly Regex RequiredRx = new(
        @"required|Required", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Diff file header: diff --git a/path b/path or --- a/path / +++ b/path
    private static readonly Regex DiffHeaderRx = new(
        @"^diff --git a/(.*?) b/(.*?)$", RegexOptions.Multiline | RegexOptions.Compiled);

    // Hunk header for counting added/removed lines
    private static readonly Regex HunkHeaderRx = new(
        @"^@@\s", RegexOptions.Multiline | RegexOptions.Compiled);

    // Wizard indicators: step, stepper, wizard, Next/Back buttons
    private static readonly Regex WizardIndicatorRx = new(
        @"(?:wizard|stepper|step-indicator|currentStep|StepNumber|NextStep|PreviousStep|step\s*=\s*\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Button text extraction
    private static readonly Regex ButtonRx = new(
        @"^\+.*<(?:button|Button|btn)[^>]*>([^<]*)</",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DiffAnalysisResult Analyze(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
            return new DiffAnalysisResult();

        var routes = new List<string>();
        var components = new List<string>();
        var formElements = new List<DiffFormElement>();
        var fileChanges = new List<DiffFileChange>();
        int totalAdded = 0;

        // Extract routes
        foreach (Match m in BlazorRouteRx.Matches(patch))
            routes.Add(m.Groups[1].Value);
        foreach (Match m in ReactRouteRx.Matches(patch))
            if (!routes.Contains(m.Groups[1].Value))
                routes.Add(m.Groups[1].Value);
        foreach (Match m in AspNetRouteRx.Matches(patch))
            if (!routes.Contains(m.Groups[1].Value))
                routes.Add(m.Groups[1].Value);

        // Extract file changes and components
        var fileBlocks = DiffHeaderRx.Matches(patch);
        for (int i = 0; i < fileBlocks.Count; i++)
        {
            var filePath = fileBlocks[i].Groups[2].Value;
            var blockStart = fileBlocks[i].Index;
            var blockEnd = i + 1 < fileBlocks.Count ? fileBlocks[i + 1].Index : patch.Length;
            var block = patch[blockStart..blockEnd];

            // Count added/removed lines
            int added = 0, removed = 0;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith('+') && !line.StartsWith("+++"))
                    added++;
                else if (line.StartsWith('-') && !line.StartsWith("---"))
                    removed++;
            }
            totalAdded += added;

            var kind = removed == 0 && added > 0 ? DiffChangeKind.Added
                     : added == 0 && removed > 0 ? DiffChangeKind.Deleted
                     : DiffChangeKind.Modified;

            fileChanges.Add(new DiffFileChange
            {
                Path = filePath,
                Kind = kind,
                AddedLines = added,
                RemovedLines = removed,
            });

            // Track UI component files
            if (filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".vue", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".svelte", StringComparison.OrdinalIgnoreCase))
            {
                components.Add(Path.GetFileNameWithoutExtension(filePath));
            }
        }

        // Extract form elements (global scan of added lines)
        foreach (Match m in FormInputRx.Matches(patch))
        {
            var attrs = m.Groups[1].Value;
            var elementTag = m.Value.Contains("select", StringComparison.OrdinalIgnoreCase) ? "select"
                           : m.Value.Contains("textarea", StringComparison.OrdinalIgnoreCase) ? "textarea"
                           : "input";

            var typeMatch = InputTypeRx.Match(attrs);
            var placeholderMatch = PlaceholderRx.Match(attrs);
            var bindMatch = BindRx.Match(attrs);

            formElements.Add(new DiffFormElement
            {
                ElementType = elementTag,
                InputType = typeMatch.Success ? typeMatch.Groups[1].Value : null,
                Placeholder = placeholderMatch.Success ? placeholderMatch.Groups[1].Value : null,
                BindProperty = bindMatch.Success ? bindMatch.Groups[1].Value : null,
                IsRequired = RequiredRx.IsMatch(attrs),
            });
        }

        // Assign labels to form elements using proximity — scan for <label> lines
        // near form inputs (best-effort, labels may not be 1:1)
        var labels = new Queue<string>();
        foreach (Match m in LabelRx.Matches(patch))
            labels.Enqueue(m.Groups[1].Value.Trim());

        for (int i = 0; i < formElements.Count && labels.Count > 0; i++)
        {
            if (formElements[i].Label is null)
                formElements[i] = formElements[i] with { Label = labels.Dequeue() };
        }

        // Detect UI pattern
        var pattern = DetectPattern(patch, formElements, routes, components);

        return new DiffAnalysisResult
        {
            NewRoutes = routes.Distinct().ToList(),
            ModifiedComponents = components.Distinct().ToList(),
            FormElements = formElements,
            DetectedPattern = pattern,
            FileChanges = fileChanges,
            AddedLineCount = totalAdded,
            ModifiedFileCount = fileChanges.Count,
        };
    }

    private static UIPatternKind DetectPattern(
        string patch, IReadOnlyList<DiffFormElement> forms,
        IReadOnlyList<string> routes, IReadOnlyList<string> components)
    {
        // Wizard: step indicators, Next/Back buttons, multi-step state
        if (WizardIndicatorRx.IsMatch(patch))
            return UIPatternKind.Wizard;

        // CRUD form: has form inputs with a submit/save button
        if (forms.Count > 0)
        {
            var buttons = ButtonRx.Matches(patch);
            var buttonTexts = buttons.Cast<Match>()
                .Select(m => m.Groups[1].Value.Trim().ToLowerInvariant())
                .ToList();

            if (buttonTexts.Any(b => b.Contains("create") || b.Contains("save") || b.Contains("add")))
                return UIPatternKind.CrudForm;

            if (buttonTexts.Any(b => b.Contains("apply") || b.Contains("update")))
                return UIPatternKind.SettingsPage;
        }

        // Dashboard: charts, graphs, metrics, cards
        if (patch.Contains("chart", StringComparison.OrdinalIgnoreCase)
            || patch.Contains("dashboard", StringComparison.OrdinalIgnoreCase)
            || patch.Contains("metric", StringComparison.OrdinalIgnoreCase)
            || patch.Contains("KPI", StringComparison.Ordinal))
            return UIPatternKind.Dashboard;

        // Data table: table, grid, sortable, filterable
        if (patch.Contains("<table", StringComparison.OrdinalIgnoreCase)
            || patch.Contains("DataGrid", StringComparison.OrdinalIgnoreCase)
            || patch.Contains("sortable", StringComparison.OrdinalIgnoreCase))
            return UIPatternKind.DataTable;

        // Modal/Dialog
        if (patch.Contains("modal", StringComparison.OrdinalIgnoreCase)
            || patch.Contains("dialog", StringComparison.OrdinalIgnoreCase))
            return UIPatternKind.Modal;

        // List-Detail: master list with detail view
        if (components.Any(c => c.Contains("List", StringComparison.OrdinalIgnoreCase))
            && components.Any(c => c.Contains("Detail", StringComparison.OrdinalIgnoreCase)))
            return UIPatternKind.ListDetail;

        // Navigation: only route changes
        if (routes.Count > 0 && forms.Count == 0)
            return UIPatternKind.Navigation;

        return UIPatternKind.Unknown;
    }

    /// <summary>
    /// Builds a human-readable summary of the diff analysis for inclusion in LLM prompts.
    /// </summary>
    public static string BuildSummary(DiffAnalysisResult analysis)
    {
        var sb = new System.Text.StringBuilder();

        if (analysis.NewRoutes.Count > 0)
        {
            sb.AppendLine("NEW ROUTES:");
            foreach (var r in analysis.NewRoutes)
                sb.AppendLine($"  - {r}");
        }

        if (analysis.ModifiedComponents.Count > 0)
        {
            sb.AppendLine("COMPONENTS (added/modified):");
            foreach (var c in analysis.ModifiedComponents)
                sb.AppendLine($"  - {c}");
        }

        if (analysis.FormElements.Count > 0)
        {
            sb.AppendLine("FORM ELEMENTS:");
            foreach (var f in analysis.FormElements)
            {
                var label = f.Label ?? f.Placeholder ?? f.BindProperty ?? "unlabeled";
                var type = f.InputType ?? f.ElementType;
                var req = f.IsRequired ? " (required)" : "";
                sb.AppendLine($"  - {type}: \"{label}\"{req}");
            }
        }

        sb.AppendLine($"DETECTED UI PATTERN: {analysis.DetectedPattern}");
        sb.AppendLine($"FILES CHANGED: {analysis.ModifiedFileCount} ({analysis.AddedLineCount} lines added)");

        return sb.ToString();
    }
}
