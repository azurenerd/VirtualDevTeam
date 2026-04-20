# AgentSquad Gap Analysis — Why a Single CLI Session Beats a Multi-Agent Pipeline

**Date:** 2026-04-20
**Subject:** Root-cause analysis and remediation plan for the quality gap between a one-shot Copilot CLI build and an AgentSquad run on the identical project brief (ReportingDashboard executive dashboard).

## Implementation Status (2026-04-20)

- ✅ **R1 — PNG design references.** Implemented. `IGitHubService.GetFileBytesAsync` added; `EngineerAgentBase.GetDesignContextAsync` now discovers PNG/JPG/WEBP under `docs/design-screenshots/` AND anywhere in the tree matching design/concept/mockup/wireframe/prototype/reference/screenshot keywords. Image bytes are cached and injected as `ImageContent` via the new `AddUserMessageWithDesignImages` helper at the plan-generation, single-pass implementation, and step-implementation call sites. `SoftwareEngineerAgent.ReadDesignReferencesAsync` now delegates to the base method (HTML + PNG unified).
- ✅ **R3 — Scope rule relaxed + integration review strengthened.** `prompts/software-engineer/single-pass-implementation.md` now explicitly permits integration edits (DI registration, `_Imports.razor` usings, route mapping, required middleware) with an `INTEGRATION EDIT:` justification marker and minimum-footprint rule. `prompts/software-engineer/integration-review-system.md` replaced with a 9-point checklist covering DI, middleware, routing, imports, static assets, data file copy-to-output, composition, error paths, and build/run verification. T-FINAL plumbing in `SoftwareEngineerAgent` (the `IntegrationTaskId = "T-FINAL"` task auto-assigned after all other tasks complete) is unchanged — the prompt it runs is just more rigorous now.
- ✅ **R2 — SinglePRMode.** `LimitsConfig.SinglePRMode` (bool, default false) added. When true, `SoftwareEngineerAgent.CreateEngineeringPlanAsync` bypasses AI task decomposition, self-assessment, foundation-first enforcement, and parallel-file validation; instead it creates ONE `T1` task covering every enhancement issue, plus the standard T-FINAL integration task. Flip the flag in `appsettings.json` under `AgentSquad.Limits.SinglePRMode: true` to opt in.

Verified: `dotnet build src/AgentSquad.Agents` clean (0 errors, 0 new warnings); Core tests 385/385; Agents tests 81/81. Full-solution build is blocked only by file locks from the concurrently running Dashboard process — not a code issue.

---


## TL;DR

In 8 minutes and one Copilot CLI session, a single agent produced Research.md, PMSpec.md, Architecture.md, a complete Blazor Server dashboard (`C:\BenCLITest\`), and had it running in a browser at ~95% of the target visual design.

In 8+ hours across three retries, AgentSquad (on the same brief) produced dozens of PRs and never assembled a working, visually-correct product.

This is not an AI capability gap. The same model (`claude-opus-4.7`) powers both. The gap is a **system design** gap — specifically, AgentSquad has four structural features that each, on their own, significantly degrade output quality, and that **compound** when combined:

1. **Mandatory task fragmentation** (1 PR = 1 narrow slice), hard-coded in the planning prompts.
2. **Aggressive anti-integration scope rules** that explicitly forbid any engineer from touching files outside their slice, *even when the slice is incorrect*.
3. **Design-reference blindness** — the actual pixel-perfect design (PNG) is silently dropped; agents only ever see the lower-fidelity HTML concept (and even that truncated at 8K chars).
4. **Context serialization loss** across phase boundaries — decisions and tradeoffs made by the PM or Architect are flattened to markdown and re-parsed, losing everything the single-CLI agent held in memory.

The single-CLI run has none of these properties, which is why it works.

## 1. What actually happened in the single CLI run (8 minutes, 1 session)

| Phase | What the agent did | Why it worked |
|---|---|---|
| Read inputs | Opened `appsettings.json`, read project description + tech stack, viewed `OriginalDesignConcept.html` AND the PNG via the native `view` tool (real image input to vision model) | The image is actually rendered as an image to the LLM, not as base64 text |
| Research.md | Synthesized problem framing, color grammar, data model, rendering approach, edge cases | All context held in one conversation; no handoff loss |
| PMSpec.md | 7 user stories with acceptance criteria, scope/NFR/metrics | Continuity with Research: no re-derivation |
| Architecture.md | Full file layout, DI plan, record types, validator rules, component API | Continuity with PMSpec: one mind owns the vertical slice |
| Code | ~15 source files, one csproj, sample data, CSS, all components | Written as one integrated whole, not 13 separate PRs |
| Build iteration 1 | Razor `<text>` tag conflict → rewrote `TimelineSvg` to emit SVG via `MarkupString` | Same context; fix in seconds |
| Build iteration 2 | Missing `app.UseAntiforgery()` → added to `Program.cs` | Same context; the agent *could* modify `Program.cs` because nothing forbade it |
| Run | `dotnet run --urls http://localhost:5200`, opened browser | Done |

Tool budget: ~35 calls. LLM model: `claude-opus-4.7`. Exactly what AgentSquad uses as the `premium` tier.

## 2. What AgentSquad actually does to the same brief

### 2.1 Mandatory fragmentation (smoking gun #1)

From `prompts/software-engineer/plan-generation-system.md`:

> CRITICAL — Foundation Task (MUST be Task T1)
> The FIRST task (T1) MUST ALWAYS be a 'Project Foundation & Scaffolding' task that:
> - Creates a proper .gitignore …
> - Sets up the solution/project structure, build configuration, and shared infrastructure
> - Creates the core data models, interfaces, and abstractions …
> - Has NO dependencies (all other tasks should depend on T1)

From `prompts/engineer-base/step-planning-system.md`:

> Break the task into 3-6 discrete, ordered implementation steps. IMPORTANT rules:
> - Step 1 MUST be project scaffolding: folder structure, config files, boilerplate, package manifests, and empty placeholder files that establish the project skeleton.

And from `prompts/software-engineer/plan-generation-system.md`:

> CRITICAL — Parallel-Friendly Task Decomposition
> Multiple engineers will work on tasks IN PARALLEL. Design tasks to MINIMIZE overlap and merge conflicts:
> - **Separate by component/module boundary**: each task should own a distinct set of files. Two tasks should NEVER create or modify the same file.
> - **Explicit file ownership**: every task's FilePlan must list EXACTLY which files it creates or modifies.
> - **Shared infrastructure in T1**: anything that multiple tasks would need (base classes, interfaces, config models, shared DTOs) should go in T1 so parallel tasks only CONSUME these, never create them.

**Result — observed in `azurenerd/ReportingDashboard`:**

| PR # | Title |
|---|---|
| 2070 | Project Foundation & Scaffolding (T1) |
| 2071 | Implement DashboardDataService Hot-Reload |
| 2072 | Flesh Out POCO Data Models & JSON Deserialization |
| 2073 | Implement Timeline Coordinate Math Helpers |
| 2074 | Port Base CSS and Global 1920x1080 Layout |
| 2075 | Build Dashboard Header Component |
| 2076 | Build Timeline SVG Component |
| 2077 | Build Monthly Execution Heatmap Component |
| 2078 | Author Sample data.json and README |
| 2079 | Build Error Banner and Wire Dashboard Composition |

That is **ten PRs** to produce what one CLI session produced in **one** commit. Each PR was generated by a separate LLM invocation with its own context window. The context assembled for PR #2077 (Heatmap) does not know what PR #2076 (Timeline) chose for coordinate math. They are independent monologues stapled together via the main branch.

PR counts 2096, 2112, 2135 show this foundation-scaffolding step being retried over and over across runs — each retry starts the 10-PR fragmentation cascade from zero.

### 2.2 Anti-integration scope rules (smoking gun #2)

From `prompts/software-engineer/single-pass-implementation.md` (the prompt every engineering PR runs through):

> ## SCOPE RULE (read this before anything else) — CRITICAL
> - Produce ONLY the files required by THIS task's acceptance criteria.
> - Do NOT implement features from other tasks even if the PM spec / architecture describe them.
> - If the task is 'scaffolding' or 'project foundation': emit project manifests, directory structure, placeholder entry-points, and .gitignore ONLY. Do NOT implement pages, components, services, or models — those belong to their own tasks.
> - **Do NOT regenerate files that already exist on the branch (.sln, .csproj, Program.cs, existing components, CSS files, data files) unless the task EXPLICITLY requires changes to them.** Regenerating existing infrastructure files causes merge conflicts with other PRs and is the #1 reason for review rejection.
> - When in doubt, produce FEWER files rather than more. A downstream task will fill the gap.

This is the poison pill. When PR #2071 (DashboardDataService) inevitably needs a DI registration in `Program.cs`, and `Program.cs` was created by PR #2070 without that registration, **the engineer is explicitly forbidden from touching `Program.cs`**. The gap silently propagates. No later task will fix it either, because every later task sees the same rule: "do not regenerate files that already exist."

In my CLI run, when the first build told me `app.UseAntiforgery()` was missing, I edited `Program.cs` in the same session and moved on. That is illegal under AgentSquad's prompt rules.

**This is the most direct cause of "it never finishes."** Integration issues require cross-file edits, and every PR is prompt-prohibited from making cross-file edits.

### 2.3 Design-reference blindness (smoking gun #3)

The project description in `appsettings.json` explicitly names **two** design references:

> Take the OriginalDesignConcept.html design template file from the ReportingDashboard repo, as well as the C:/Pics/ReportingDashboardDesign.png design of what I want the dashboard to look like…

In `src/AgentSquad.Agents/SoftwareEngineerAgent.cs`, `ReadDesignReferencesAsync()`:

```csharp
var designFiles = tree
    .Where(f =>
    {
        var ext = Path.GetExtension(f).ToLowerInvariant();
        if (ext != ".html" && ext != ".htm") return false;  // ← PNG is silently dropped
        var name = Path.GetFileName(f).ToLowerInvariant();
        return name.Contains("design") || name.Contains("concept") ||
               name.Contains("mockup") || name.Contains("wireframe") ||
               name.Contains("prototype") || name.Contains("reference");
    })
    .ToList();
// ...
sb.AppendLine(content.Length > 8000 ? content[..8000] + "\n<!-- truncated -->" : content);
```

Two defects here:
1. **`.png` (and `.jpg`, `.svg`, etc.) is filtered out.** The PNG never reaches any agent. This PNG is what shows the summary counters strip, the 4th lane (M4), the 6-month timeline, and the item-link styling — none of which the HTML concept conveys. My CLI run used the PNG via the native `view` tool and absorbed all of that detail. AgentSquad agents are effectively building from a **smaller, outdated design doc.**
2. **HTML is truncated at 8000 chars.** `OriginalDesignConcept.html` is ~10K chars; the last ~2K bytes (which include the Blockers row of the heatmap) get cut off.
3. **Only files in the target repo's git tree are scanned.** The PNG at `C:\Pics\ReportingDashboardDesign.png` isn't in any repo; even if PNGs were allowed, this path wouldn't be followed.

Even where AgentSquad *does* support embedding images — `CopilotCliChatCompletionService.AppendMessageContent` has a real `ImageContent` path that emits `data:image/png;base64,…` — that path is only used in two places (PM PR review, Test Engineer screenshot checks). **It is never used for passing the design reference to the PM/Architect/SE during planning or implementation.**

### 2.4 Context serialization across phase boundaries (smoking gun #4)

The pipeline is: Researcher → PM → Architect → SE-plan → SE-per-task × N → Test Engineer.

Between each phase, state is serialized to a markdown file (Research.md, PMSpec.md, Architecture.md, EngineeringPlan.md) and re-loaded into the next agent's fresh context. This is by design — it matches how humans collaborate via docs — but it loses:

- **Unstated assumptions**: color grammar I derived in Research just by looking at the HTML; an exact CSS token mapping never made it verbatim into the hand-off.
- **Rejected options**: the Researcher considered and discarded three options; only the chosen one ships. The Architect re-derives the same decision, sometimes differently.
- **Cross-document alignment**: my CLI run aligned the PMSpec's "US-3 (see current point in time clearly)" with the Architecture's `TimelineLayoutEngine.NowX` because I owned both docs simultaneously. In AgentSquad, the PM and Architect are separate agents, separate runs, separate context windows — they *reinvent* the same terms with different names.

The PM prompt even requires this re-derivation:
> You MUST include a '## Visual Design Specification' section in PMSpec describing every visual element

So the PM reads the HTML (not the PNG), paraphrases it into prose, and downstream SEs work from *paraphrased prose of a truncated HTML file that omits the real design*. The information has been degraded at four stages by the time it reaches the engineer who writes the CSS.

### 2.5 Layered on top: AgenticLoop, rework cycles, and decision gating

From `appsettings.json`:
```
"MaxReworkCycles": 3,
"MaxArchitectReworkCycles": 2,
"MaxPmReworkCycles": 3,
"MaxTestReworkCycles": 2,
"MaxClarificationRoundTrips": 5,
"AgenticLoop": { "Enabled": true, "MaxIterations": 2, "Roles": { … } }
```

Each rework cycle re-invokes the agent with *new* critique context on a *fresh* context window. This often makes outputs *worse*: the agent doesn't see what it previously decided, so it second-guesses coherent choices. It's a form of drift. My single-CLI run has zero rework cycles because the first output was correct — one mind, one draft, one coherent product.

## 3. Why the Interactive CLI mode (Method 3) in AgentSquad still underperforms

Interactive mode just keeps the human in the loop. It doesn't remove the four structural defects above. You get the same fragmented PRs, the same scope-lock rules, the same PNG blindness, the same cross-phase context loss — just with you clicking "approve" at each gate. The model is still producing narrow slices under narrow prompts; the human-in-the-loop step is not where the quality is lost.

## 4. Answering your direct questions

> Is the problem that it's splitting up things too much into smaller PRs?

**Yes, and that is the biggest single factor.** It's not that small PRs are bad in general — in a human team, they're great. But AgentSquad pairs small PRs with *explicit prohibitions against fixing anything outside the PR's slice*. That combination is lethal. In human teams, a developer working on feature B *does* go back and fix a bug in feature A when they discover it. Here, the prompt forbids it.

> Are you doing something in this CLI that is more robust and different than Method 3?

**Yes, four concrete things:**

1. **I see the PNG as a real image** (via the vision-capable view tool) — AgentSquad's `ReadDesignReferencesAsync` filters PNGs out entirely.
2. **I own every file in the project across the whole session.** AgentSquad splits ownership by task, then forbids cross-task edits.
3. **I iterate in-place on build failures.** AgentSquad requires a new PR (and therefore a fresh LLM invocation with fresh context) to fix anything — which can't edit files from prior PRs.
4. **I carry every unstated design decision in working memory through research → spec → architecture → code.** AgentSquad forcibly serializes state to markdown between phases, losing everything that wasn't explicitly written down.

## 5. Remediation Plan — bringing AgentSquad up to single-CLI quality

This is organized by impact × effort. Start from the top; each successive change increases invasiveness.

### R1 — Stop filtering out image design references (30 minutes, very high impact)

**Change:** In `SoftwareEngineerAgent.ReadDesignReferencesAsync` (and the equivalent helpers in `ResearcherAgent`, `ProgramManagerAgent`, `ArchitectAgent`):
1. Accept `.png`, `.jpg`, `.jpeg`, `.webp`, `.svg` in addition to `.html`/`.htm`.
2. For image file types, use `GitHub.GetFileBytesAsync` (or add one) and wrap as `ImageContent` on the `ChatMessageContent.Items` collection so `CopilotCliChatCompletionService.AppendMessageContent` takes the existing base64-embedding path.
3. Also accept local absolute paths mentioned in the `Project.Description` — regex out `C:\...\.png` / `file://` references and load them via `File.ReadAllBytes` when they exist.
4. Raise the HTML truncation limit from 8000 to 32000 (or remove when the file is the only design ref).

Expected effect: every downstream agent finally sees the real target design. Output fidelity should improve immediately.

### R2 — Add a "Single-PR Mode" to the planning prompt (1 hour, very high impact)

**Change:** Add a new configuration flag `AgentSquad:Agents:SinglePRMode` (default true for small projects). When enabled:
1. `plan-generation-system.md` emits exactly ONE task (T1) whose `FilePlan` lists every file in the architecture doc.
2. `single-pass-implementation.md` has its SCOPE RULE replaced with an INTEGRATION RULE: "You are producing the entire project in one commit. Output every file needed to run `dotnet build && dotnet run` and render the page matching the design."
3. Remove the "Do NOT regenerate files that already exist" clause in this mode.

Expected effect: the 10-PR cascade collapses to 1 PR. Build-iteration loops run in-place on the same branch, not as a new PR each time.

**Heuristic for when to use this mode:** projects with ≤ ~25 files planned, a single csproj, or where the PM/Architect docs are < 20KB. Larger projects can still use the parallel mode.

### R3 — Remove "do not touch other files" blockers from existing multi-PR mode (1 hour, high impact)

Even when you keep multi-PR mode, relax the integration prohibitions:
- In `single-pass-implementation.md`, replace "Do NOT regenerate files that already exist… unless the task EXPLICITLY requires changes to them" with: "You MAY modify any file that is necessary for the project to build and run. When modifying an existing file, preserve unrelated content. Your PR MUST leave `dotnet build` green."
- In `plan-generation-system.md`, add: "The LAST task in the plan MUST be a 'Final Integration' task whose job is to run the app, fix any build or runtime failures discovered, and touch any files needed across the project."

Expected effect: gaps between PRs close instead of propagating. Fewer "it built but doesn't work" outcomes.

### R4 — Compose the PM/Architect in the SAME agent run (2-4 hours, high impact)

Right now PM and Architect are separate `AgentBase` implementations with separate context windows. Combine them into a single agent call that produces both PMSpec and Architecture together, then hands them to a single SE run. This collapses two cross-phase serialization boundaries (PM→Architect and Architect→SE-plan) into one and eliminates the "reinvent terms with different names" drift I described in §2.4.

Implementation sketch:
- New prompt `prompts/pm-architect/combined-system.md` that asks for both docs in one response, delimited by `=== PMSpec ===` and `=== Architecture ===`.
- New agent `PmArchitectAgent` or simply have `ProgramManagerAgent` produce both when `AgentSquad:Agents:CombinePmArchitect` is true.
- Keep the markdown artifacts for human review — they're good docs for humans — but the LLM never *round-trips* through them.

### R5 — Run build/run AS PART OF the same LLM session (4-8 hours, medium impact)

Today, the SE agent emits files, commits them, and a separate workflow runs the build. When the build fails, a fresh LLM call is made with the build log as new context. The agent doesn't remember why it wrote what it wrote.

Change: after the SE emits files, the same agent-loop invocation runs `dotnet build` locally, pipes the result back into the chat history of the SAME agent turn, and iterates up to N times before committing. This is what I did manually in my CLI run (and what the Copilot CLI does natively). Build → fix → build → fix, all in one conversation.

This requires either:
- Running build commands as tool calls inside the Copilot CLI turn (requires `--allow-tool shell` or equivalent), OR
- Running an "inner loop" in the .NET host that re-invokes `CopilotCliChatCompletionService` with the running transcript after each build.

The second is less invasive.

### R6 — Kill or gate the rework cycles (1 hour, medium impact)

`MaxReworkCycles=3` + `MaxPmReworkCycles=3` + `MaxTestReworkCycles=2` is an estimated 18-27 extra agent runs per project, each on a fresh context. Most of them make outputs worse.

Change:
- Default `MaxReworkCycles=1`.
- When rework is triggered, feed the agent its PREVIOUS output + the critique, not just the critique. Use `ChatHistory` continuation, not a new turn from scratch.
- Add a "rework-no-op" detector: if the new output is ≥ 90% identical to the previous, stop and commit the previous.

### R7 — Diagnostics: prove the fix worked (2 hours)

Add a golden-case test that runs on every PR of AgentSquad itself: given `appsettings.json` with the ReportingDashboard brief, can AgentSquad in `SinglePRMode` produce a project that:
1. Builds clean.
2. Serves an HTTP 200 page that contains the words "Project Helios" (or whatever the sample name is), "SHIPPED", "IN PROGRESS", and an SVG `<polygon>` (the milestone diamonds).

If yes, ship. If no, we know we regressed.

## 6. Recommended order of operations

1. **This week, in this order:** R1 (images) → R3 (relax scope rule) → R2 (single-PR mode flag). These three alone should get AgentSquad to my CLI's output quality on projects of this size.
2. **Next week:** R4 (combine PM+Architect), R6 (kill most rework cycles).
3. **Later:** R5 (in-session build loop), R7 (golden test).

## 7. Risks / what could still fail

- **R2 single-PR mode** may hit copilot CLI context limits on very large projects (> ~30 files). The heuristic in R2 should gate it. If exceeded, fall back to multi-PR mode with R3 applied.
- **R1 image handling** assumes the underlying `copilot` CLI model actually consumes `data:image/png;base64,...` blobs as images. Verify with a quick smoke test: pipe a prompt that includes the base64 and ask the CLI "describe what you see" — if it answers textually about the image content, R1 is real; if it just echoes "I see a base64 blob", we need the `copilot` CLI team to support image input on stdin, or we attach images via temp file + `--file` flag (if/when that exists).
- **R4** risks producing a single giant PMSpec+Architecture response that exceeds the model's coherent-output budget. Mitigation: keep each doc targeted (< 8KB), use explicit section markers, cap the combined response.

## 8. Cost comparison (observed)

| Metric | Single CLI session | One AgentSquad run (ballpark) |
|---|---|---|
| LLM calls | ~35 turns | ~200+ (PM ×3 rework, Arch ×2, SE-plan ×1, SE × 10 tasks × possible rework, Test × etc.) |
| Wall clock | 8 minutes | 8+ hours |
| PRs created | 0 (local folder) | 10-30 |
| Finished, working artifact | Yes | Not observed |
| Input context per call | Full (whole project) | Sliced (per-task) |
| Vision model usage | Yes (PNG viewed directly) | No (PNG filtered out) |

The AgentSquad run is more expensive AND produces a worse result. The fix isn't "try harder with more agents" — it's "reduce coordination overhead until each LLM call owns enough scope to be internally coherent."

## 9. Bottom line

Your instinct was right: AgentSquad is over-fragmenting and losing context at every boundary. The specific mechanisms are:

- Prompts that mandate 1-PR-per-slice.
- Prompts that forbid cross-file integration.
- A design-reference loader that throws away PNGs.
- Four serialization boundaries where decisions get paraphrased away.

Each of the first three has a fix under an hour of work. After those three, AgentSquad should be at rough parity with a single-CLI run on projects of this size and scope, and the pipeline-of-agents architecture can be reserved for genuinely large projects where parallelism pays off.

---
*Produced in the same single CLI session that generated the comparison dashboard at `C:\BenCLITest\`.*
