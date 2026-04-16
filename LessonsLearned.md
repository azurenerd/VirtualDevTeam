# Lessons Learned: Building a Multi-Agent AI Development System

> **Author:** Ben Humphrey (azurenerd) with Copilot CLI  
> **Project:** AgentSquad — a .NET 8 multi-agent system where 7 AI agents (PM, Researcher, Architect, Software Engineer, Software Engineers, Test Engineer) collaborate through GitHub PRs/Issues to build software.  
> **Purpose:** This document captures hard-won lessons from ~85+ iterative build-run-fix cycles over multiple sessions. It's intended for engineering teams considering AI agent-based development pipelines, to help them avoid the same pitfalls and build better agent orchestration from day one.

---

## Table of Contents

1. [The Plan Is Never Enough](#1-the-plan-is-never-enough)
2. [Agent Context Is Everything](#2-agent-context-is-everything)
3. [Visibility and Observability](#3-visibility-and-observability)
4. [Idempotency and Crash Recovery](#4-idempotency-and-crash-recovery)
5. [PR and Review Workflow Pitfalls](#5-pr-and-review-workflow-pitfalls)
6. [AI Output Quality Control](#6-ai-output-quality-control)
7. [Parallel Work and Merge Conflicts](#7-parallel-work-and-merge-conflicts)
8. [Testing Agent Challenges](#8-testing-agent-challenges)
9. [Model Selection and Cost Management](#9-model-selection-and-cost-management)
10. [Design and UI Quality](#10-design-and-ui-quality)
11. [Requirements and Scenario Documentation](#11-requirements-and-scenario-documentation)
12. [Recommendations for Agent-Based Development](#12-recommendations-for-agent-based-development)
13. [Dashboard Architecture and Process Separation](#13-dashboard-architecture-and-process-separation)
14. [GitHub API Rate Limiting and Caching](#14-github-api-rate-limiting-and-caching)
15. [Vision-Based Screenshot Review](#15-vision-based-screenshot-review)
16. [Human Gate Configuration Must Be Enforced on ALL Code Paths](#16-human-gate-configuration-must-be-enforced-on-all-code-paths)
17. [Port Conflicts When Multiple Agents Run Apps Simultaneously](#17-port-conflicts-when-multiple-agents-run-apps-simultaneously)
18. [Standalone Dashboard Data Hydration from SQLite](#18-standalone-dashboard-data-hydration-from-sqlite)
19. [GitHub API Pagination Is Mandatory During Reset](#19-github-api-pagination-is-mandatory-during-reset)
20. [Hardcoded Port Bindings Break Automated UI Testing](#20-hardcoded-port-bindings-break-automated-ui-testing)
21. [Blazor Server SynchronizationContext Kills HTTP Calls](#21-blazor-server-synchronizationcontext-kills-http-calls)
22. [Transient Status Flash from Pre-Gate Status Updates](#22-transient-status-flash-from-pre-gate-status-updates)
23. [AI Agents Rewrite Components from Scratch During Incremental PRs](#23-ai-agents-rewrite-components-from-scratch-during-incremental-prs)
24. [SE Parallelism Enhancements](#24-se-parallelism-enhancements)
25. [Decision Impact Classification & Gating](#25-decision-impact-classification--gating)
26. [Agent Task Steps — Real-Time Workflow Visibility](#26-agent-task-steps--real-time-workflow-visibility)

---

## 1. The Plan Is Never Enough

**Lesson:** Even with a comprehensive architecture document, detailed PM specification, and engineering plan, the agent system required constant human guidance to course-correct behaviors that were never anticipated in the original design.

### What happened:
- The initial plan covered agent roles, message bus communication, GitHub integration, and a phase-gated workflow. It seemed comprehensive.
- In practice, dozens of emergent behaviors surfaced only during live execution: agents acting out of order, duplicate work on restart, review loops that never terminated, agents posting meta-commentary instead of doing work.
- Each fix revealed 2-3 more issues that couldn't have been predicted from the plan alone.

### Examples of guidance that was needed but not in the original plan:
- "The PM agent doesn't create a PM Spec document" — the original plan had agents but didn't specify the document pipeline (Research.md → PMSpec.md → Architecture.md → Engineering tasks)
- "The SE agent created the plan but hasn't asked for any new developers and no new PRs have been created" — the spawning workflow for engineer agents wasn't detailed
- "Make sure the agents don't review the code until the engineering agents are ready" — review timing relative to PR readiness wasn't specified
- "After a review we need to add a message to send back to the author when there is feedback" — the rework loop wasn't in the original design

### Takeaway:
**Plan for the plan to be incomplete.** Budget significant time for iterative observation and correction. The first 5-10 end-to-end runs will primarily surface gaps in the workflow design, not validate it.

---

## 2. Agent Context Is Everything

**Lesson:** AI agents lose all context between invocations. Every piece of information they need must be explicitly provided in their prompt, or they will produce generic, misaligned output.

### What happened:
- Reviewers (PM, Architect, SE) were approving or rejecting PRs without reading the actual code files, the linked issue, the PMSpec, or the Architecture document. They were reviewing based solely on the PR title and description.
- Engineers were generating code without knowing what files already existed in the repository, leading to duplicate classes and conflicting namespaces.
- The Test Engineer was writing markdown test plans instead of actual runnable test code because it wasn't told the technology stack or given examples.
- The Architect was building Architecture.md without reading the PMSpec business goals.
- **No agent ever read the visual design reference file** (`OriginalDesignConcept.html`) that was sitting in the repository root, resulting in a UI that looked nothing like the intended design.

### Specific guidance that was needed:
- "Can you confirm if the architect reads the PMSpec.md or other details before writing the architecture.md file?" → It didn't.
- "When doing a review, does the architect agent read the architecture.md and PMSpec.md?" → No, and it wasn't reading the actual code either.
- "Make sure reviewers are reviewing the PR according to the description, acceptance criteria, and context understanding of the key PMSpec, Architecture Plan and Engineering Plan."
- "Each reviewer MUST look at the actual files checked in for that PR to ensure the code meets expectations."

### Takeaway:
**Enumerate every document each agent role needs to read, for every action it takes.** Create a context matrix:

| Agent | Action | Must Read |
|-------|--------|-----------|
| PM | Write PMSpec | Research.md, Project Description, Design Files |
| PM | Review PR | PMSpec.md, Linked Issue, PR Code Files |
| Architect | Write Architecture | PMSpec.md, Research.md, Design Files |
| Architect | Review PR | Architecture.md, PMSpec.md, PR Code Files |
| SE | Create Tasks | PMSpec.md, Architecture.md, Design Files, Repo Structure |
| Engineer | Implement | PMSpec.md, Architecture.md, Issue Details, Design Files, Repo Structure |
| TE | Write Tests | Merged PR Code, PMSpec.md, Architecture.md, Design Files |

If it's not in the prompt, it doesn't exist to the agent.

---

## 3. Visibility and Observability

**Lesson:** You cannot debug a multi-agent system without real-time visibility into what every agent is doing, has done, and is waiting for.

### What happened:
- Early runs showed all agents as "Idle" or "Online" with no way to tell what was happening internally.
- Status messages were truncated with "..." and no way to see the full text.
- Agents would appear stuck for 10-15 minutes with no indication of whether they were working, waiting, or errored.
- Dashboard timer displays would reset unpredictably, making it impossible to judge actual elapsed time.
- No error/warning indicators existed — failures were silent.

### Guidance that was needed:
- "Add a better status message so I can see progress in more real-time"
- "Make it so if I mouse over the status in the dashboard it shows a popup text of the full status"
- "Create another section in the overview cards for the agents to show errors or warnings"
- "I want to be able to see a history for each agent, what were their tasks they have completed or are on currently"
- "The status in the overview agent card in the dashboard are not updating... I have to refresh"

### Timeline visualization iterations:
- The Project Timeline page went through several iterations to become useful. Initial implementation showed a flat list with no way to understand parent-child relationships between enhancement issues, engineering tasks, and PRs.
- A PM/Engineering toggle was added so the PM could see the project from a business perspective (enhancements → tasks) while engineers could see it from a technical perspective (tasks → PRs).
- PRs and Issues needed visual distinction — colored badges ("PR #X" in purple, "Issue #X" in green) were added to both node labels and detail popups.
- Auto-refresh caused a critical race condition: the 30-second background refresh rebuilt `_phases`/`_groupLookup`, which invalidated `_selectedGroup` in the detail panel. This caused `NullReferenceException` crashes. Fix: re-fetch the selected group from the new lookup after every rebuild, with null-safe pattern matching.
- Background refresh also caused UX annoyance — the "Syncing work items" overlay flashed every 30 seconds. Fix: only show the overlay on first load or manual refresh; auto-refresh runs silently.
- Phase naming confusion: "Complete" phase was renamed to "Finalization" because closed engineering tasks were landing there instead of staying in Development with closed visual indicators.

### Takeaway:
**Build the monitoring dashboard BEFORE the agent pipeline.** Include:
- Real-time status updates (use SignalR/WebSocket, not polling)
- Full status text with hover/expand capability
- Per-agent activity log with timestamps
- Error/warning counters with drill-down
- Phase progression visualization
- GitHub artifact links (PRs, Issues) directly from the dashboard

---

## 4. Idempotency and Crash Recovery

**Lesson:** Multi-agent systems crash, restart, and resume constantly. Every operation must be idempotent, and every agent must recover gracefully from partial state.

### What happened:
- Restarting the service during development created duplicate Issues (4 copies of the same research issue).
- Engineers assigned in the engineering plan disappeared on restart because they were only tracked in memory.
- PRs were left in broken states (open but abandoned) after crashes.
- Agents would restart their work from scratch instead of resuming where they left off.

### Guidance that was needed:
- "There are now 4 issues of the same thing for the first research task... please consider how to best ensure that the solution doesn't duplicate Issues if they already exist"
- "The program restarted and those two engineers were gone on restart. Can we make sure the TeamMembers.md file is created... so when the program starts again, the PM agent can read that file and make sure all engineer agents are started"
- "Keep fixing bugs and doing a full reset and restarting when issues happen, until a full successful run end to end can happen"

### Takeaway:
**Design every agent operation as idempotent from day one:**
- Check if a GitHub Issue with the same title exists before creating one
- Check if a PR for a branch exists before creating one
- Check if a document already exists before generating it
- Persist agent state (assignments, progress) to durable storage (SQLite, GitHub files)
- On startup, scan existing GitHub state to reconstruct what happened
- Use labels, not memory, to track PR/Issue status

---

## 5. PR and Review Workflow Pitfalls

**Lesson:** The PR review cycle is the most complex part of agent orchestration and generated more bugs than any other subsystem.

### What happened:
- **Review timing:** Reviewers would start reviewing PRs before the engineer finished coding, reviewing placeholder files.
- **Review spam:** Reviewers would post 4+ duplicate reviews on the same PR because the message bus re-triggered reviews after every rework cycle.
- **Verbose reviews:** AI reviewers wrote 2000-4000 character reviews with headers, bullet lists, and summaries when a 2-sentence verdict was sufficient.
- **Scope confusion:** The PM agent requested changes because a single PR didn't cover the entire PMSpec — not understanding that PRs are incremental.
- **Review loops:** The rework counter was tracked per-feedback-item instead of per-round, so with 2 reviewers, the 3-rework limit was hit in 1.5 actual rounds.
- **Approval deadlock:** The SE couldn't approve its own PR, but was listed as a required reviewer.
- **Force-approval gaps:** When max rework cycles were reached, the force-approval logic was blocked by stale "needs review" state.
- **PM giving code advice:** The PM was commenting on implementation details instead of business alignment.

### Guidance that was needed:
- "Ensure that the agents don't review the code until the engineering agents are ready with the code done in the PR"
- "Is it better to add labels for when to review, or just have the engineering agents send a message?"
- "The PM should not be giving code advice, just making sure the code accomplishes the PM spec/user story goals"
- "There should not be any review requesting changes because the PR doesn't cover the entire PMSpec"

### Takeaway:
**Define explicit review contracts:**
- Engineers signal "ready for review" via message bus — reviewers don't poll
- Each reviewer has a defined scope (PM = business alignment, Architect = architecture compliance, SE = code quality)
- Reviews are brief (1-3 sentences with a clear APPROVED/CHANGES_REQUESTED verdict)
- Rework counting is per-round, not per-feedback-item
- Force-approval exists as a safety valve with a reasonable threshold
- Agents who already approved don't re-review after someone else's feedback is addressed

---

## 6. AI Output Quality Control

**Lesson:** AI models can "break character" and produce completely unusable output, especially under certain prompt conditions or with smaller models.

### What happened:
- **Meta-commentary instead of work:** The AI posted reviews saying "I'm an interactive AI assistant with tools" and "I'm powered by Claude Haiku 4.5" instead of reviewing code.
- **Markdown documents instead of code:** Engineers generated markdown descriptions of what the code should do rather than actual source files.
- **Malformed file names:** AI put code fragments in file paths — literal `{` as a filename, `@using ReportingDashboard.Models` as a filename, `.gitignore (APPEND)` with instructions embedded in the path.
- **WIP placeholders committed:** Documents were committed with "Work in progress, being generated..." text.
- **Truncated output:** Large implementations were cut off mid-file, leaving broken code committed.
- **Preamble contamination:** AI responses started with "Here's the implementation:" or "Sure, I'll help with that" which ended up in committed files.

### Guidance that was needed:
- "The content in the files for the pull requests are not actually giving the content, but just a simple sentence or two"
- "The Test Engineer PR has a test plan but I am not seeing the code — it SHOULD not just be writing documents but actual unit tests"
- "Look through the Code in the repo and notice the names of the files are messed up, actual code pieces like a `{` bracket in the name"

### Takeaway:
**Validate every AI output before committing:**
- Parse and validate file paths against a whitelist of valid characters and known extensions
- Strip preamble/postamble text from AI responses before extracting code
- Check that output contains actual code (not markdown descriptions)
- Never commit files with placeholder/WIP content
- Add output format instructions that are specific and rigid ("Output ONLY `FILE:` blocks, nothing else")
- Have a "self-review" pass where the agent checks its own output for common problems

---

## 7. Parallel Work and Merge Conflicts

**Lesson:** Multiple agents working on separate PRs simultaneously will inevitably create merge conflicts, and the system needs automated conflict resolution.

### What happened:
- Multiple engineers working on parallel PRs all branched from the same main commit. When the first PR merged, all other branches diverged.
- GitHub's built-in "Update Branch" API returned 422 errors on real content conflicts.
- Force-rebasing was needed: read all PR files → reset branch to main HEAD → re-commit everything on the clean base.
- Even with rebase logic, agents would sometimes create overlapping files despite the engineering plan saying they shouldn't.

### Guidance that was needed:
- Investigation into why "almost all PRs" had merge conflicts
- "Maybe even make sure the agents are regularly pulling the latest before starting new work"

### Takeaway:
**Design the task decomposition for parallel safety:**
- The engineering plan should assign distinct file sets to each task (no two tasks modify the same file)
- A "foundation" task (T1) should establish the project skeleton before any parallel work begins
- Implement automated branch sync/rebase before every commit
- Have fallback conflict resolution (close conflicted PR, recreate on clean main)
- Include file ownership in task descriptions so engineers know what they're allowed to touch

---

## 8. Testing Agent Challenges

**Lesson:** The Test Engineer agent needs the most specific guidance of all agents — testing is the easiest place for AI to produce plausible-looking but non-functional output.

### What happened:
- TE initially wrote markdown test plans instead of code.
- When it did write code, it didn't include `.csproj` files, so tests couldn't compile.
- TE didn't know the technology stack, so it generated tests for the wrong framework.
- TE reviewed merged PRs that it had already tested (re-testing completed work).
- TE created duplicate test PRs for the same source PR.
- Test scaffolding (project files, shared fixtures) needed to be auto-generated when missing.

### Guidance that was needed:
- "The Test Engineer should only read the finished code after a PR is completed, reviewed and closed/merged"
- "It should ignore non-code artifacts in the repo, like markdown files"
- "It SHOULD not just be writing documents, but actual unit tests, integration tests and UI tests where applicable"

### Takeaway:
**For testing agents specifically:**
- Provide explicit technology stack and testing framework in configuration
- Include example test file structure in the prompt
- Auto-scaffold test project files (.csproj with correct dependencies)
- Build and run tests locally before committing — if they don't compile, regenerate
- Define clear test tiers (Unit, Integration, UI) with distinct guidelines for each
- Only trigger test generation on merged code PRs, not document PRs

---

## 9. Model Selection and Cost Management

**Lesson:** Use the cheapest viable model for iterative development, and reserve premium models for production runs.

### What happened:
- Initial development used Opus 4.6 (premium model) for all agents, costing significant resources during the many failed runs needed to debug the pipeline.
- Each end-to-end run took 30-60 minutes with premium models, and the first ~15 runs all had bugs requiring restart.
- Switching to GPT-mini for testing reduced iteration time to 10-15 minutes per run.

### Guidance that was given:
- "Change all the copilot CLI models to use something like the latest OpenAI mini model, I don't want to have to keep waiting for expensive opus calls to run just to test the end to end, since it hasn't worked once for the last many hours and I don't want to keep wasting money/resources/time until I know the core logic is good."

### Takeaway:
**Implement a "FastMode" toggle from day one:**
- Use budget/mini models during pipeline development and debugging
- Only switch to premium models once the pipeline logic is validated
- Design a model tier system (premium/standard/budget/local) that maps to agent roles
- Quality-critical decisions (PM spec, Architecture) benefit most from premium models
- Code generation (Engineers) gets best cost/quality ratio from standard-tier models
- Simple tasks (Software Engineer assignments) work fine with budget models

---

## 10. Design and UI Quality

**Lesson:** AI agents will completely ignore visual design references unless every agent in the pipeline is explicitly instructed to read, analyze, and propagate design specifications.

### What happened:
- A professional HTML design reference (`OriginalDesignConcept.html`) with SVG Gantt timelines, monthly heatmap grids, and precise color schemes was sitting in the repository root.
- **Not a single agent** — Researcher, PM, Architect, SE, or Engineers — ever read it.
- The built UI was bare, unstyled HTML that looked "like something a free local model would have created in 4 minutes."
- The design file had specific CSS grid patterns, hex color codes, typography specifications, and component layouts — all ignored.

### Guidance that was needed:
- "Look at how ugly this UI looks... this looks NOTHING like the original HTML design and picture I gave as a reference"
- "Please figure out why the example design was completely ignored"
- "Ensure the PM reads the design files, puts together a terrific and detailed description, and incorporates that into the PM Spec document with images, screenshots, or whatever design files were created"
- "Make sure the Researcher agent is looking at those design ideas as well"
- "And the architect of course too"
- "Make sure the SE agent puts in the design details in Issues and PRs where able to give perfect design guidance"
- "Ensure the testing engineer knows the designs well to know how to best test them and do the UI tests too"

### Takeaway:
**Design context must flow through EVERY layer of the pipeline:**
```
Design Files in Repo
  → Researcher: Analyzes design, recommends technologies for the specific design
  → PM: Creates "Visual Design Specification" section in PMSpec with layout, colors, interaction scenarios
  → Architect: Creates "UI Component Architecture" mapping visual sections to code components
  → SE: Includes design details in every UI-related engineering task issue
  → Engineers: Reads design files before code generation, includes in AI prompts
  → TE: Reads design context for UI tests, generates assertions for layout/color/structure conformance
```

If the design doesn't explicitly appear in every agent's prompt, it effectively doesn't exist.

---

## 11. Requirements and Scenario Documentation

**Lesson:** Writing detailed requirements with concrete workflow scenarios is the single highest-leverage activity for AI agent orchestration. Scenarios serve as both specification and test cases.

### What happened:
- The initial implementation was built from a general architecture description without formalized requirements.
- Bugs were discovered one at a time during live runs, each requiring a stop-fix-restart cycle.
- After creating a Requirements.md with numbered requirements and 14+ workflow scenarios, the fix rate improved dramatically — the scenarios made it possible to trace expected vs. actual behavior systematically.
- The scenarios also served as the Test Engineer's reference for self-diagnostics.

### Guidance that was given:
- "Search back in this whole session and go through all the requirements given by me, and fully detail them out in a Requirements.md file"
- "For each requirement, provide a workflow scenario that gives an example of how that requirement is expected to work"
- "Generate 5 robust workflow examples and for each one, look through the code and ensure the code is built to operate that way — I want to avoid all extra hours of me having to keep running the solution, waiting, only to find you didn't consider a needed feature"

### Takeaway:
**Write scenario-based requirements BEFORE building the agent pipeline:**
- Each requirement should have a concrete workflow scenario (Given/When/Then or step-by-step)
- Scenarios should cover: happy path, error recovery, restart recovery, parallel execution, review loops
- Include explicit "should NOT" statements (e.g., "PM should NOT give code advice during reviews")
- Keep the document as a living artifact — update it as new scenarios are discovered
- Use the requirements doc for agent self-diagnostics and automated scenario testing

---

## 12. Recommendations for Agent-Based Development

Based on 50+ build-run-fix cycles, here is the recommended approach for teams building AI agent development pipelines:

### Before Writing Any Agent Code

1. **Write the Requirements.md first** with numbered requirements and workflow scenarios. This document will save more time than any other artifact.

2. **Create a context matrix** mapping every agent role × every action → required documents. If a document isn't listed, the agent won't read it.

3. **Design the monitoring dashboard** before the agent pipeline. You will spend 70%+ of your time watching agents and diagnosing issues. Make that experience good.

4. **Define review contracts** specifying: who reviews what, what scope each reviewer covers, how verdicts are structured, maximum rework cycles, and force-approval thresholds.

5. **Include visual design files** in the initial repository and plan how design context flows through every pipeline stage.

### When Building the Pipeline

6. **Start with FastMode** — use budget models for all agents during development. Switch to premium models only after pipeline logic is validated.

7. **Make every operation idempotent** — check before creating Issues, PRs, documents. Assume the agent will be restarted mid-operation.

8. **Validate all AI output** before committing — file paths, file content, output format. AI models will produce surprising garbage that must be caught.

9. **Design tasks for parallel safety** — each task owns distinct files, a foundation task runs first, dependency graphs are explicit.

10. **Build incremental** — get Research → PMSpec → Architecture working before adding engineering. Get one engineer working before adding parallelism.

### During Iterative Runs

11. **Watch the first 5 runs end-to-end.** Don't multi-task. The bugs you catch in real-time during run 1-5 would take 10x longer to diagnose from logs.

12. **Fix in batches, not one-at-a-time.** Stop the run, fix all observed issues, reset everything, restart clean. Don't patch mid-run.

13. **Keep a running log** of every user intervention that was needed. These become your requirements updates and your lessons learned.

14. **Reset cleanly between runs** — delete all GitHub artifacts (Issues, PRs, branches, files), clear local state (SQLite DB, workspace), and start fresh. Partial state from failed runs will mask new bugs.

15. **Update Requirements.md after every significant fix.** If you had to tell the agent something new, it's a new requirement that should be documented for future runs.

### The Hard Truth

Building a multi-agent AI development pipeline is not a "set it and forget it" exercise. Even with the best planning:
- **~30% of the work** is the initial implementation
- **~50% of the work** is iterating on emergent behaviors discovered during live runs
- **~20% of the work** is refining prompt engineering, output validation, and context management

The agents will not "figure out" what you want from a high-level description. They need explicit, detailed, repeated instruction at every step. The good news is that once the pipeline is tuned, it reliably produces high-quality output — but getting there requires patience and a willingness to observe, diagnose, and correct at a granular level.

---

## 13. Dashboard Architecture and Process Separation

**Lesson:** Running the monitoring dashboard in the same process as the agents creates a devastating development feedback loop. Any UI tweak requires killing the runner, losing all agent state, rebuilding, and restarting from scratch.

### What happened:
- The dashboard was initially embedded in the Runner process as a Blazor Server app. This seemed simpler — one process, shared DI container, in-process data access.
- During the timeline and overview page iterations, every CSS change, Razor fix, or layout tweak required stopping the Runner. All 7 agents died. In-memory state (message bus subscriptions, agent assignments, rework queues) was lost.
- A typical UI iteration cycle was: stop Runner → edit Razor file → rebuild → restart → wait 2-3 minutes for agents to reinitialize → navigate to the page → discover the fix didn't work → repeat. Each cycle cost 5+ minutes of wall-clock time.
- DLL locks from running processes prevented rebuilds — the `copilot` CLI child processes held locks on assemblies. Both the Runner PID and child `dotnet` PIDs had to be killed.

### Guidance that was needed:
- "The dashboard should be a separate process so I can iterate on the UI without killing agents"
- "Agents lose all their state when the dashboard crashes or needs a rebuild"
- "Need a way to restart just the UI without affecting the backend"

### Technical decisions and pitfalls:
- **`IDashboardDataService` interface**: Decouples Razor pages from the data source. `DashboardDataService` (in-process) vs. `HttpDashboardDataService` (HTTP client for standalone mode). Razor pages never know which implementation they're using.
- **REST API exposure**: Runner exposes ~30 endpoints at `/api/dashboard/*` for the standalone dashboard to consume. This was the path of least resistance — SignalR cross-process would have been more complex.
- **`IHttpClientFactory` vs `AddHttpClient<T>`**: `AddHttpClient<T>` registers a transient factory, which conflicts with singleton service registration. The standalone dashboard's `HttpDashboardDataService` is a singleton (it holds HTTP client state). Using `IHttpClientFactory` with named clients resolved the DI conflict.
- **Stub services**: The standalone dashboard project needs registrations for services it doesn't host (`NullGitHubService`, `GateNotificationService`, `AgentStateStore`, `BuildTestMetrics`) because Razor pages reference them transitively through shared components.
- **`DashboardMode(IsStandalone: bool)`**: A simple record that controls NavMenu visibility and other behavioral differences. Injected via DI — pages check `_dashboardMode.IsStandalone` to conditionally render elements.

### Takeaway:
**Separate the monitoring UI from the agent runtime from the start.** The embedded-dashboard shortcut saves 30 minutes of initial setup and costs hours of lost productivity during UI iteration. Architecture pattern: Runner exposes a REST API, Dashboard.Host consumes it via `HttpDashboardDataService`, shared Razor components use `IDashboardDataService` abstraction so they work in both modes.

---

## 20. Hardcoded Port Bindings Break Automated UI Testing

**Lesson:** AI-generated ASP.NET apps frequently include `app.Urls.Clear(); app.Urls.Add("http://localhost:5050")` which is a **programmatic override** that defeats ALL external configuration — `ASPNETCORE_URLS` env var, `--urls` CLI args, `launchSettings.json`, everything. This silently breaks any test infrastructure that starts apps on unique ports.

### What happened:
- The Test Engineer derives a unique port per workspace (hash-based, range 5100-5899) and sets `ASPNETCORE_URLS` env var.
- AI-generated `Program.cs` files contain `app.Urls.Add("http://localhost:5050")` because that's what the agent learned from examples.
- The `app.Urls.Add()` call sets `PreferHostingUrls = true` on `IServerAddressesFeature`, overriding everything in Kestrel's config hierarchy.
- The TE's app health check waited 90 seconds then timed out — the app was listening on 5050 (hardcoded), not the unique port.

### Three iterations of fixes (each built on the previous failure):
1. **Replace with env-var-reading code** — Replaced `app.Urls.Add("url")` with `app.Urls.Add(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "url")`. Worked locally, failed in production. Hypothesis: `dotnet run` skipped recompilation and used cached build output.
2. **Delete bin/ to force rebuild** — Added bin/ deletion after patching. Still failed. `dotnet run` may use obj/ artifacts to skip compilation.
3. **Comment out entirely + delete bin/ AND obj/** — Instead of replacing the line with new code, comment it out entirely: `// [PlaywrightRunner] app.Urls.Add(...)`. Delete both `bin/` and `obj/` directories. This way there's zero programmatic URL override — Kestrel falls back to `ASPNETCORE_URLS` env var naturally. ✅ **This approach worked.**

### Key Kestrel URL priority (highest to lowest):
1. `app.Urls.Add()` / `UseUrls()` (programmatic — **overrides everything**)
2. `ASPNETCORE_URLS` environment variable
3. `--urls` command line argument
4. `appsettings.json` Kestrel section
5. Default (`http://localhost:5000`)

### Takeaway:
**When patching AI-generated source code, commenting out is more reliable than replacing.** Replacement introduces compilation dependencies, cache invalidation issues, and subtle failures. Commenting preserves the original intent (as documentation) while cleanly removing the behavior. Always delete both `bin/` and `obj/` to guarantee recompilation after source patching.

---

## 21. Blazor Server SynchronizationContext Kills HTTP Calls

**Lesson:** Blazor Server's `DispatcherSynchronizationContext` interferes with `SocketsHttpHandler` I/O. HTTP calls made from Razor component event handlers or SignalR hub contexts get their socket reads aborted with `ERROR_OPERATION_ABORTED` (Win32 error 995), causing mysterious timeouts.

### What happened:
- The standalone dashboard's Configuration page makes HTTP POST to save settings to the Runner API.
- Save button handler calls `HttpClient.PostAsJsonAsync()` directly from the Blazor component.
- The call consistently timed out after exactly 100 seconds (HttpClient default timeout).
- Root cause: Blazor Server marshals continuations back to its sync context. The `SocketsHttpHandler` async I/O completion posts to this context, which can deadlock or abort.

### Fix:
Wrap all HTTP calls in `Task.Run(async () => ...)` to escape the sync context:
```csharp
var response = await Task.Run(async () => 
    await _httpClient.PostAsJsonAsync("/api/config", settings));
```

### Takeaway:
**In Blazor Server, always use `Task.Run` for HTTP calls to external services.** This is a known .NET pattern — `ConfigureAwait(false)` is insufficient because `SocketsHttpHandler` itself uses the sync context for I/O completion callbacks.

---

## 22. Transient Status Flash from Pre-Gate Status Updates

**Lesson:** Agents that call `UpdateStatus("⏳ Awaiting human approval...")` BEFORE checking whether the gate actually requires human approval create a misleading status flash on the dashboard. Even if the gate is in auto mode and returns `Proceed` in <1ms, the dashboard's 10-second poll interval can capture the transient status.

### What happened:
- All agents with gates (PM, Architect, Software Engineer, Researcher) updated their status to "Awaiting human approval" before calling `WaitForGateAsync`.
- When gates are in auto mode, `CheckGateAsync` returns `Proceed` instantly — but the status update was already published.
- The dashboard polling at 10s intervals would randomly show "⏳ Awaiting human approval" on agent cards, then switch back to the real status on the next poll.

### Fix:
Guard status updates with `_gateCheck.RequiresHuman(gateId)`:
```csharp
if (_gateCheck.RequiresHuman("pm_spec_review"))
    UpdateStatus("⏳ Awaiting human approval for PM Spec...");
```

### Takeaway:
**Never publish user-visible status updates for conditional operations before checking the condition.** The general pattern: check first, update status second. This applies to any UI where polling intervals create windows for stale state.

---

## 23. AI Agents Rewrite Components from Scratch During Incremental PRs

**Lesson:** When agents implement incremental features (e.g., "add heatmap section to existing dashboard"), they frequently rewrite the entire component from scratch instead of surgically adding the new section. This causes visual regressions in previously-working UI elements.

### What happened:
- PR #1266 was tasked with adding a CSS Grid heatmap component as the third band in `Dashboard.razor`.
- The agent rewrote the entire `Dashboard.razor` file (+199/-157 lines) and modified `dashboard.css` (+88/-49 lines), including changes to the existing header and timeline sections.
- The rewrite changed CSS class names (`.tl-ws-label` → `.tl-ws`), removed `display: inline-block` from icon elements, reformatted styles, and restructured the Razor code-behind pattern.
- The resulting UI rendered correctly (tests passed) but looked visually different from the original design — likely due to cascading CSS changes affecting the header/timeline colors and layout.

### Why this happens:
- AI agents have limited context about which parts of a file "work" and which are being modified.
- Regenerating from scratch (with the full spec) often produces cleaner code than surgical insertion.
- The agent's acceptance criteria focused on the heatmap section — it didn't have explicit "do not change existing sections" constraints beyond file-level restrictions.

### Takeaway:
**Include explicit preservation constraints in task acceptance criteria.** For incremental features, specify: "Existing header and timeline sections MUST NOT be modified — diff should show additions only in the heatmap region." Consider PR review checks that flag unexpected changes to existing component sections.

---

## 14. GitHub API Rate Limiting and Caching

**Lesson:** A multi-agent system with 7 agents polling GitHub every 30 seconds, plus a dashboard refreshing on its own cadence, will burn through the 5000/hour GitHub API rate limit in approximately 30 minutes without caching. Rate limiting and caching are not optimizations — they are prerequisites for the system to function.

### What happened:
- Early runs had no caching. Each agent called `GetOpenIssuesAsync` and `GetOpenPullRequestsAsync` every poll cycle. With 7 agents polling every 30 seconds: 14 list-endpoint calls × 2 cycles/minute = 28 calls/minute just for list endpoints.
- Add mutation calls (creating issues, posting comments, updating labels) and the dashboard's own polling, and the system was making 50-80 API calls per minute.
- After ~30 minutes, the system hit GitHub's rate limit. The `RateLimitManager` existed in code but was **never wired into `GitHubService`** — all 76 API call sites bypassed it entirely. This was a CRITICAL bug that went undetected for multiple sessions.
- Even after wiring `RateLimitManager`, the system still consumed quota too fast because every agent independently fetched the same list data that hadn't changed since the last poll.

### Guidance that was needed:
- "Why are we hitting the rate limit so fast? We have 5000 calls per hour"
- "The agents are all requesting the same data — can we cache it?"
- "We need the rate limit manager to actually be used"

### Technical solution:
- **30-second TTL shared cache**: `GitHubService` caches responses for 7 hot-path list methods. First caller hits the API; subsequent callers within 30 seconds get the cached response. `SemaphoreSlim(1,1)` with double-checked locking prevents thundering herd.
- **Mutation-triggered invalidation**: All mutation methods (create issue, update PR, post comment, merge, etc.) call `InvalidateListCaches()` so the next read reflects the change. This ensures cache staleness never exceeds 30 seconds AND mutations are immediately visible.
- **Force Refresh**: Dashboard Force Refresh button calls `InvalidateListCaches()` before fetching, giving the user a way to bypass the cache on demand.
- **Net result**: ~90% reduction in list-endpoint API calls. A full run that previously hit the rate limit at 30 minutes now runs for hours without approaching the limit.

### Takeaway:
**Implement caching and rate limit management before the first multi-agent run.** The math is straightforward: `agents × endpoints_per_poll × polls_per_minute = calls_per_minute`. If that number exceeds `5000 / 60 ≈ 83`, you'll hit the limit within an hour. A 30-second TTL cache is the simplest fix — it's short enough that staleness is rarely a problem, and mutation-triggered invalidation handles the cases where it would be.

---

---

## 15. Vision-Based Screenshot Review

**Lesson:** If your AI agent receives a screenshot URL as plain text, it will "review" the screenshot based solely on the URL's presence — not its visual content. The AI will hallucinate a review, producing plausible-sounding approval text, because it literally cannot see the image.

### What happened:
- The Test Engineer posted screenshots of the running application as PR comments (e.g., "Screenshot: https://github.com/user/repo/assets/123/screenshot.png"). The three reviewers (PM, Architect, SE) received these URLs as plain text in their AI prompts.
- PR #1152 shipped with a screenshot that clearly showed a broken UI — an error page with "Error: Failed to load data.json" visible in the image. All three reviewers approved the PR because they could see the text "Screenshot: https://..." but physically could not view the image content.
- The AI models produced confident, specific-sounding reviews: "Screenshots confirm the UI is functioning correctly" — pure hallucination based on URL presence.

### Technical solution:
- Added `GetPRScreenshotImagesAsync` to `PullRequestWorkflow.cs` that downloads actual image bytes from GitHub PR comment URLs (max 5 images per PR, max 2MB each, 15-second timeout per download).
- `CopilotCliChatCompletionService` updated with `AppendMessageContent` helper that handles `ImageContent` by converting to base64 data URIs embedded directly in the prompt.
- All three reviewer agents (PM, Architect, SE) now receive actual screenshot images as Semantic Kernel `ImageContent` items in their ChatHistory — not just URLs.
- PM and Architect prompts explicitly instruct: "examine the screenshots for error pages, blank screens, JSON parse errors, broken layouts, missing CSS, or any visual indication the application is not working correctly."
- Falls back to URL-only text context (`GetPRScreenshotContextAsync`) if image download fails — degraded but not broken.

### Takeaway:
**If AI can't perceive the data format, it will hallucinate a review.** URL text ≠ image content. Always verify the AI can actually consume the input format you're providing. This applies broadly: don't pass audio file paths and expect transcription, don't pass binary data and expect parsing, don't pass image URLs and expect visual analysis. If the model needs to see something, you must embed the actual data in a format it can process.

---

## 16. Human Gate Configuration Must Be Enforced on ALL Code Paths

**Lesson:** A gate check that exists on one code path but not another is worse than no gate at all — it creates false confidence that human review is happening when it's silently bypassed on the unchecked path.

### What happened:
- The SE agent had two paths that could lead to a PR merge: (1) a direct merge path via `ApproveAndMaybeMergeAsync` (SE approves and immediately merges), and (2) a Phase 3 path via `MergeTestedPRsAsync` (after PM + TE approval). Only the Phase 3 path had the `FinalPRApproval` gate check. The direct merge path completely bypassed human gate review.
- Gate rejection results from `AssessGateApprovalAsync` were silently discarded — when a human posted "Not approved" on a PR, the SE ignored the rejection and continued to the next task without triggering rework.
- `GateCheckService` used `IOptions<AgentSquadConfig>` which captures config once at construction time. Changing gate configuration in the dashboard or appsettings.json had no effect until the runner was restarted, making it impossible to dynamically enable gates during a run.

### Technical solution:
- Added `ReadyToMerge` enum value and `deferMerge` parameter to `ApproveAndMaybeMergeAsync`. SE now checks `_gateCheck.RequiresHuman(GateIds.FinalPRApproval)` on BOTH merge paths.
- Gate rejection results are now properly handled: if `GateDecision.Rejected`, SE sends a `ChangesRequestedMessage` with the human's feedback and triggers a rework cycle. Human-initiated rework uses "HumanReviewer" as `ReviewerAgent`.
- Changed `GateCheckService` from `IOptions<AgentSquadConfig>` to `IOptionsMonitor<AgentSquadConfig>` with a `Config` property that reads `_configMonitor.CurrentValue.HumanInteraction` on every gate check call. Changes to appsettings.json are now picked up at runtime.

### Takeaway:
**Every code path that can produce the gated outcome must be audited for the gate check.** This is the same class of bug as "forgot to check permissions on the admin endpoint" — the fix is systematic: enumerate all paths to the outcome, verify each one enforces the gate, and write unit tests that exercise each path with the gate enabled. For configuration, use `IOptionsMonitor` (not `IOptions`) whenever the setting might need to change at runtime.

---

## 17. Port Conflicts When Multiple Agents Run Apps Simultaneously

**Lesson:** When multiple agents (SE + TE, or multiple PEs) try to start the application under test on the same port (e.g., `:5100`), the second process fails because the port is already bound. This manifests as "App did not respond at http://localhost:5100 within 90s" — a timeout that looks like a build failure but is actually a port conflict.

### What happened:
- The workspace config had a single `AppBaseUrl` of `http://localhost:5100` shared by all agents.
- SE 1 starts the app on `:5100` to capture a screenshot — succeeds.
- TE starts the app on `:5100` to run UI tests — the port is occupied by SE 1's still-running app, so `dotnet run` fails silently or can't bind.
- Activity log shows concurrent screenshot capture (SE at 19:02) and UI test execution (TE at 18:56-19:04) on the same port.
- The TE's "app not responding" error was misdiagnosed as a build failure, missing data.json, or project path issue.

### Technical solution:
- Added `DeriveUniquePort(workspacePath)` to `PlaywrightRunner` — hashes the workspace path to a port in the range 5100–5899.
- Each agent's workspace path contains their unique agent ID (e.g., `C:\Agents\testengineer-8f2b...\`), so each gets a different port.
- Applied port rewriting in both `RunUITestsAsync` (TE tests) and `CaptureAppScreenshotAsync` (SE screenshots).
- Port rewrite also updates `ASPNETCORE_URLS` env var and the `--urls` flag in the start command.
- Config's `AppStartCommand` is temporarily swapped during execution and restored in the finally block.

### Takeaway:
**Any shared resource (port, file, temp directory) becomes a contention point in multi-agent systems.** When debugging "works for one agent but fails for another," check for resource sharing before investigating code bugs. The fix is to derive per-agent resources from the agent's identity — workspace path, agent ID, or a stable hash.

---

## 18. Standalone Dashboard Data Hydration from SQLite

**Lesson:** The standalone Dashboard process (port 5051) has an empty in-memory `AgentRegistry` because it's a separate process from the Runner. The `agent_state` SQLite table is also always empty because agents never call `SaveCheckpointAsync`. Dashboard data must be hydrated from the `ai_usage` and `activity_log` tables instead.

### What happened:
- Standalone Dashboard created its own empty SQLite DB in its working directory instead of reading the Runner's DB.
- After fixing the DB path, the `agent_state` table had 0 rows — no agent data.
- The `ai_usage` table (15 agents, with model/cost info) and `activity_log` table (287 entries, with status/timestamps) had all the data.
- Initial hydration showed 48 "agents" with wrong display names because the DB accumulated records from ALL previous restarts (old GUIDs + new GUIDs).

### Technical solution:
1. **DB path**: Dashboard's `Program.cs` resolves the Runner's DB file via relative path (`../AgentSquad.Runner/agentsquad_*.db`).
2. **Data hydration**: `DashboardDataService.SeedFromDatabase()` queries `ai_usage` for agent IDs + cost + model, and `activity_log` for latest status per agent.
3. **Boot time filtering**: `AgentStateStore.RecordBoot()` writes `last_boot_utc` to `run_metadata` table on each Runner startup. Dashboard filters agents to only those with activity AFTER `last_boot_utc`.
4. **Display names**: `InferRole()` extracts role from agent ID prefix (e.g., `SoftwareEngineer-xxx` → "Software Engineer"). `FormatDisplayName()` numbers agents per role.
5. **Periodic refresh**: Timer loop re-seeds from DB every 10 seconds.

### Takeaway:
**When a read-only satellite process needs data from a write-primary process, plan the data contract.** Don't assume the schema has the right tables — check which tables actually get populated during operation. Accumulated historical records need filtering by run identity (boot timestamp, run ID, etc.) to avoid showing stale agents.

---

---

## 19. GitHub API Pagination Is Mandatory During Reset

**Lesson:** GitHub's REST API returns a maximum of 100 items per page. A typical agent run creates 200+ issues, 20+ PRs, and 20+ branches. A single non-paginated API call during reset silently misses everything beyond page 1, leaving the repo dirty for the next run.

### What happened:
- Reset script fetched `GET /repos/{owner}/{repo}/issues?state=open&per_page=100` — returned 100 items (page 1 of 2+).
- Script closed all 100 items and reported "0 remaining" because the verification also only checked page 1.
- 9 issues on page 2 were never seen or closed.
- User discovered the leftover issues when checking the repo on another machine.

### Why this keeps happening:
- Copilot CLI sessions lose context through compaction — the "always paginate" detail gets lost.
- One-shot API calls look correct because they return data successfully; there's no error indicating pagination was needed.
- The verification step had the same bug as the cleanup step, so it confirmed "success" when the repo was still dirty.

### Technical solution:
- **Always use a pagination loop** for any GitHub API call during reset:
  ```
  do { fetch page; process items; page++ } while (batch.Count == per_page)
  ```
- **When closing items, always re-fetch page 1** each iteration (closing shifts items between pages — fetching page 2 after closing page 1 items skips newly-shifted items).
- **Verification must also paginate** — the check is useless if it has the same bug as the cleanup.

### Takeaway:
**Any GitHub API call that could return more than 100 results MUST paginate.** This includes issues, PRs, branches, commits, and comments. The failure mode is silent — you get valid data back, just not all of it. Build the pagination loop once and reuse it everywhere.

---

## 24. SE Parallelism Enhancements

**What**: Enhanced the Software Engineer agent's task planning to maximize parallel execution by multiple engineers working simultaneously on separate PRs.

**Key Lessons**:

1. **File Overlap Detection is Critical for Parallel Work** — When multiple engineers work on tasks in parallel, file conflicts are the #1 source of merge failures. Implementing `DetectFileOverlaps()` to compare owned files across tasks in the same wave, combined with AI-assisted repair via `ValidateAndRepairTaskPlanAsync()`, prevents conflicts before they happen. Prevention is far cheaper than resolution.

2. **Wave Scheduling Enables Structured Parallelism** — Assigning tasks to waves (W1, W2, W3+) based on dependency chains provides a simple but effective parallelism model. Targeting 60%+ of non-foundation tasks in W1 (depending only on the foundation task T1) maximizes throughput. A star topology (all tasks depend only on T1) is ideal but not always achievable.

3. **Typed Dependencies Add Precision** — Simple dependency lists (T1, T3) don't tell you WHY tasks depend on each other. Adding type annotations (T1(files), T3(api)) enables smarter scheduling and helps identify dependencies that could be restructured. The coupling type matters — a file dependency is harder to parallelize than an API dependency.

4. **Parallelism Metrics Drive Improvement** — Logging W1 percentage, overlap count, and a qualitative score (Excellent/Good/Fair/Poor) after each planning cycle creates a feedback loop. Without metrics, you can't tell if task decomposition is getting better or worse over time.

5. **Shared Files Must Be Explicit** — The SHARED file declaration pattern (e.g., `SHARED:Program.cs` in T1's FilePlan) makes it clear which files multiple tasks may touch. Without explicit shared file tracking, overlap detection would flag legitimate shared modifications as conflicts.

---

## 25. Decision Impact Classification & Gating

**What**: Implemented a system where agents classify their design decisions by impact level (XS-XL) and high-impact decisions are gated for human approval before the agent continues.

**Key Lessons**:

1. **AI Classification Beats Hardcoded Rules** — Using an AI turn to classify decision impact handles novel situations that rule-based systems would miss. The AI considers scope, reversibility, risk, and component count — factors that are hard to capture in static rules. Fallback to Medium on classification failure is a safe default.

2. **Optional Dependencies Preserve Backward Compatibility** — Making `DecisionGateService?` an optional constructor parameter (null-safe) across all 7 agent types means existing tests and configurations work unchanged. Agents check `_decisionGate != null` before calling, so the feature is purely additive. This pattern is essential when adding cross-cutting features to an established codebase.

3. **Extra AI Turns Only When Needed** — Generating structured implementation plans (plan generation turn) only for gated decisions (L+ by default) avoids slowing down routine XS/S decisions. The cost of an extra AI call is justified only when human review is required.

4. **Configurable Gate Thresholds Enable Progressive Adoption** — The `MinimumGateLevel` config ("None", "XS", "S", "M", "L", "XL") lets users start with light gating (L only) and tighten it over time. This reduces friction during initial adoption while still catching the highest-impact decisions.

5. **Separate Decision Storage from Reasoning Events** — `AgentDecision` records are richer than `AgentReasoningEvent` (rationale, alternatives, plan, approval status, feedback). Keeping them as separate data models with their own `IDecisionLog` interface enables purpose-built queries and UI without polluting the existing reasoning pipeline.

6. **Timeout Fallback Strategy Matters** — Gated decisions that block agents indefinitely can stall the entire pipeline. The configurable timeout with fallback action ("auto-approve" or "block") gives users control over the tradeoff between safety and throughput. Auto-approve after timeout is the pragmatic default for most teams.

7. **Dashboard Integration Requires Multiple Touchpoints** — A single "decisions" page isn't enough. Users need: (1) Filtering in the Reasoning tab to see decisions alongside other events, (2) Actionable approve/reject in the Approvals tab, and (3) A quick-glance count on the Overview page. Three integration points = complete visibility.

8. **Gate Notifications Should Reuse Existing Infrastructure** — Rather than building a new notification system, decision gates reuse `GateNotificationService` with a `"Decision:{id}"` prefix pattern. This leverages existing notification UI, polling, and resolution mechanics. Build on what exists.

---

## 26. Agent Task Steps — Real-Time Workflow Visibility

**What**: Added step-by-step progress tracking to all 7 agent roles, with a dashboard UI showing live step timelines, progress bars, timing, LLM call counts, and cost per step.

**Key Lessons**:

1. **Dynamic Steps Beat Pre-Planned Steps** — Agent execution paths are conditional (a PM may skip clarification, an engineer may not need rework). Pre-planning steps creates false predictions that confuse users when agents deviate. Emitting steps as they happen — `BeginStep()` when starting, `CompleteStep()` when done — ensures the UI always reflects reality. Step templates provide the "expected future" view without binding agents to a fixed plan.

2. **Step Instrumentation Must Be Non-Blocking** — Every `BeginStep`/`CompleteStep`/`RecordSubStep` call in agent code is wrapped in try/catch. If step tracking fails (OOM, corrupted state, race condition), the agent continues working. Observability must never interfere with execution. This is the same principle as logging — you never let a logging failure crash your service.

3. **Step Templates Provide UI Completeness Without Pre-Computation** — Users want to see "what's coming next" even before the agent reaches that step. `AgentStepTemplates` provides expected step names per role, shown greyed out in the UI. This gives progress context (3 of 7 steps done) without requiring the agent to pre-compute its plan. Templates are informational, not prescriptive — the agent may skip or add steps.

4. **Zero LLM Overhead Is Non-Negotiable for Observability** — Step tracking is pure in-process instrumentation — no extra AI calls, no token usage, no cost. Adding observability that consumes LLM budget would undermine the very visibility it provides by slowing agents down. The `AgentTaskTracker` is a ConcurrentDictionary with atomic status transitions — microsecond overhead.

5. **Sub-Steps Add Depth Without Complexity** — Rather than creating deeply nested step hierarchies, `RecordSubStep()` adds a flat child entry to an existing step (e.g., "Reviewing file: auth.cs" under "Code Review"). This gives meaningful progress detail during long-running steps without complicating the data model or the UI rendering.

6. **Shared Engineer Base Reduces Instrumentation Duplication** — Software Engineers share common workflows (issue pickup, implementation, build/test, PR creation) via `EngineerAgentBase`. Instrumenting steps at the base class level means both roles get step tracking for free, with role-specific steps added only in subclasses. This mirrors the existing agent architecture — step instrumentation follows the same inheritance patterns.

7. **REST API Enables External Tooling** — The five step endpoints (`/api/steps/{agentId}`, `/current`, `/progress`, `/active`, `/templates/{role}`) enable external dashboards, CLI tools, and automation scripts to consume step data. This is important for CI/CD integration where teams want to monitor agent progress programmatically, not just through the Blazor UI.

---

*This document was compiled from 80+ checkpoints, 400+ conversation turns, and 85+ end-to-end test runs across six Copilot CLI sessions building the AgentSquad system.*
