# Prompt Externalization Plan

> **Status**: Draft  
> **Author**: AgentSquad Team  
> **Last Updated**: 2025-07-14  
> **Applies To**: AgentSquad v1.x (.NET 8)

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Current State Analysis](#2-current-state-analysis)
3. [Template System Design](#3-template-system-design)
4. [Concrete Before/After Examples](#4-concrete-beforeafter-examples)
5. [Shared Fragments](#5-shared-fragments)
6. [Relationship with Existing RoleContextProvider](#6-relationship-with-existing-rolecontextprovider)
7. [Migration Strategy (5 Phases)](#7-migration-strategy-5-phases)
8. [Testing Strategy](#8-testing-strategy)
9. [File Organization](#9-file-organization)
10. [Benefits Summary](#10-benefits-summary)
11. [Risks and Mitigations](#11-risks-and-mitigations)
12. [Relationship to Other Plans](#12-relationship-to-other-plans)

---

## 1. Executive Summary

### Problem

AgentSquad currently has **39+ prompt blocks hardcoded as C# interpolated strings** spread across
8 agent implementation files. These prompts — which define what each AI agent thinks, knows, and
produces — are tangled with orchestration logic that controls *when*, *whether*, and *who* to call.

This coupling creates several pain points:

- **Slow iteration**: Every prompt tweak requires a C# recompile and restart. A simple wording
  change to a review rubric triggers a full build cycle.
- **No A/B testing**: There is no mechanism to swap prompt versions at runtime to compare quality.
- **No per-project customization**: All projects use identical prompts regardless of domain,
  language, or team conventions.
- **Mixed concerns**: Prompt engineers (who care about wording, structure, and output format) must
  navigate C# orchestration code to make changes.
- **Duplication**: Several agents share near-identical prompt fragments (code style guidelines,
  PR description formats, review output schemas) that are copy-pasted rather than shared.

### Solution

Externalize all prompt definitions to **Markdown template files** (`.md`) with YAML frontmatter
metadata and `{{variable}}` substitution, loaded at runtime by a new `PromptTemplateService`.

### The Decision Rule

This single rule governs what moves to templates and what stays in C#:

| Question | Answer | Location |
|----------|--------|----------|
| "What should the AI think, know, or produce?" | Prompt content | **External `.md` template** |
| "When, whether, or who should call the AI?" | Orchestration logic | **C# code** |

Examples:

- The 60-line code review rubric that tells the PE *what* to evaluate → **template**
- The `if (pr.Status == Approved)` check that decides *when* to trigger review → **C# code**
- The system message defining the Researcher's persona and expertise → **template**
- The `while (!ct.IsCancellationRequested)` agent loop polling for work → **C# code**
- The list of test categories the Test Engineer should cover → **template**
- The `_messageBus.Subscribe<ReviewRequestMessage>()` routing call → **C# code**

### Benefits at a Glance

| Benefit | Impact |
|---------|--------|
| Faster prompt iteration | Edit `.md`, no recompile (with hot-reload) |
| A/B testing | Swap prompt versions via configuration |
| Per-project customization | Override any prompt per project |
| Separation of concerns | Prompt engineers edit Markdown; developers own C# |
| Testability | Unit test rendering independently from agent logic |
| Reusability | Shared fragments eliminate duplication |
| Version control | Track prompt changes in dedicated diffs |

### Existing Foundation

Two agents — `CustomAgent` and `SmeAgent` — already load external prompt definitions via
`RoleContextProvider`, which reads `.agent.md` files and injects `[ROLE CUSTOMIZATION]` and
`[ROLE KNOWLEDGE]` sections. This plan extends that proven pattern to all 7 core agents with a
more general-purpose template system.

---

## 2. Current State Analysis

### Agent Prompt Inventory

The following catalog identifies every hardcoded prompt block across the codebase, organized by
agent. Each entry notes the prompt's purpose, approximate size, and the variables it currently
interpolates.

---

### 2.1 ResearcherAgent (~3 prompts)

The Researcher is the simplest agent and the ideal first migration candidate.

| # | Prompt | Location | Size | Variables |
|---|--------|----------|------|-----------|
| 1 | **System message** | `ResearcherAgent.cs` constructor / init | ~15 lines | Agent name, project description |
| 2 | **Research instructions** | `RunAgentLoopAsync` | ~25 lines | Project description, research topics, technology stack |
| 3 | **Summary generation** | Post-research synthesis | ~20 lines | Research findings, project context |

**Notes**: The Researcher uses a 3-turn conversation pattern. All prompts are relatively
self-contained with simple string interpolation. No shared fragments needed.

---

### 2.2 PMAgent (~6 prompts)

The PM is the first agent to receive research output and produces the PMSpec.md that all
downstream agents consume.

| # | Prompt | Location | Size | Variables |
|---|--------|----------|------|-----------|
| 1 | **System message** | Constructor / init | ~20 lines | Agent name, project description |
| 2 | **PM Spec generation** | Main workflow | ~40 lines | Project description, research summary, user stories |
| 3 | **Review criteria** | Spec review loop | ~15 lines | Spec draft, acceptance criteria |
| 4 | **Clarification questions** | Research gap analysis | ~15 lines | Research summary, identified gaps |
| 5 | **Spawn decision criteria** | Dynamic scaling logic | ~10 lines | Current workload, complexity signals |
| 6 | **Executive communication** | Status reporting | ~10 lines | Project status, blockers, timeline |

**Notes**: The PM Spec generation prompt is the most critical — its output format must match what
the Architect, PE, and Engineers expect. Changes here require downstream validation.

The **research integration** prompt (combining Research.md content with project description to
produce the initial spec draft) is embedded in the spec generation flow and should be extracted
as a separate template for clarity.

---

### 2.3 ArchitectAgent (~5 prompts)

The Architect consumes PMSpec.md and produces Architecture.md, the technical blueprint.

| # | Prompt | Location | Size | Variables |
|---|--------|----------|------|-----------|
| 1 | **System message** | Constructor / init | ~20 lines | Agent name, project description, tech stack |
| 2 | **Architecture document generation** | Main workflow | ~50 lines | PM spec, research summary, constraints, tech stack |
| 3 | **PR review criteria** | Architecture PR review | ~20 lines | PR diff, architecture principles |
| 4 | **Technology evaluation** | Tech selection analysis | ~25 lines | Candidate technologies, requirements, constraints |
| 5 | **Design pattern selection** | Pattern recommendation | ~15 lines | Problem domain, scale requirements, team experience |

**Notes**: The Architect uses a 5-turn conversation — the most turns of any agent. The
architecture generation prompt is particularly long because it must specify the expected output
structure (component diagrams, API contracts, data models, deployment topology). The **constraint
analysis** logic (evaluating non-functional requirements from the PM spec) is currently
interwoven with the architecture generation prompt and should be separated during extraction.

---

### 2.4 PrincipalEngineerAgent (~6 prompts)

The PE is the technical lead, producing the EngineeringPlan.md and conducting code reviews.

| # | Prompt | Location | Size | Variables |
|---|--------|----------|------|-----------|
| 1 | **System message** | Constructor / init | ~20 lines | Agent name, project description |
| 2 | **Engineering plan generation** | Main workflow | ~45 lines | Architecture doc, PM spec, tech stack, constraints |
| 3 | **Task decomposition** | Work breakdown | ~30 lines | Engineering plan, team capacity, skill levels |
| 4 | **Code review** | PR review workflow | **~60 lines** | PR number, PR diff, PR description, architecture doc |
| 5 | **PR review standards** | Review calibration | ~15 lines | Code quality thresholds, project conventions |
| 6 | **Spawn decision criteria** | Engineer scaling | ~10 lines | Task queue depth, complexity distribution |

**Notes**: The **code review prompt** is the single largest prompt in the codebase at ~60 lines.
It includes a detailed scoring rubric, output format specification (JSON), review categories
(correctness, security, performance, maintainability, test coverage), and severity levels. This
is the flagship migration target — demonstrating the most dramatic improvement in maintainability.

The **leader election** prompt (used when multiple PEs exist in the PE fleet) defines consensus
criteria and is currently a shorter block within the spawn decision flow. It should be extracted
as its own template.

---

### 2.5 SeniorEngineerAgent (~4 prompts)

Senior Engineers are the primary implementers, producing PRs with production code.

| # | Prompt | Location | Size | Variables |
|---|--------|----------|------|-----------|
| 1 | **System message** | Constructor / init | ~15 lines | Agent name, assigned task, tech stack |
| 2 | **Implementation instructions** | Code generation | ~35 lines | Task description, architecture context, file list, conventions |
| 3 | **Self-review criteria** | Pre-submit review | ~20 lines | Own PR diff, coding standards, test requirements |
| 4 | **Rework handling** | Review feedback response | ~15 lines | Review comments, original code, requested changes |

**Notes**: Senior Engineers share significant prompt DNA with Junior Engineers. The
**implementation instructions** prompt includes sections on file organization, error handling
patterns, and test expectations that are nearly identical between Senior and Junior roles.
These should become shared fragments with role-specific overrides.

The **PR description generation** prompt (formatting the PR body with context, changes summary,
and testing notes) is currently in `EngineerAgentBase` and shared by both Senior and Junior.

---

### 2.6 JuniorEngineerAgent (~4 prompts)

Junior Engineers handle simpler tasks with additional guidance and mentoring emphasis.

| # | Prompt | Location | Size | Variables |
|---|--------|----------|------|-----------|
| 1 | **System message** | Constructor / init | ~20 lines | Agent name, assigned task, mentor name |
| 2 | **Implementation instructions** | Code generation | ~40 lines | Task description, architecture context, examples, learning notes |
| 3 | **Self-review criteria** | Pre-submit review | ~25 lines | Own PR diff, common mistakes checklist, learning goals |
| 4 | **Rework handling** | Review feedback response | ~20 lines | Review comments, original code, mentoring guidance |

**Notes**: Junior prompts are ~20% longer than Senior equivalents due to:
- Explicit mentoring language ("explain your reasoning", "consider edge cases")
- Common mistakes checklists
- Learning goal tracking
- More detailed examples in implementation instructions

The template system should support a **base + override** pattern where the Junior implementation
template extends the shared engineer implementation template with additional sections.

---

### 2.7 TestEngineerAgent (~8+ prompts)

The Test Engineer has the most prompts of any agent, covering the full testing lifecycle.

| # | Prompt | Location | Size | Variables |
|---|--------|----------|------|-----------|
| 1 | **System message** | Constructor / init | ~20 lines | Agent name, project description, test framework |
| 2 | **Test strategy** | Strategy document generation | ~35 lines | Architecture doc, PM spec, tech stack, risk areas |
| 3 | **Test code generation** | Test implementation | ~40 lines | Source code under test, test framework, coverage targets |
| 4 | **Testability assessment** | Code review for testability | ~20 lines | Source code, dependency injection patterns, coupling analysis |
| 5 | **Source bug classification** | Bug categorization | ~15 lines | Bug report, severity criteria, reproduction steps |
| 6 | **Rework instructions** | Test failure remediation | ~15 lines | Failed tests, error messages, suggested fixes |
| 7 | **Coverage analysis** | Coverage gap identification | ~20 lines | Coverage report, uncovered paths, risk assessment |
| 8 | **Integration test patterns** | Integration test generation | ~25 lines | Service boundaries, API contracts, test doubles strategy |
| 9 | **UI test patterns** | UI/E2E test generation | ~20 lines | UI components, user flows, accessibility checks |

**Notes**: The Test Engineer's prompts are highly domain-specific. The **test strategy** prompt
must align with the architecture document's component boundaries, and the **test code generation**
prompt must match the project's test framework (xUnit, NUnit, MSTest, Jest, pytest, etc.).

Template variables for the Test Engineer will include `{{test_framework}}`, `{{coverage_target}}`,
`{{tech_stack}}`, and `{{source_code}}` — the latter potentially being very large (entire file
contents). The template system must handle large variable values gracefully.

---

### 2.8 EngineerAgentBase (~3 shared prompts)

The abstract base class for Senior and Junior Engineers contains shared prompt logic.

| # | Prompt | Location | Size | Variables |
|---|--------|----------|------|-----------|
| 1 | **Common implementation patterns** | Shared code generation preamble | ~20 lines | Tech stack, coding conventions, error handling patterns |
| 2 | **File change instructions** | PR file modification guidance | ~15 lines | File list, change scope, backward compatibility rules |
| 3 | **Context building** | Task context assembly | ~15 lines | Architecture doc, engineering plan, related PRs |

**Notes**: These prompts are prepended to role-specific prompts in both Senior and Junior
Engineers. In the template system, they become shared fragments included via
`{{> shared/implementation-patterns}}` or similar syntax.

---

### 2.9 Summary Statistics

| Metric | Value |
|--------|-------|
| Total hardcoded prompt blocks | **39+** |
| Total approximate lines of prompt text | **~750** |
| Agent files containing prompts | **8** |
| Agents with 5+ prompts | **4** (PM, Architect, PE, TE) |
| Largest single prompt | **~60 lines** (PE code review) |
| Shared/duplicated fragments | **~5** (code style, PR format, review output, project context, implementation patterns) |
| Existing externalized agents | **2** (CustomAgent, SmeAgent via RoleContextProvider) |

---

## 3. Template System Design

### 3.1 File Structure Convention

All prompt templates live under a `prompts/` directory at the project root, organized by agent
role:

```
prompts/{agent-role}/{prompt-name}.md
```

**Naming conventions**:
- Agent role directories use **kebab-case** matching the agent's conceptual name:
  `researcher`, `pm`, `architect`, `principal-engineer`, `senior-engineer`, `junior-engineer`,
  `test-engineer`
- Prompt file names use **kebab-case** describing the prompt's purpose:
  `system-message.md`, `code-review.md`, `test-strategy.md`
- Shared fragments live under `prompts/shared/`
- File extension is always `.md` for editor syntax highlighting and Markdown tooling support

### 3.2 YAML Frontmatter

Each template file begins with YAML frontmatter enclosed in `---` fences. The frontmatter
carries metadata about the prompt — it is **not** included in the rendered output.

```markdown
---
model_tier: premium
max_tokens: 4096
temperature: 0.7
version: "1.0"
tags: [code-review, principal-engineer]
description: "Principal Engineer code review rubric and scoring criteria"
variables:
  - pr_number
  - pr_diff
  - pr_description
  - architecture_doc
---
```

**Supported frontmatter fields**:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `model_tier` | string | No | Recommended model tier: `premium`, `standard`, `budget`, `local` |
| `max_tokens` | integer | No | Suggested max token limit for the response |
| `temperature` | float | No | Suggested temperature (0.0–1.0) |
| `version` | string | No | Semantic version for A/B testing and tracking |
| `tags` | string[] | No | Categorization tags for search and filtering |
| `description` | string | No | Human-readable description of the prompt's purpose |
| `variables` | string[] | No | Declared template variables (for validation) |
| `includes` | string[] | No | Declared fragment includes (for validation) |

**Design note**: Frontmatter fields are *advisory*. The agent's C# code ultimately controls
model selection and parameters. However, the `PromptTemplateService` exposes parsed frontmatter
so agents can optionally respect template-declared preferences.

### 3.3 Template Variables

Template variables use double-curly-brace syntax: `{{variable_name}}`.

```markdown
You are {{agent_name}}, the Principal Engineer for the {{project_name}} project.

## Your Task
Review PR #{{pr_number}} against the architecture defined in:

{{architecture_doc}}
```

**Variable rules**:
- Variable names use **snake_case**: `{{pr_number}}`, `{{project_description}}`
- Variables are replaced with their string values verbatim — no escaping or encoding
- Undefined variables produce a **warning log** and are left as-is in the output
  (e.g., `{{unknown_var}}` remains literally in the rendered text)
- Empty string is a valid value — `{{pr_diff}}` with value `""` renders as empty
- Variables can appear multiple times in a template; all occurrences are replaced
- Whitespace inside braces is trimmed: `{{ pr_number }}` is equivalent to `{{pr_number}}`

**Standard variables** available to all agents:

| Variable | Source | Description |
|----------|--------|-------------|
| `{{agent_name}}` | `AgentIdentity.DisplayName` | The agent's display name |
| `{{agent_role}}` | `AgentIdentity.Role` | The agent's role (e.g., "PrincipalEngineer") |
| `{{project_name}}` | `AgentSquadConfig.Project.Name` | Project name |
| `{{project_description}}` | `ProjectFileManager` | Full project description |
| `{{tech_stack}}` | `ProjectFileManager` | Technology stack summary |
| `{{research_summary}}` | `ProjectFileManager` (Research.md) | Research document content |
| `{{pm_spec}}` | `ProjectFileManager` (PMSpec.md) | PM specification content |
| `{{architecture_doc}}` | `ProjectFileManager` (Architecture.md) | Architecture document content |
| `{{engineering_plan}}` | `ProjectFileManager` (EngineeringPlan.md) | Engineering plan content |
| `{{role_context}}` | `RoleContextProvider` | Merged [ROLE CUSTOMIZATION] + [ROLE KNOWLEDGE] |

### 3.4 Include Fragments

Reusable prompt fragments are included using the `{{> path/to/fragment}}` syntax (inspired by
Mustache/Handlebars partials):

```markdown
You are {{agent_name}}, a Senior Engineer.

## Coding Standards
{{> shared/code-style-guidelines}}

## PR Format
{{> shared/pr-description-format}}
```

**Include rules**:
- Paths are relative to the `prompts/` root directory
- The `.md` extension is optional in include references
- Included fragments can themselves contain variables (resolved after inclusion)
- Included fragments can include other fragments (recursive)
- **Circular include detection**: The service tracks the include stack and throws
  `CircularIncludeException` if a fragment includes itself (directly or transitively)
- Maximum include depth: **10 levels** (configurable)
- Missing fragments produce a **warning log** and render as an empty string

### 3.5 PromptTemplateService Class Design

```csharp
namespace AgentSquad.Core.Prompts;

/// <summary>
/// Loads, parses, and renders prompt templates from .md files with YAML frontmatter
/// and {{variable}} substitution.
/// </summary>
public class PromptTemplateService : IPromptTemplateService
{
    private readonly string _promptsBasePath;
    private readonly ILogger<PromptTemplateService> _logger;
    private readonly ConcurrentDictionary<string, PromptTemplate> _cache;

    public PromptTemplateService(
        IOptions<AgentSquadConfig> config,
        ILogger<PromptTemplateService> logger)
    {
        _promptsBasePath = config.Value.Prompts?.BasePath ?? "prompts";
        _logger = logger;
        _cache = new ConcurrentDictionary<string, PromptTemplate>();
    }

    /// <summary>
    /// Renders a template with variable substitution (no fragment includes).
    /// </summary>
    /// <param name="templatePath">
    /// Relative path from prompts root, without .md extension.
    /// Example: "principal-engineer/code-review"
    /// </param>
    /// <param name="variables">Key-value pairs for {{variable}} substitution.</param>
    /// <returns>The rendered prompt string.</returns>
    public Task<string> RenderAsync(
        string templatePath,
        Dictionary<string, string> variables,
        CancellationToken ct = default);

    /// <summary>
    /// Renders a template with variable substitution AND fragment includes.
    /// Recursively resolves {{> path/to/fragment}} references.
    /// </summary>
    public Task<string> RenderWithIncludesAsync(
        string templatePath,
        Dictionary<string, string> variables,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the parsed frontmatter metadata for a template without rendering it.
    /// </summary>
    public Task<PromptMetadata?> GetMetadataAsync(
        string templatePath,
        CancellationToken ct = default);

    /// <summary>
    /// Invalidates the cache for a specific template or all templates.
    /// Used by FileSystemWatcher for hot-reload.
    /// </summary>
    public void InvalidateCache(string? templatePath = null);
}
```

**Supporting types**:

```csharp
/// <summary>
/// Parsed representation of a prompt template file.
/// </summary>
public record PromptTemplate
{
    public required PromptMetadata Metadata { get; init; }
    public required string Body { get; init; }
    public required string RawContent { get; init; }
    public required DateTimeOffset LoadedAt { get; init; }
}

/// <summary>
/// Parsed YAML frontmatter metadata.
/// </summary>
public record PromptMetadata
{
    public string? ModelTier { get; init; }
    public int? MaxTokens { get; init; }
    public float? Temperature { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Variables { get; init; } = [];
    public IReadOnlyList<string> Includes { get; init; } = [];
}

/// <summary>
/// Interface for DI registration and testing.
/// </summary>
public interface IPromptTemplateService
{
    Task<string> RenderAsync(
        string templatePath,
        Dictionary<string, string> variables,
        CancellationToken ct = default);

    Task<string> RenderWithIncludesAsync(
        string templatePath,
        Dictionary<string, string> variables,
        CancellationToken ct = default);

    Task<PromptMetadata?> GetMetadataAsync(
        string templatePath,
        CancellationToken ct = default);

    void InvalidateCache(string? templatePath = null);
}
```

### 3.6 Template Loading and Caching

```
Request Flow:
┌─────────────┐     ┌──────────────────┐     ┌─────────────────┐
│  Agent Code  │────>│ PromptTemplate   │────>│  File System    │
│              │     │   Service        │     │  (prompts/*.md) │
│ RenderAsync( │     │                  │     │                 │
│  "pe/review",│     │ 1. Check cache   │     │                 │
│  variables)  │     │ 2. Load if miss  │     │                 │
│              │<────│ 3. Parse YAML    │<────│                 │
│              │     │ 4. Substitute    │     │                 │
│ rendered     │     │ 5. Return        │     │                 │
│ prompt text  │     │                  │     │                 │
└─────────────┘     └──────────────────┘     └─────────────────┘
```

**Caching strategy**:
- Templates are cached in a `ConcurrentDictionary<string, PromptTemplate>` after first load
- Cache key is the normalized template path (e.g., `"principal-engineer/code-review"`)
- Cache entries include `LoadedAt` timestamp for staleness detection
- Cache is invalidated:
  - Explicitly via `InvalidateCache()` (called by FileSystemWatcher in Phase 5)
  - On application restart (cache is in-memory only)
- No TTL-based expiration — templates change infrequently; explicit invalidation is preferred

### 3.7 Fallback Behavior

If a template file is missing from disk:

1. **Log a warning**: `"Prompt template '{path}' not found, falling back to hardcoded default"`
2. **Return `null`** from the service (or throw a specific `TemplateNotFoundException`)
3. **Agent code** catches the null/exception and uses the original hardcoded string
4. This ensures **zero-downtime migration** — templates can be added incrementally without
   breaking agents that haven't been migrated yet

```csharp
// Agent code pattern during migration:
var prompt = await _templateService.RenderAsync("researcher/system-message", variables);
if (prompt is null)
{
    // Fallback to hardcoded default (removed after migration is complete)
    prompt = $"You are {Identity.DisplayName}, a research specialist...";
    _logger.LogWarning("Using hardcoded fallback for researcher/system-message");
}
```

### 3.8 Hot-Reload (Phase 5)

In development, a `FileSystemWatcher` monitors the `prompts/` directory and invalidates cache
entries when files change:

```csharp
public class PromptFileWatcher : IHostedService, IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly IPromptTemplateService _templateService;

    public Task StartAsync(CancellationToken ct)
    {
        _watcher = new FileSystemWatcher(_promptsBasePath, "*.md")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };
        _watcher.Changed += (_, e) => _templateService.InvalidateCache(
            NormalizePath(e.FullPath));
        _watcher.EnableRaisingEvents = true;
        return Task.CompletedTask;
    }
}
```

This is **optional** and only enabled when `AgentSquadConfig.Prompts.HotReload` is `true`
(defaults to `false` in production).

---

## 4. Concrete Before/After Examples

### 4.1 Example 1: Principal Engineer Code Review Prompt

This is the largest and most complex prompt in the codebase (~60 lines). It demonstrates the
highest-value migration.

#### Before (PrincipalEngineerAgent.cs)

```csharp
private async Task<string> ReviewPullRequestAsync(int prNumber, string diff, string description)
{
    var kernel = _modelRegistry.GetKernel(Identity.ModelTier);
    var chat = new ChatHistory();

    chat.AddSystemMessage($"""
        You are {Identity.DisplayName}, the Principal Engineer for the {_config.Project.Name} project.
        You are an expert code reviewer with deep knowledge of software architecture,
        design patterns, security best practices, and performance optimization.

        Your role is to provide thorough, constructive code reviews that maintain
        high engineering standards while being respectful and educational.
        """);

    var reviewPrompt = $"""
        Review Pull Request #{prNumber}.

        ## PR Description
        {description}

        ## Code Changes (Diff)
        ```diff
        {diff}
        ```

        ## Review Criteria

        Evaluate the code against the following categories, scoring each 1-5:

        ### 1. Correctness (Weight: 30%)
        - Does the code do what it claims to do?
        - Are edge cases handled?
        - Are error conditions properly managed?
        - Is the logic sound and free of bugs?

        ### 2. Security (Weight: 25%)
        - Are inputs validated and sanitized?
        - Are there potential injection vulnerabilities?
        - Is sensitive data properly protected?
        - Are authentication/authorization checks in place?

        ### 3. Performance (Weight: 15%)
        - Are there obvious performance bottlenecks?
        - Is resource usage (memory, CPU, I/O) reasonable?
        - Are appropriate data structures used?
        - Is caching considered where beneficial?

        ### 4. Maintainability (Weight: 20%)
        - Is the code readable and self-documenting?
        - Does it follow project conventions?
        - Is complexity kept manageable?
        - Are abstractions appropriate (not over/under-engineered)?

        ### 5. Test Coverage (Weight: 10%)
        - Are new behaviors covered by tests?
        - Are edge cases tested?
        - Are tests meaningful (not just ceremony)?

        ## Output Format

        Respond with a JSON object:
        ```json
        {{
            "overall_score": 4.2,
            "recommendation": "approve" | "request_changes" | "comment",
            "categories": {{
                "correctness": {{ "score": 5, "findings": [...] }},
                "security": {{ "score": 4, "findings": [...] }},
                "performance": {{ "score": 4, "findings": [...] }},
                "maintainability": {{ "score": 4, "findings": [...] }},
                "test_coverage": {{ "score": 3, "findings": [...] }}
            }},
            "critical_issues": [...],
            "suggestions": [...],
            "praise": [...]
        }}
        ```

        Score thresholds:
        - >= 4.0: Approve
        - 3.0-3.9: Approve with suggestions
        - < 3.0: Request changes
        """;

    chat.AddUserMessage(reviewPrompt);

    var service = kernel.GetRequiredService<IChatCompletionService>();
    var response = await service.GetChatMessageContentsAsync(chat, cancellationToken: ct);

    return response[^1].Content ?? "";
}
```

#### After

**File: `prompts/principal-engineer/system-message.md`**

```markdown
---
model_tier: premium
version: "1.0"
description: "Principal Engineer persona and expertise definition"
variables:
  - agent_name
  - project_name
---
You are {{agent_name}}, the Principal Engineer for the {{project_name}} project.
You are an expert code reviewer with deep knowledge of software architecture,
design patterns, security best practices, and performance optimization.

Your role is to provide thorough, constructive code reviews that maintain
high engineering standards while being respectful and educational.
```

**File: `prompts/principal-engineer/code-review.md`**

```markdown
---
model_tier: premium
max_tokens: 4096
temperature: 0.3
version: "1.0"
description: "Code review rubric with weighted scoring across 5 categories"
tags: [code-review, principal-engineer, scoring]
variables:
  - pr_number
  - pr_description
  - pr_diff
includes:
  - shared/review-output-format
---
Review Pull Request #{{pr_number}}.

## PR Description
{{pr_description}}

## Code Changes (Diff)
```diff
{{pr_diff}}
```

## Review Criteria

Evaluate the code against the following categories, scoring each 1-5:

### 1. Correctness (Weight: 30%)
- Does the code do what it claims to do?
- Are edge cases handled?
- Are error conditions properly managed?
- Is the logic sound and free of bugs?

### 2. Security (Weight: 25%)
- Are inputs validated and sanitized?
- Are there potential injection vulnerabilities?
- Is sensitive data properly protected?
- Are authentication/authorization checks in place?

### 3. Performance (Weight: 15%)
- Are there obvious performance bottlenecks?
- Is resource usage (memory, CPU, I/O) reasonable?
- Are appropriate data structures used?
- Is caching considered where beneficial?

### 4. Maintainability (Weight: 20%)
- Is the code readable and self-documenting?
- Does it follow project conventions?
- Is complexity kept manageable?
- Are abstractions appropriate (not over/under-engineered)?

### 5. Test Coverage (Weight: 10%)
- Are new behaviors covered by tests?
- Are edge cases tested?
- Are tests meaningful (not just ceremony)?

## Output Format
{{> shared/review-output-format}}

## Score Thresholds
- >= 4.0: Approve
- 3.0-3.9: Approve with suggestions
- < 3.0: Request changes
```

**Updated C# code in PrincipalEngineerAgent.cs**:

```csharp
private async Task<string> ReviewPullRequestAsync(int prNumber, string diff, string description)
{
    var kernel = _modelRegistry.GetKernel(Identity.ModelTier);
    var chat = new ChatHistory();

    // System message — externalized to template
    var systemPrompt = await _templateService.RenderAsync(
        "principal-engineer/system-message",
        new Dictionary<string, string>
        {
            ["agent_name"] = Identity.DisplayName,
            ["project_name"] = _config.Project.Name
        }, ct);
    chat.AddSystemMessage(systemPrompt);

    // Review prompt — externalized with fragment includes
    var reviewPrompt = await _templateService.RenderWithIncludesAsync(
        "principal-engineer/code-review",
        new Dictionary<string, string>
        {
            ["pr_number"] = prNumber.ToString(),
            ["pr_description"] = description,
            ["pr_diff"] = diff
        }, ct);
    chat.AddUserMessage(reviewPrompt);

    // Orchestration logic stays in C# — unchanged
    var service = kernel.GetRequiredService<IChatCompletionService>();
    var response = await service.GetChatMessageContentsAsync(chat, cancellationToken: ct);

    return response[^1].Content ?? "";
}
```

**What changed**:
- 🟢 Prompt content moved to `.md` files — editable without recompilation
- 🟢 Frontmatter captures metadata (model tier, temperature, version)
- 🟢 Shared review output format extracted to a fragment
- 🔴 Orchestration logic (when to review, ChatHistory construction, kernel selection) stays in C#

---

### 4.2 Example 2: Researcher System Message

#### Before (ResearcherAgent.cs)

```csharp
chat.AddSystemMessage($"""
    You are {Identity.DisplayName}, a research specialist for the {_config.Project.Name} project.
    Your expertise spans software engineering, technology evaluation, and market analysis.

    You conduct thorough research to inform the project's technical decisions.
    You produce well-structured research documents with citations and evidence.

    Project Description:
    {_projectDescription}
    """);
```

#### After

**File: `prompts/researcher/system-message.md`**

```markdown
---
model_tier: standard
version: "1.0"
description: "Researcher persona and project context"
variables:
  - agent_name
  - project_name
  - project_description
---
You are {{agent_name}}, a research specialist for the {{project_name}} project.
Your expertise spans software engineering, technology evaluation, and market analysis.

You conduct thorough research to inform the project's technical decisions.
You produce well-structured research documents with citations and evidence.

## Project Description
{{project_description}}
```

**Updated C# code**:

```csharp
var systemPrompt = await _templateService.RenderAsync(
    "researcher/system-message",
    new Dictionary<string, string>
    {
        ["agent_name"] = Identity.DisplayName,
        ["project_name"] = _config.Project.Name,
        ["project_description"] = _projectDescription
    }, ct);
chat.AddSystemMessage(systemPrompt);
```

---

### 4.3 Example 3: Test Engineer Test Strategy

#### Before (TestEngineerAgent.cs)

```csharp
var strategyPrompt = $"""
    Based on the project architecture and requirements, create a comprehensive test strategy.

    ## Architecture
    {architectureDoc}

    ## Requirements (PM Spec)
    {pmSpec}

    ## Technology Stack
    {techStack}

    ## Test Framework
    {testFramework}

    Create a test strategy covering:
    1. Unit test approach and coverage targets
    2. Integration test boundaries and patterns
    3. End-to-end test scenarios
    4. Performance test considerations
    5. Security test checklist
    6. Test data management strategy
    7. CI/CD integration plan

    Focus on high-risk areas identified in the architecture:
    {riskAreas}

    Target coverage: {coverageTarget}%

    Output a structured Markdown document.
    """;
```

#### After

**File: `prompts/test-engineer/test-strategy.md`**

```markdown
---
model_tier: standard
max_tokens: 4096
version: "1.0"
description: "Comprehensive test strategy generation from architecture and requirements"
tags: [test-strategy, test-engineer, planning]
variables:
  - architecture_doc
  - pm_spec
  - tech_stack
  - test_framework
  - risk_areas
  - coverage_target
---
Based on the project architecture and requirements, create a comprehensive test strategy.

## Architecture
{{architecture_doc}}

## Requirements (PM Spec)
{{pm_spec}}

## Technology Stack
{{tech_stack}}

## Test Framework
{{test_framework}}

## Strategy Requirements

Create a test strategy covering:
1. Unit test approach and coverage targets
2. Integration test boundaries and patterns
3. End-to-end test scenarios
4. Performance test considerations
5. Security test checklist
6. Test data management strategy
7. CI/CD integration plan

## High-Risk Areas
Focus on high-risk areas identified in the architecture:
{{risk_areas}}

## Coverage Target
Target coverage: {{coverage_target}}%

## Output Format
Output a structured Markdown document with clear sections, rationale for each decision,
and concrete examples where applicable.
```

**Updated C# code**:

```csharp
var strategyPrompt = await _templateService.RenderAsync(
    "test-engineer/test-strategy",
    new Dictionary<string, string>
    {
        ["architecture_doc"] = architectureDoc,
        ["pm_spec"] = pmSpec,
        ["tech_stack"] = techStack,
        ["test_framework"] = testFramework,
        ["risk_areas"] = riskAreas,
        ["coverage_target"] = coverageTarget.ToString()
    }, ct);
```

---

## 5. Shared Fragments

Shared fragments eliminate duplication across agents. They live in `prompts/shared/` and are
included via the `{{> shared/fragment-name}}` syntax.

### 5.1 `prompts/shared/code-style-guidelines.md`

Referenced by: Senior Engineer, Junior Engineer, Test Engineer (for test code), PE (for review)

```markdown
## Coding Standards

Follow these project coding conventions:
- Use file-scoped namespaces throughout
- Prefer `record` types for DTOs and immutable data
- Use `ArgumentNullException.ThrowIfNull()` for guard clauses
- Suffix async methods with `Async` and accept `CancellationToken ct = default`
- Use `ILogger<T>` with structured logging (named parameters, not string interpolation)
- Implement `IDisposable` with a `_disposed` flag when managing resources
- Enable nullable reference types; resolve all nullable warnings
- Keep methods under 30 lines where possible; extract helper methods
```

### 5.2 `prompts/shared/pr-description-format.md`

Referenced by: Senior Engineer, Junior Engineer (PR creation)

```markdown
## PR Description Format

Structure the PR description as follows:

### Summary
A 1-2 sentence overview of what this PR does.

### Changes
- Bullet list of specific changes made
- Group by file or feature area

### Testing
- What tests were added or modified
- How to verify the changes manually

### Related
- Link to the task/issue this addresses
- Note any dependent PRs
```

### 5.3 `prompts/shared/review-output-format.md`

Referenced by: Principal Engineer (code review), Architect (architecture review)

```markdown
Respond with a JSON object following this structure:
```json
{
    "overall_score": 4.2,
    "recommendation": "approve | request_changes | comment",
    "categories": {
        "<category_name>": {
            "score": 5,
            "findings": ["Finding 1", "Finding 2"]
        }
    },
    "critical_issues": [
        {
            "severity": "critical | major | minor",
            "location": "file:line",
            "description": "What's wrong",
            "suggestion": "How to fix"
        }
    ],
    "suggestions": ["Suggestion 1", "Suggestion 2"],
    "praise": ["What was done well"]
}
```

Guidelines:
- Be specific about file and line locations
- Provide actionable fix suggestions for every issue
- Include at least one item in `praise` — acknowledge good work
```

### 5.4 `prompts/shared/project-context.md`

Referenced by: All agents that need project background

```markdown
## Project Context

**Project**: {{project_name}}

### Description
{{project_description}}

### Technology Stack
{{tech_stack}}

### Key Architectural Decisions
{{architecture_summary}}
```

### 5.5 `prompts/shared/implementation-patterns.md`

Referenced by: Senior Engineer, Junior Engineer (via EngineerAgentBase)

```markdown
## Implementation Patterns

When implementing code changes:

### Error Handling
- Wrap external calls in try/catch with specific exception types
- Log exceptions with context before re-throwing or handling
- Use `OperationCanceledException` for clean cancellation support
- Never swallow exceptions silently

### Dependency Injection
- Accept dependencies through constructor injection
- Use `IOptions<T>` for configuration
- Register services in extension methods (e.g., `AddFeatureName()`)

### File Organization
- One primary type per file
- File name matches the primary type name
- Group related files in feature folders

### Backward Compatibility
- Don't remove public API members without deprecation
- Add new optional parameters with defaults
- Use feature flags for behavioral changes
```

---

## 6. Relationship with Existing RoleContextProvider

### 6.1 Current RoleContextProvider Behavior

The `RoleContextProvider` (used by `CustomAgent` and `SmeAgent`) reads `.agent.md` files and
injects two sections into agent prompts:

- **[ROLE CUSTOMIZATION]**: Agent-specific behavioral overrides (tone, focus areas, constraints)
- **[ROLE KNOWLEDGE]**: Domain knowledge the agent should have (terminology, processes, standards)

These are injected into the agent's system message via placeholder replacement.

### 6.2 How the Two Systems Complement Each Other

| Aspect | RoleContextProvider | PromptTemplateService |
|--------|--------------------|-----------------------|
| **Purpose** | Agent persona and domain knowledge | Task-specific prompt content |
| **Source files** | `.agent.md` (per-agent config) | `prompts/**/*.md` (prompt library) |
| **Granularity** | One file per agent | Multiple files per agent |
| **Content type** | Who the agent *is* | What the agent should *do* |
| **Variable support** | Placeholder tokens only | Full `{{variable}}` substitution |
| **Fragment includes** | Not supported | Supported via `{{> path}}` |
| **Metadata** | Not supported | YAML frontmatter |
| **Used by** | CustomAgent, SmeAgent | All 7 core agents (after migration) |

### 6.3 Integration Pattern

Templates can reference the role context via a `{{role_context}}` variable, which the agent
populates from `RoleContextProvider` before rendering:

```csharp
// In agent initialization:
var roleContext = await _roleContextProvider.GetContextAsync(Identity.Role);

// When rendering a template:
var systemPrompt = await _templateService.RenderAsync(
    "pm/system-message",
    new Dictionary<string, string>
    {
        ["agent_name"] = Identity.DisplayName,
        ["project_name"] = _config.Project.Name,
        ["role_context"] = roleContext  // Injected from RoleContextProvider
    }, ct);
```

This means the same prompt template can produce different behaviors depending on the
`.agent.md` configuration — the template defines the structure, and `RoleContextProvider`
fills in the persona.

### 6.4 Future Convergence (Optional)

In the long term, `RoleContextProvider` could be refactored to become a special case of
`PromptTemplateService` — loading `.agent.md` files as templates with their own frontmatter.
However, this is **not** part of this plan. The two systems will coexist independently until a
natural convergence point emerges.

---

## 7. Migration Strategy (5 Phases)

### Overview

The migration follows a risk-ordered, incremental approach. Each phase is independently
deployable and adds value on its own. No phase requires subsequent phases to be completed.

```
Phase 1          Phase 2         Phase 3           Phase 4              Phase 5
Foundation +     Shared          Engineer          Complex Agents       Polish +
Researcher       Fragments + PM  Agents            (Arch, PE, TE)       Hot-Reload
─────────────>  ──────────────>  ──────────────>   ──────────────────>  ──────────>
  ~2 days          ~2 days         ~3 days            ~4 days            ~2 days
```

**Total estimated effort**: ~13 days (conservative, assumes thorough testing at each phase)

---

### Phase 1: Foundation + Researcher (Lowest Risk)

**Goal**: Build the template infrastructure and prove it works with the simplest agent.

**Duration**: ~2 days

#### Tasks

1. **Create `IPromptTemplateService` interface** and `PromptTemplateService` implementation
   in `AgentSquad.Core/Prompts/`:
   - YAML frontmatter parsing (using `YamlDotNet` or manual parsing)
   - `{{variable}}` substitution with Regex
   - In-memory `ConcurrentDictionary` cache
   - Null/warning fallback for missing templates
   - Thread-safe for concurrent agent access

2. **Create `PromptTemplate` and `PromptMetadata` record types** (as designed in Section 3)

3. **Register `IPromptTemplateService`** as a singleton in the DI container
   (`ServiceCollectionExtensions` or `Program.cs`)

4. **Create the `prompts/` directory structure**:
   ```
   prompts/
   └── researcher/
       ├── system-message.md
       ├── research-instructions.md
       └── summary-generation.md
   ```

5. **Extract ResearcherAgent's 3 prompts** to template files:
   - Copy existing prompt strings verbatim into `.md` files
   - Replace interpolated variables with `{{variable}}` placeholders
   - Add YAML frontmatter

6. **Update ResearcherAgent.cs**:
   - Inject `IPromptTemplateService` via constructor
   - Replace hardcoded strings with `_templateService.RenderAsync()` calls
   - Keep fallback to hardcoded strings during transition

7. **Verify behavioral equivalence**:
   - Log both the externalized prompt output and the original hardcoded output
   - Diff them to confirm identical rendering
   - Run agent end-to-end and verify same quality research output

8. **Write unit tests**:
   - `PromptTemplateServiceTests.RenderAsync_SubstitutesVariables`
   - `PromptTemplateServiceTests.RenderAsync_HandlesUndefinedVariables`
   - `PromptTemplateServiceTests.RenderAsync_ParsesFrontmatter`
   - `PromptTemplateServiceTests.RenderAsync_ReturnsNullForMissingTemplate`
   - `PromptTemplateServiceTests.RenderAsync_CachesAfterFirstLoad`

#### Definition of Done
- [x] `PromptTemplateService` implemented and registered in DI
- [x] ResearcherAgent uses external templates for all 3 prompts
- [x] Hardcoded fallbacks exist for all 3 prompts
- [x] Unit tests pass
- [x] No behavioral regression in Researcher output

#### Risks
- **Low**: Researcher is the simplest agent with fewest prompts
- **Mitigation**: Fallback to hardcoded strings if template loading fails

---

### Phase 2: Shared Fragments + PM

**Goal**: Prove the fragment include system works and migrate the PM, which produces the
critical PMSpec.md consumed by all downstream agents.

**Duration**: ~2 days

#### Tasks

1. **Implement fragment includes** (`{{> path}}` syntax) in `PromptTemplateService`:
   - `RenderWithIncludesAsync` method
   - Recursive resolution with circular dependency detection
   - Maximum depth enforcement (default: 10)

2. **Create shared fragments**:
   ```
   prompts/shared/
   ├── code-style-guidelines.md
   ├── pr-description-format.md
   ├── review-output-format.md
   └── project-context.md
   ```

3. **Extract PMAgent's 6 prompts** to template files:
   ```
   prompts/pm/
   ├── system-message.md
   ├── spec-generation.md
   ├── review-criteria.md
   ├── clarification-questions.md
   ├── spawn-decision.md
   └── executive-communication.md
   ```

4. **Verify PM Spec output**:
   - PMSpec.md format must remain unchanged (downstream agents parse it)
   - Run full pipeline: Researcher → PM → verify PMSpec.md structure

5. **Write additional tests**:
   - `PromptTemplateServiceTests.RenderWithIncludesAsync_ResolvesFragments`
   - `PromptTemplateServiceTests.RenderWithIncludesAsync_DetectsCircularIncludes`
   - `PromptTemplateServiceTests.RenderWithIncludesAsync_HandlesNestedIncludes`
   - `PromptTemplateServiceTests.RenderWithIncludesAsync_WarnsOnMissingFragment`
   - Integration test: PM spec generation produces valid output

#### Definition of Done
- [x] Fragment include system implemented
- [x] Shared fragments created and tested
- [x] PMAgent uses external templates for all 6 prompts
- [x] PMSpec.md output format unchanged
- [x] All tests pass

#### Risks
- **Medium**: PM Spec format changes would break downstream agents
- **Mitigation**: Output comparison tests; keep hardcoded fallbacks until verified

---

### Phase 3: Engineer Agents

**Goal**: Migrate Senior, Junior, and the shared EngineerAgentBase prompts, proving the
base + override pattern.

**Duration**: ~3 days

#### Tasks

1. **Extract EngineerAgentBase's 3 shared prompts**:
   ```
   prompts/shared/
   ├── implementation-patterns.md  (new)
   ├── file-change-instructions.md (new)
   └── context-building.md         (new)
   ```
   These become shared fragments that both Senior and Junior templates include.

2. **Extract SeniorEngineerAgent's 4 prompts**:
   ```
   prompts/senior-engineer/
   ├── system-message.md
   ├── implementation.md          (includes shared/implementation-patterns)
   ├── self-review.md
   └── rework.md
   ```

3. **Extract JuniorEngineerAgent's 4 prompts**:
   ```
   prompts/junior-engineer/
   ├── system-message.md
   ├── implementation.md          (includes shared/implementation-patterns + mentoring)
   ├── self-review.md             (includes common-mistakes checklist)
   └── rework.md
   ```

4. **Validate the base + override pattern**:
   - Senior `implementation.md` includes `{{> shared/implementation-patterns}}` then adds
     Senior-specific instructions
   - Junior `implementation.md` includes the same fragment then adds mentoring language,
     examples, and learning goals
   - Verify that both render correctly with distinct outputs

5. **Test PR creation flow**:
   - Engineers create PRs with description generated from templates
   - PR description must match expected format (uses `{{> shared/pr-description-format}}`)

6. **Write tests**:
   - Base + override pattern renders correctly for Senior
   - Base + override pattern renders correctly for Junior (with extra sections)
   - PR description format is consistent between Senior and Junior

#### Definition of Done
- [x] EngineerAgentBase shared prompts are fragments
- [x] SeniorEngineerAgent uses external templates for all 4 prompts
- [x] JuniorEngineerAgent uses external templates for all 4 prompts
- [x] Base + override pattern verified
- [x] PR descriptions render correctly
- [x] All tests pass

#### Risks
- **Medium**: Inheritance pattern (shared base + role-specific additions) may be tricky
  to get right in the template system
- **Mitigation**: Test both role variants side-by-side; shared fragment is append-only

---

### Phase 4: Complex Agents (Architect, PE, Test Engineer)

**Goal**: Migrate the most complex agents with the largest and most variable-rich prompts.

**Duration**: ~4 days

#### Tasks

1. **Extract ArchitectAgent's 5 prompts**:
   ```
   prompts/architect/
   ├── system-message.md
   ├── architecture-generation.md   (~50 lines, many variables)
   ├── pr-review.md
   ├── technology-evaluation.md
   └── design-patterns.md
   ```

2. **Extract PrincipalEngineerAgent's 6 prompts**:
   ```
   prompts/principal-engineer/
   ├── system-message.md
   ├── engineering-plan.md
   ├── task-decomposition.md
   ├── code-review.md              (~60 lines — flagship migration)
   ├── spawn-decision.md
   └── leader-election.md
   ```

3. **Extract TestEngineerAgent's 8+ prompts**:
   ```
   prompts/test-engineer/
   ├── system-message.md
   ├── test-strategy.md
   ├── test-generation.md
   ├── testability-assessment.md
   ├── source-bug-classification.md
   ├── rework-instructions.md
   ├── coverage-analysis.md
   └── ui-test-patterns.md
   ```

4. **Handle large variable values**:
   - Test Engineer's `{{source_code}}` can be entire file contents (thousands of lines)
   - Architect's `{{pm_spec}}` can be 100+ lines
   - Verify that variable substitution handles large values without performance degradation
   - Consider streaming or chunked rendering if needed (unlikely for current scale)

5. **PE Code Review — flagship migration**:
   - Extract the ~60-line review prompt with its scoring rubric
   - Include `{{> shared/review-output-format}}` for the JSON schema
   - Verify that JSON output format is preserved exactly
   - Run side-by-side comparison of review quality

6. **Write comprehensive tests**:
   - Each agent's templates render correctly with full variable sets
   - Large variable values (10KB+) substitute without issues
   - PE review output JSON schema is preserved
   - Architect's 5-turn conversation renders all turns correctly

#### Definition of Done
- [x] ArchitectAgent uses external templates for all 5 prompts
- [x] PrincipalEngineerAgent uses external templates for all 6 prompts
- [x] TestEngineerAgent uses external templates for all 8+ prompts
- [x] PE code review flagship migration verified
- [x] Large variable handling tested
- [x] All tests pass

#### Risks
- **High**: These agents have the most complex prompts with many variables and
  interdependencies
- **Mitigation**: Migrate one agent at a time within the phase; keep fallbacks; extensive
  side-by-side testing

---

### Phase 5: Polish + Hot-Reload

**Goal**: Add development-time quality-of-life features and clean up.

**Duration**: ~2 days

#### Tasks

1. **Implement `PromptFileWatcher`** (`IHostedService`):
   - `FileSystemWatcher` on `prompts/` directory
   - Invalidate cache entries on file change
   - Debounce rapid changes (100ms window)
   - Enable via `AgentSquadConfig.Prompts.HotReload` (default: `false`)

2. **Add prompt versioning support**:
   - Read `version` from frontmatter
   - Log which version of each prompt is being used
   - Support version selection via config: `"Prompts.Overrides.pe-code-review": "2.0"`

3. **Add A/B testing support**:
   - Config-based prompt variant selection
   - Log variant used for each rendering (for quality comparison)
   - Example config:
     ```json
     {
       "Prompts": {
         "ABTest": {
           "principal-engineer/code-review": {
             "variants": ["1.0", "2.0"],
             "distribution": [50, 50]
           }
         }
       }
     }
     ```

4. **Remove hardcoded fallbacks**:
   - After all phases are verified, remove the fallback hardcoded strings from agent code
   - Templates become the single source of truth
   - Missing templates now throw `TemplateNotFoundException` instead of falling back

5. **Documentation**:
   - Update `docs/architecture.md` with prompt externalization design
   - Add `prompts/README.md` explaining how to create and modify templates
   - Update agent implementation guide with template usage patterns
   - Document the decision rule in developer onboarding docs

6. **Write hot-reload tests**:
   - File change triggers cache invalidation
   - Next render picks up updated template
   - Rapid changes are debounced correctly

#### Definition of Done
- [x] Hot-reload working in development
- [x] Prompt versioning logged
- [x] A/B testing framework in place
- [x] All hardcoded fallbacks removed
- [x] Documentation updated
- [x] All tests pass

#### Risks
- **Low**: All functional migration is complete; this is pure polish
- **Mitigation**: Hot-reload is opt-in; A/B testing is additive

---

## 8. Testing Strategy

### 8.1 Unit Tests

Unit tests validate the `PromptTemplateService` in isolation.

#### Core Rendering Tests

```csharp
public class PromptTemplateServiceTests : IDisposable
{
    [Fact]
    public async Task RenderAsync_SubstitutesAllVariables()
    {
        // Arrange: template with {{name}} and {{role}}
        // Act: render with { "name": "Alice", "role": "Engineer" }
        // Assert: output contains "Alice" and "Engineer", no {{}} markers
    }

    [Fact]
    public async Task RenderAsync_LeavesUndefinedVariablesAsIs()
    {
        // Arrange: template with {{name}} and {{unknown}}
        // Act: render with { "name": "Alice" } (missing "unknown")
        // Assert: output contains "Alice" and literal "{{unknown}}"
    }

    [Fact]
    public async Task RenderAsync_HandlesEmptyVariableValue()
    {
        // Arrange: template with {{description}}
        // Act: render with { "description": "" }
        // Assert: output has empty string where {{description}} was
    }

    [Fact]
    public async Task RenderAsync_TrimsWhitespaceInVariableNames()
    {
        // Arrange: template with {{ name }} (spaces inside braces)
        // Act: render with { "name": "Alice" }
        // Assert: substitution works correctly
    }

    [Fact]
    public async Task RenderAsync_HandlesMultipleOccurrencesOfSameVariable()
    {
        // Arrange: template with {{name}} appearing 3 times
        // Act: render with { "name": "Alice" }
        // Assert: all 3 occurrences replaced
    }
}
```

#### Frontmatter Tests

```csharp
[Fact]
public async Task GetMetadataAsync_ParsesAllFrontmatterFields()
{
    // Arrange: template with model_tier, max_tokens, temperature, version, tags
    // Act: get metadata
    // Assert: all fields parsed correctly with correct types
}

[Fact]
public async Task RenderAsync_ExcludesFrontmatterFromOutput()
{
    // Arrange: template with frontmatter + body
    // Act: render
    // Assert: output starts with body content, no --- or YAML
}
```

#### Fragment Include Tests

```csharp
[Fact]
public async Task RenderWithIncludesAsync_ResolvesSimpleFragment()
{
    // Arrange: template with {{> shared/guidelines}}, fragment file exists
    // Act: render with includes
    // Assert: fragment content appears in output
}

[Fact]
public async Task RenderWithIncludesAsync_ResolvesNestedFragments()
{
    // Arrange: template A includes fragment B, which includes fragment C
    // Act: render with includes
    // Assert: all three levels rendered correctly
}

[Fact]
public async Task RenderWithIncludesAsync_DetectsCircularIncludes()
{
    // Arrange: fragment A includes fragment B, fragment B includes fragment A
    // Act & Assert: throws CircularIncludeException
}

[Fact]
public async Task RenderWithIncludesAsync_EnforcesMaxDepth()
{
    // Arrange: 11 levels of nested includes (exceeding max depth of 10)
    // Act & Assert: throws MaxIncludeDepthExceededException
}

[Fact]
public async Task RenderWithIncludesAsync_WarnsOnMissingFragment()
{
    // Arrange: template with {{> shared/nonexistent}}
    // Act: render with includes
    // Assert: warning logged, missing fragment renders as empty string
}

[Fact]
public async Task RenderWithIncludesAsync_SubstitutesVariablesInFragments()
{
    // Arrange: fragment contains {{project_name}}
    // Act: render parent template that includes fragment, with variables
    // Assert: variable in fragment is substituted
}
```

#### Cache Tests

```csharp
[Fact]
public async Task RenderAsync_CachesTemplateAfterFirstLoad()
{
    // Arrange: template file on disk
    // Act: render twice
    // Assert: file read only once (verify via mock or counter)
}

[Fact]
public void InvalidateCache_RemovesCachedTemplate()
{
    // Arrange: template is cached
    // Act: invalidate specific template
    // Assert: next render reloads from disk
}

[Fact]
public void InvalidateCache_ClearsAllWhenNoPathSpecified()
{
    // Arrange: multiple templates cached
    // Act: invalidate with null path
    // Assert: all entries cleared
}
```

### 8.2 Integration Tests

Integration tests verify that the template system works correctly within the full agent pipeline.

```csharp
public class PromptTemplateIntegrationTests : IDisposable
{
    [Fact]
    public async Task ResearcherAgent_UsesExternalTemplates_ProducesSameOutput()
    {
        // Build DI container with real PromptTemplateService
        // Run Researcher with external templates
        // Compare rendered prompts against known-good hardcoded versions
    }

    [Fact]
    public async Task PMAgent_SpecGeneration_PreservesOutputFormat()
    {
        // Run PM with external templates
        // Parse PMSpec.md output
        // Verify all expected sections present: Executive Summary, Goals, etc.
    }

    [Fact]
    public async Task PEAgent_CodeReview_ProducesValidJson()
    {
        // Run PE code review with external template
        // Parse JSON output
        // Verify schema: overall_score, recommendation, categories, etc.
    }

    [Fact]
    public async Task EngineerAgents_SharedFragments_IncludedCorrectly()
    {
        // Render Senior and Junior implementation prompts
        // Both should contain shared/implementation-patterns content
        // Junior should additionally contain mentoring sections
    }
}
```

### 8.3 Behavioral Regression Tests

These tests compare AI output quality between hardcoded and externalized prompts.

**Approach**:
1. Capture a set of reference inputs (project description, research, PR diffs, etc.)
2. Run each agent with **hardcoded** prompts and save outputs
3. Run each agent with **externalized** prompts using the same inputs
4. Compare outputs for:
   - Structural similarity (same sections, same format)
   - Content quality (manual review for a sample)
   - JSON schema compliance (where applicable)

**Note**: These are not automated pass/fail tests. They produce comparison reports for human
review. AI output is inherently non-deterministic, so exact match is not expected.

### 8.4 Missing File Fallback Tests

```csharp
[Fact]
public async Task RenderAsync_ReturnsNullWhenTemplateFileMissing()
{
    // Template path doesn't exist on disk
    // Service returns null (not exception)
    // Agent falls back to hardcoded string
}

[Fact]
public async Task Agent_GracefullyFallsBackWhenTemplatesMissing()
{
    // Delete all template files
    // Run agent
    // Agent should work normally using hardcoded fallbacks
    // Warning logs emitted for each missing template
}
```

### 8.5 Edge Case Tests

```csharp
[Fact]
public async Task RenderAsync_HandlesEmptyTemplate()
{
    // Template file exists but has empty body (only frontmatter)
    // Returns empty string
}

[Fact]
public async Task RenderAsync_HandlesVeryLargeVariableValues()
{
    // Variable value is 100KB of text (e.g., full source file)
    // Substitution completes without timeout or OOM
}

[Fact]
public async Task RenderAsync_HandlesSpecialCharactersInVariables()
{
    // Variable value contains {{, }}, regex special chars, newlines
    // All rendered literally without interpretation
}

[Fact]
public async Task RenderAsync_HandlesTemplateWithNoBraces()
{
    // Template has no variables — just static text
    // Returns the body as-is
}

[Fact]
public async Task RenderAsync_HandlesConcurrentAccess()
{
    // 50 concurrent render calls for different templates
    // All complete without errors or data corruption
}
```

### 8.6 Hot-Reload Tests (Phase 5)

```csharp
[Fact]
public async Task FileWatcher_InvalidatesCacheOnFileChange()
{
    // Write template file
    // Render (caches it)
    // Modify file on disk
    // Wait for FileSystemWatcher event
    // Render again — should pick up changes
}

[Fact]
public async Task FileWatcher_DebouncesRapidChanges()
{
    // Modify file 10 times rapidly
    // Cache should only be invalidated once (after debounce window)
}
```

---

## 9. File Organization

### 9.1 Complete Directory Tree

```
prompts/
├── README.md                           # How to create and modify templates
│
├── shared/                             # Reusable fragments included by multiple agents
│   ├── code-style-guidelines.md        # Coding standards (Senior, Junior, TE, PE)
│   ├── pr-description-format.md        # PR body template (Senior, Junior)
│   ├── review-output-format.md         # JSON review schema (PE, Architect)
│   ├── project-context.md              # Project description + tech stack (all agents)
│   ├── implementation-patterns.md      # Error handling, DI, file org (Senior, Junior)
│   ├── file-change-instructions.md     # PR file modification guidance (Senior, Junior)
│   └── context-building.md             # Task context assembly (Senior, Junior)
│
├── researcher/                         # ResearcherAgent prompts (Phase 1)
│   ├── system-message.md               # Researcher persona and expertise
│   ├── research-instructions.md        # How to conduct research
│   └── summary-generation.md           # How to synthesize findings
│
├── pm/                                 # PMAgent prompts (Phase 2)
│   ├── system-message.md               # PM persona
│   ├── spec-generation.md              # PMSpec.md creation instructions
│   ├── review-criteria.md              # Spec quality review rubric
│   ├── clarification-questions.md      # Research gap analysis
│   ├── spawn-decision.md               # When to spawn additional agents
│   └── executive-communication.md      # Status reporting format
│
├── architect/                          # ArchitectAgent prompts (Phase 4)
│   ├── system-message.md               # Architect persona
│   ├── architecture-generation.md      # Architecture.md creation instructions
│   ├── pr-review.md                    # Architecture compliance review
│   ├── technology-evaluation.md        # Technology comparison framework
│   └── design-patterns.md              # Pattern selection criteria
│
├── principal-engineer/                 # PrincipalEngineerAgent prompts (Phase 4)
│   ├── system-message.md               # PE persona
│   ├── engineering-plan.md             # EngineeringPlan.md creation
│   ├── task-decomposition.md           # Work breakdown structure
│   ├── code-review.md                  # 60-line review rubric (flagship)
│   ├── spawn-decision.md               # Engineer scaling criteria
│   └── leader-election.md              # PE fleet consensus
│
├── senior-engineer/                    # SeniorEngineerAgent prompts (Phase 3)
│   ├── system-message.md               # Senior Engineer persona
│   ├── implementation.md               # Code generation instructions
│   ├── self-review.md                  # Pre-submit review criteria
│   └── rework.md                       # Review feedback handling
│
├── junior-engineer/                    # JuniorEngineerAgent prompts (Phase 3)
│   ├── system-message.md               # Junior Engineer persona (with mentoring)
│   ├── implementation.md               # Code generation with extra guidance
│   ├── self-review.md                  # Review with common mistakes checklist
│   └── rework.md                       # Feedback handling with learning goals
│
└── test-engineer/                      # TestEngineerAgent prompts (Phase 4)
    ├── system-message.md               # Test Engineer persona
    ├── test-strategy.md                # Test strategy document generation
    ├── test-generation.md              # Test code generation
    ├── testability-assessment.md       # Code testability review
    ├── source-bug-classification.md    # Bug categorization
    ├── rework-instructions.md          # Test failure remediation
    ├── coverage-analysis.md            # Coverage gap identification
    └── ui-test-patterns.md             # UI/E2E test generation
```

### 9.2 File Count Summary

| Directory | Files | Phase |
|-----------|-------|-------|
| `shared/` | 7 | 1-3 |
| `researcher/` | 3 | 1 |
| `pm/` | 6 | 2 |
| `architect/` | 5 | 4 |
| `principal-engineer/` | 6 | 4 |
| `senior-engineer/` | 4 | 3 |
| `junior-engineer/` | 4 | 3 |
| `test-engineer/` | 8 | 4 |
| **Total** | **43 + README** | — |

### 9.3 Configuration

The template system is configured in `appsettings.json`:

```json
{
  "AgentSquad": {
    "Prompts": {
      "BasePath": "prompts",
      "HotReload": false,
      "MaxIncludeDepth": 10,
      "Overrides": {},
      "ABTest": {}
    }
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `BasePath` | string | `"prompts"` | Root directory for template files |
| `HotReload` | bool | `false` | Enable FileSystemWatcher for dev-time reload |
| `MaxIncludeDepth` | int | `10` | Maximum nested include depth |
| `Overrides` | dict | `{}` | Per-template version overrides for A/B testing |
| `ABTest` | dict | `{}` | A/B test configuration with variants and distribution |

---

## 10. Benefits Summary

### 10.1 Faster Prompt Iteration

**Before**: Changing a single word in a prompt requires:
1. Open C# file → find the prompt string → edit → save
2. `dotnet build` (~10-30s)
3. Restart the application
4. Wait for agent to reach the prompt in its workflow

**After** (with hot-reload): Edit the `.md` file → save → change is live immediately.

Even without hot-reload, the edit-restart cycle is simpler: no build step, and the change is
a clean diff in a Markdown file rather than buried in C# string interpolation.

### 10.2 Prompt Engineering Accessibility

Prompt templates are Markdown files — the most universal documentation format. Anyone who can
write English can improve prompts:
- Product managers refining spec generation instructions
- Domain experts adding specialized knowledge
- QA engineers improving test strategy templates
- Tech leads adjusting code review rubrics

No C#, .NET, or IDE knowledge required.

### 10.3 A/B Testing

With prompt versioning, teams can:
- Create `code-review-v2.md` with different scoring weights
- Configure 50/50 traffic split in `appsettings.json`
- Compare review quality metrics between versions
- Promote the winner as the default

This enables data-driven prompt optimization without code deployments.

### 10.4 Per-Project Customization

Different projects have different needs:
- A security-focused project needs heavier weight on security review criteria
- A data pipeline project needs different architecture patterns
- A greenfield project vs. a legacy migration need different implementation guidance

The template system supports per-project prompt overrides via the `Overrides` configuration,
allowing teams to customize agent behavior without forking the codebase.

### 10.5 Cleaner Version Control

Prompt changes produce focused diffs:

```diff
--- a/prompts/principal-engineer/code-review.md
+++ b/prompts/principal-engineer/code-review.md
@@ -15,7 +15,7 @@
 ### 2. Security (Weight: 25%)
-- Are inputs validated and sanitized?
+- Are inputs validated, sanitized, and parameterized?
+- Are SQL queries using parameterized statements?
 - Are there potential injection vulnerabilities?
```

Compare this to the equivalent change buried in a C# file with surrounding orchestration code.

### 10.6 Independent Testability

The template rendering pipeline can be tested independently:
- Does the template render correctly with given variables?
- Do fragment includes resolve properly?
- Is the frontmatter parsed correctly?

These tests are fast, deterministic, and don't require AI model calls.

### 10.7 Reusability via Fragments

The 5+ shared fragments eliminate duplication:
- Code style guidelines: defined once, used by 4 agents
- PR description format: defined once, used by 2 agents
- Review output format: defined once, used by 2 agents

Changes to shared standards propagate automatically to all consuming agents.

### 10.8 Clear Separation of Concerns

The decision rule provides an unambiguous boundary:

| Concern | Owner | Location |
|---------|-------|----------|
| What the AI should think/know/produce | Prompt Engineer | `.md` template |
| When/whether/who to call the AI | Developer | C# code |
| Agent lifecycle, message routing, state | Developer | C# code |
| Model selection, retry logic, rate limiting | Developer | C# code |

This separation makes both prompt engineering and code maintenance easier by reducing
cognitive load.

---

## 11. Risks and Mitigations

### 11.1 Template Bugs (Missing Variables)

**Risk**: A template references `{{pr_number}}` but the calling code passes `pr_num` instead.
The prompt is sent to the AI with a literal `{{pr_number}}` in it, producing unexpected output.

**Severity**: Medium — the AI may still produce reasonable output, but quality degrades.

**Mitigations**:
- **Declared variables in frontmatter**: Templates list expected variables in `variables:` field;
  the service can validate that all declared variables are provided
- **Warning logs**: Undefined variables produce a structured log warning that's easy to alert on
- **Integration tests**: Test each template with its expected variable set
- **Fallback to hardcoded**: During migration, hardcoded fallback ensures no disruption

### 11.2 Performance Overhead of File I/O

**Risk**: Reading template files from disk for every prompt rendering adds latency.

**Severity**: Low — template files are small (< 10KB each), and the overhead is negligible
compared to the 1-30 second AI model call that follows.

**Mitigations**:
- **In-memory caching**: Templates are cached after first load via `ConcurrentDictionary`
- **Lazy loading**: Templates are loaded on first use, not at startup
- **Benchmarking**: Measure template rendering time (expected: < 1ms cached, < 5ms uncached)
  vs. AI call time (expected: 1,000-30,000ms)

### 11.3 Drift Between Templates and Code

**Risk**: A template is updated to expect a new variable, but the C# code isn't updated to
provide it (or vice versa).

**Severity**: Medium — can cause template bugs (see 11.1).

**Mitigations**:
- **Integration tests**: Tests validate the contract between C# code and templates
- **Declared variables**: Frontmatter `variables:` field serves as a contract
- **Code review**: PRs that modify templates should also update corresponding C# code
  (and vice versa)
- **CI validation**: A build step could verify that all declared variables are provided
  in the corresponding agent code

### 11.4 Over-Extraction (Logic in Templates)

**Risk**: Templates grow to include conditional logic, loops, or other programming constructs,
blurring the line between prompt content and orchestration logic.

**Severity**: Medium — can make templates unmaintainable and hard to test.

**Mitigations**:
- **Decision rule enforcement**: The "what vs. when/whether/who" rule is enforced in code reviews
- **No Turing-complete template language**: The template system only supports variable
  substitution and fragment includes — no `if`, `for`, or expressions
- **Code review checklist**: "Does this template contain orchestration logic?" as a standard
  review question

### 11.5 Security Considerations

**Risk**: Template files could be modified maliciously to inject harmful content into AI prompts.

**Severity**: Low — templates are version-controlled and subject to the same access controls
as source code.

**Mitigations**:
- Templates are checked into Git and reviewed via PRs (same as code)
- File system permissions on the `prompts/` directory
- Templates don't execute code — they're pure text with variable substitution
- No user-supplied input flows directly into template *paths* (preventing path traversal)

### 11.6 Fragment Complexity

**Risk**: Deeply nested fragment includes create hard-to-trace prompt composition.

**Severity**: Low-Medium — can make debugging difficult ("where did this text come from?").

**Mitigations**:
- **Max depth limit**: 10 levels (configurable), far more than needed
- **Circular include detection**: Throws immediately on circular references
- **Render logging**: Debug-level log shows full include resolution chain
- **Convention**: Fragments should be 1-2 levels deep; deeper nesting indicates a design problem
- **README guidance**: `prompts/README.md` documents best practices for fragment organization

---

## 12. Relationship to Other Plans

### 12.1 SMEAgentsPlan.md

The [SME Agents Plan](SMEAgentsPlan.md) introduces Subject Matter Expert agents that are
dynamically spawned based on project needs. These agents would **natively use prompt templates**
from day one:

- SME agents already use `RoleContextProvider` for persona definition
- Their task-specific prompts would be template files in `prompts/sme/{specialty}/`
- New SME specialties can be added by creating template files — no C# changes needed
- The template system's fragment includes allow SME agents to share common patterns
  (e.g., `{{> shared/sme-communication-protocol}}`)

**Implication**: Completing prompt externalization before SME agent implementation means SME
agents get the template system for free.

### 12.2 PEParallelismEnhancements.md

The [PE Parallelism Enhancements](PEParallelismEnhancements.md) plan introduces a PE fleet
with multiple Principal Engineers working in parallel. With prompt externalization:

- PE fleet prompts are templates from the start — no hardcoded strings to migrate
- Leader election prompts (`prompts/principal-engineer/leader-election.md`) are shared across
  all PE instances
- Individual PEs can have customized review emphasis via template variable overrides
- A/B testing of review approaches across PE fleet members

### 12.3 .agent.md Files (Copilot CLI)

The `.agent.md` files used by the Copilot CLI for agent definitions are **separate from**
prompt templates:

| Aspect | `.agent.md` | Prompt Templates |
|--------|-------------|-----------------|
| Purpose | Define agent persona for Copilot CLI | Define task instructions for AgentSquad |
| Audience | Copilot CLI runtime | `PromptTemplateService` |
| Location | Repository root | `prompts/` directory |
| Format | Markdown with specific headers | Markdown with YAML frontmatter |

Both can coexist without conflict. If an agent has both a `.agent.md` (Copilot CLI persona)
and prompt templates (AgentSquad task instructions), they serve different systems.

### 12.4 Future Directions

The prompt externalization system opens up possibilities that extend beyond this plan:

- **Prompt marketplace**: Share proven prompt templates across AgentSquad deployments
- **Automated prompt optimization**: Use evaluation results to auto-tune prompt wording
- **Multi-language support**: Localize agent prompts for international teams
- **Prompt analytics**: Track which prompt versions produce the best AI output quality
- **Dynamic prompt composition**: Runtime prompt assembly based on project characteristics
  (detected tech stack, codebase size, team size)

These are aspirational and not committed work items.

---

## Appendix A: Template Syntax Quick Reference

| Syntax | Purpose | Example |
|--------|---------|---------|
| `---` ... `---` | YAML frontmatter | `--- model_tier: premium ---` |
| `{{variable}}` | Variable substitution | `{{pr_number}}` |
| `{{> path}}` | Fragment include | `{{> shared/guidelines}}` |
| `{{ variable }}` | Variable with spaces (trimmed) | `{{ pr_number }}` |

## Appendix B: Migration Checklist Template

For each agent migration, complete this checklist:

- [ ] Identify all hardcoded prompt strings in the agent file
- [ ] Create template files in `prompts/{role}/` for each prompt
- [ ] Copy prompt text verbatim, replacing interpolations with `{{variables}}`
- [ ] Add YAML frontmatter with metadata
- [ ] Extract shared content to fragment files in `prompts/shared/`
- [ ] Update agent constructor to accept `IPromptTemplateService`
- [ ] Replace hardcoded strings with `_templateService.RenderAsync()` calls
- [ ] Add hardcoded fallback for each template (Phase 1-4 only)
- [ ] Write unit tests for template rendering
- [ ] Write integration test for agent pipeline
- [ ] Run behavioral comparison (side-by-side output review)
- [ ] Remove hardcoded fallback (Phase 5)
- [ ] Update documentation

## Appendix C: Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2025-07-14 | Use `{{variable}}` syntax (Mustache-like) | Widely recognized, simple, no ambiguity with C# or Markdown |
| 2025-07-14 | YAML frontmatter for metadata | Standard in static site generators, well-tooled, human-readable |
| 2025-07-14 | No conditional logic in templates | Prevents templates from becoming Turing-complete; keeps separation clean |
| 2025-07-14 | Fragment includes with depth limit | Enables reuse without risk of infinite recursion |
| 2025-07-14 | Phase 1 starts with Researcher | Lowest risk (3 simple prompts), validates infrastructure before complex agents |
| 2025-07-14 | Fallback to hardcoded during migration | Zero-downtime migration; templates can fail without breaking agents |
| 2025-07-14 | ConcurrentDictionary for cache | Thread-safe without locks; matches existing codebase patterns |
| 2025-07-14 | Hot-reload is opt-in (Phase 5) | Not needed in production; avoids FileSystemWatcher overhead |
