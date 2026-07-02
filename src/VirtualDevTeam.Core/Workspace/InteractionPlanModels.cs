namespace VirtualDevTeam.Core.Workspace;

/// <summary>
/// Structured interaction plan for Playwright MCP testing.
/// Generated from task context + diff analysis, this tells the MCP agent
/// exactly how to interact with the UI rather than using generic exploration.
/// </summary>
public record InteractionPlan
{
    /// <summary>Ordered test scenarios to execute.</summary>
    public required IReadOnlyList<TestScenario> Scenarios { get; init; }

    /// <summary>One-line summary of what's being tested.</summary>
    public string? TaskSummary { get; init; }

    /// <summary>Detected UI pattern for the task (wizard, dashboard, CRUD, etc.).</summary>
    public UIPatternKind DetectedPattern { get; init; }

    /// <summary>Whether form input interactions are permitted (SafeWrite scenarios exist).</summary>
    public bool AllowsFormInput => Scenarios.Any(s => s.Safety == SafetyLevel.SafeWrite);
}

/// <summary>A single test scenario within an <see cref="InteractionPlan"/>.</summary>
public record TestScenario
{
    /// <summary>Human-readable scenario name (e.g., "Complete wizard flow").</summary>
    public required string Name { get; init; }

    /// <summary>Relative URL path to navigate to (e.g., "/create-project").</summary>
    public required string Url { get; init; }

    /// <summary>Description of what this scenario validates.</summary>
    public string? Description { get; init; }

    /// <summary>Ordered interaction steps.</summary>
    public required IReadOnlyList<InteractionStep> Steps { get; init; }

    /// <summary>Safety classification for this scenario.</summary>
    public SafetyLevel Safety { get; init; } = SafetyLevel.ReadOnly;
}

/// <summary>A single interaction step within a <see cref="TestScenario"/>.</summary>
public record InteractionStep
{
    /// <summary>What action to perform.</summary>
    public required InteractionAction Action { get; init; }

    /// <summary>Target element — CSS selector, text content, or URL depending on action.</summary>
    public required string Target { get; init; }

    /// <summary>Value to type, option to select, or null for non-input actions.</summary>
    public string? Value { get; init; }

    /// <summary>What should be visible/true after this step completes.</summary>
    public string? ExpectedResult { get; init; }

    /// <summary>Human-readable step description.</summary>
    public string? Description { get; init; }
}

/// <summary>Actions the MCP agent can perform during testing.</summary>
public enum InteractionAction
{
    Navigate,
    Click,
    Type,
    Select,
    WaitForText,
    WaitForElement,
    Verify,
    Screenshot,
    ScrollTo,
    Hover,
    ToggleAndRevert,
}

/// <summary>High-level UI pattern classification for the task.</summary>
public enum UIPatternKind
{
    Unknown,
    Wizard,
    Dashboard,
    CrudForm,
    SettingsPage,
    ListDetail,
    Navigation,
    DataTable,
    Modal,
    LandingPage,
}

/// <summary>Safety level for a test scenario — determines what the agent may do.</summary>
public enum SafetyLevel
{
    /// <summary>Only viewing, clicking non-destructive UI controls.</summary>
    ReadOnly,
    /// <summary>Uses synthetic test data for form inputs — no real side effects.</summary>
    SafeWrite,
    /// <summary>Would modify real data — SKIP this scenario.</summary>
    Destructive,
}

/// <summary>
/// Results of static diff analysis — extracted routes, components, form elements.
/// Pure data, no LLM involved.
/// </summary>
public record DiffAnalysisResult
{
    public IReadOnlyList<string> NewRoutes { get; init; } = [];
    public IReadOnlyList<string> ModifiedComponents { get; init; } = [];
    public IReadOnlyList<DiffFormElement> FormElements { get; init; } = [];
    public UIPatternKind DetectedPattern { get; init; }
    public IReadOnlyList<DiffFileChange> FileChanges { get; init; } = [];
    public int AddedLineCount { get; init; }
    public int ModifiedFileCount { get; init; }
}

/// <summary>A form element detected in the diff.</summary>
public record DiffFormElement
{
    public required string ElementType { get; init; }
    public string? InputType { get; init; }
    public string? Label { get; init; }
    public string? Placeholder { get; init; }
    public string? BindProperty { get; init; }
    public bool IsRequired { get; init; }
}

/// <summary>A file changed in the diff.</summary>
public record DiffFileChange
{
    public required string Path { get; init; }
    public required DiffChangeKind Kind { get; init; }
    public int AddedLines { get; init; }
    public int RemovedLines { get; init; }
}

public enum DiffChangeKind { Added, Modified, Deleted, Renamed }
