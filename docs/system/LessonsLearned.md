# Lessons Learned: Building a Multi-Agent AI Development System

> **Author:** Ben Humphrey (azurenerd) with Copilot CLI  
> **Project:** VirtualDevTeam — a .NET 8 multi-agent system where 7 AI agents (PM, Researcher, Architect, Software Engineer, Software Engineers, Test Engineer) collaborate through GitHub PRs/Issues to build software.  
> **Purpose:** This document captures hard-won lessons from ~100+ iterative build-run-fix cycles over multiple sessions. It's intended for engineering teams considering AI agent-based development pipelines, to help them avoid the same pitfalls and build better agent orchestration from day one.

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
27. [Stale Merged PRs Cause False Task Drops](#27-stale-merged-prs-cause-false-task-drops)
28. [Standalone Dashboard Must Use HTTP-Based Data Service](#28-standalone-dashboard-must-use-http-based-data-service)
29. [Persisted SME Definitions Auto-Respawn on Startup](#29-persisted-sme-definitions-auto-respawn-on-startup)
30. [JSON Case Sensitivity Breaks Dashboard Polling](#30-json-case-sensitivity-breaks-dashboard-polling)
31. [Exact String Skill Matching Fails for Semantic Concepts](#32-exact-string-skill-matching-fails-for-semantic-concepts)
32. [Per-PR Rework Counting Causes Premature Exhaustion](#33-per-pr-rework-counting-causes-premature-exhaustion)
33. [Blank Screenshots from Unstyled Placeholder Components](#34-blank-screenshots-from-unstyled-placeholder-components)
34. [Don't Gitignore Data Files — They Break Screenshots and Clones](#35-dont-gitignore-data-files--they-break-screenshots-and-clones)
35. [Port-Binding Bugs Are a Recurring Class — Unify the Launch Pipeline](#36-port-binding-bugs-are-a-recurring-class--unify-the-launch-pipeline)
36. [Layer Periodic Health Checks on Top of Event-Driven Ones](#37-layer-periodic-health-checks-on-top-of-event-driven-ones)
37. [Duplicate-Action Guards Are Mandatory for Multi-Agent State Transitions](#38-duplicate-action-guards-are-mandatory-for-multi-agent-state-transitions)
38. [Re-Inject Source Artifacts at Every Prompt Hop > 1](#39-re-inject-source-artifacts-at-every-prompt-hop--1)
39. [Every GitHub API Call Must Assume the Target State Has Changed](#40-every-github-api-call-must-assume-the-target-state-has-changed)
40. [Surface AI Reasoning in the UI, Not Just the Logs](#41-surface-ai-reasoning-in-the-ui-not-just-the-logs)
41. [Partial-Reset Scripts Dramatically Speed Up Late-Stage Debugging](#42-partial-reset-scripts-dramatically-speed-up-late-stage-debugging)
42. [MCP Server Auth Changes Require Process Restart](#43-mcp-server-auth-changes-require-process-restart)
43. [Centralize Model Version Strings to a Single Constant](#44-centralize-model-version-strings-to-a-single-constant)
44. [Rubber-Duck Critique Between Plan and Implementation Prevents Over-Engineering](#45-rubber-duck-critique-between-plan-and-implementation-prevents-over-engineering)
45. [`Configure<T>.Bind` Appends to Collection Defaults — It Does Not Replace](#46-configuretbind-appends-to-collection-defaults--it-does-not-replace)
46. [`.git/config.lock` Races Invisibly Under Parallel `git worktree add`](#47-gitconfiglock-races-invisibly-under-parallel-git-worktree-add)
47. [Emit `Completed(false)` Synchronously on `Started` Path Failures](#48-emit-completedfalse-synchronously-on-started-path-failures--never-let-exceptions-propagate-to-taskwhenall)
48. [val-e2e: Close Open PRs Before Live Runs](#49-val-e2e-close-open-prs-before-live-runs--checkpoint-recovery-bypasses-new-features)
49. [Copilot CLI Doesn't Report Tokens — Cost Attribution Is `$0` Until API-Key Fallback](#50-copilot-cli-doesnt-report-tokens--cost-attribution-is-0-until-api-key-fallback)
50. [Experiment Data Paths: Relative Paths Resolve Against Runner Cwd](#51-experiment-data-paths-relative-paths-resolve-against-runner-cwd-bin-dir-not-repo-root)
51. [SinglePRMode Task Leak — `ValidateEnhancementCoverageAsync` Must Respect Mode](#52-singleprmodemode-task-leak--validateenhancementcoverageasync-must-respect-mode)
52. [Per-Candidate Strategy Screenshots — Capture at Build Gate, Not at Winner Selection](#53-per-candidate-strategy-screenshots--capture-at-build-gate-not-at-winner-selection)
53. [Dashboard Strategy Key Mismatch — Use Canonical IDs Everywhere](#54-dashboard-strategy-key-mismatch--use-canonical-ids-everywhere)
54. [Own-PR Review Downgrade Loses Inline Comment Positions](#55-own-pr-review-downgrade-loses-inline-comment-positions)
55. [Wave Ordering Collisions — Hash-Based IDs Prevent Task Drops](#56-wave-ordering-collisions--hash-based-ids-prevent-task-drops)
56. [Premature Enhancement Closure After Mini-Reset — Guard Against Vacuously True Conditions](#57-premature-enhancement-closure-after-mini-reset--guard-against-vacuously-true-conditions)
57. [In-Memory State Flags Lost on Process Restart — Recover from Durable State](#58-in-memory-state-flags-lost-on-process-restart--recover-from-durable-state)
58. [EMU GitHub Restrictions — `gh` CLI Fails for Enterprise Managed Users](#59-emu-github-restrictions--gh-cli-fails-for-enterprise-managed-users)
59. [First Successful End-to-End Run — What Made It Work](#60-first-successful-end-to-end-run--what-made-it-work)
60. [External Agentic Framework Integration — Spike Before You Abstract](#61-external-agentic-framework-integration--spike-before-you-abstract)
61. [Standalone Dashboard DI Must Mirror Runner Registrations](#62-standalone-dashboard-di-must-mirror-runner-registrations)
62. [NEVER Put Secrets in Tracked Config Files](#63-never-put-secrets-in-tracked-config-files)
63. [Strategy Results Must Survive Process Restarts — Persist to SQLite](#63-strategy-results-must-survive-process-restarts--persist-to-sqlite)
64. [Capability-Based Interfaces Beat Monolithic Abstractions for Platform Providers](#64-capability-based-interfaces-beat-monolithic-abstractions-for-platform-providers)
65. [Never Use IGitHubService Directly for Agent Work Artifacts](#65-never-use-igithubservice-directly-for-agent-work-artifacts)
66. [DI Dual-Registration Pattern — Runner and StandaloneServiceRegistration Must Stay in Sync](#66-di-dual-registration-pattern--runner-and-standaloneserviceregistration-must-stay-in-sync)
67. [Task/Step Tracking Hierarchy — Tasks Are Groups, Steps Are Atomic](#67-taskstep-tracking-hierarchy--tasks-are-groups-steps-are-atomic)
68. [Concurrent Label Writes Cause Silent Overwrites — Always Re-Fetch Before Write](#68-concurrent-label-writes-cause-silent-overwrites--always-re-fetch-before-write)
69. [Recovery Must Cross-Reference PRs and Tasks — In-Memory State Is Not Durable](#69-recovery-must-cross-reference-prs-and-tasks--in-memory-state-is-not-durable)
70. [TE Must Guard Against PRs With Zero Changed Files](#70-te-must-guard-against-prs-with-zero-changed-files)
71. [JSONL Output Mode Breaks Direct ExecutePromptAsync Callers](#82-jsonl-output-mode-breaks-direct-executepromptasync-callers)
72. [Complexity-Based PR Sizing Prevents Task Explosion](#83-complexity-based-pr-sizing-prevents-task-explosion)
71. [Generic "AI Call in Progress" Status Is Useless for Monitoring](#71-generic-ai-call-in-progress-status-is-useless-for-monitoring)
72. [Stale Local Gate Approvals Auto-Approve Subsequent Resources](#72-stale-local-gate-approvals-auto-approve-subsequent-resources)
73. [Absolute Workspace Paths Break on Repo Rename or Move](#73-absolute-workspace-paths-break-on-repo-rename-or-move)
74. [develop-settings.json Is the Runtime Source of Truth, Not appsettings.json](#74-develop-settingsjson-is-the-runtime-source-of-truth-not-appsettingsjson)
75. [Run Switching Requires Explicit Cancellation of Paused Runs](#75-run-switching-requires-explicit-cancellation-of-paused-runs)
76. [Framework Orphan Recovery — Clean Up Worktrees and Processes on Crash](#76-framework-orphan-recovery--clean-up-worktrees-and-processes-on-crash)
77. [Agent Status Must Reflect Actual Work, Not Assumed Work](#77-agent-status-must-reflect-actual-work-not-assumed-work)

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
- **REST API exposure**: Runner exposes ~30 endpoints at `/api/dashboard/*` for external tooling and the optional standalone dashboard. SignalR cross-process would have been more complex.
- **`IHttpClientFactory` vs `AddHttpClient<T>`**: `AddHttpClient<T>` registers a transient factory, which conflicts with singleton service registration. The standalone dashboard's `HttpDashboardDataService` is a singleton (it holds HTTP client state). Using `IHttpClientFactory` with named clients resolved the DI conflict.
- **Stub services**: The standalone dashboard project needs registrations for services it doesn't host (`NullGitHubService`, `GateNotificationService`, `AgentStateStore`, `BuildTestMetrics`) because Razor pages reference them transitively through shared components.
- **`DashboardMode(IsStandalone: bool)`**: A simple record that controls behavioral differences. Injected via DI — pages check `_dashboardMode.IsStandalone` to use HTTP polling (standalone) or direct DI access (embedded).

### Takeaway:
**Single-process is better for development.** The standalone dashboard was created to allow UI iteration without restarting agents, but in practice it caused constant DI synchronization issues (every new Runner service needed a stub in `StandaloneServiceRegistration`). The embedded dashboard (port 5050) provides all pages with real-time in-process data and zero DI mismatch issues. The standalone project is kept only for remote monitoring scenarios.

---

## 20. Hardcoded Port Bindings Break Automated UI Testing

> **UPDATED (April 2026):** The "patch and retry" approach described here proved insufficient long-term — AI agents found new ways to hardcode ports in every subsequent generation (`ListenAnyIP`, `Configuration["urls"]`, `ConfigureKestrel`, `launchSettings.json applicationUrl`). Superseded by the unified `LaunchVerifiedAppAsync` pipeline — see **Lesson 36**.

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
- `GateCheckService` used `IOptions<VirtualDevTeamConfig>` which captures config once at construction time. Changing gate configuration in the dashboard or appsettings.json had no effect until the runner was restarted, making it impossible to dynamically enable gates during a run.

### Technical solution:
- Added `ReadyToMerge` enum value and `deferMerge` parameter to `ApproveAndMaybeMergeAsync`. SE now checks `_gateCheck.RequiresHuman(GateIds.FinalPRApproval)` on BOTH merge paths.
- Gate rejection results are now properly handled: if `GateDecision.Rejected`, SE sends a `ChangesRequestedMessage` with the human's feedback and triggers a rework cycle. Human-initiated rework uses "HumanReviewer" as `ReviewerAgent`.
- Changed `GateCheckService` from `IOptions<VirtualDevTeamConfig>` to `IOptionsMonitor<VirtualDevTeamConfig>` with a `Config` property that reads `_configMonitor.CurrentValue.HumanInteraction` on every gate check call. Changes to appsettings.json are now picked up at runtime.

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
1. **DB path**: Dashboard's `Program.cs` resolves the Runner's DB file via relative path (`../VirtualDevTeam.Runner/virtualdevteam_*.db`).
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

## 27. Single-Process Dashboard Architecture (Consolidated)

**Lesson:** The standalone dashboard (port 5051) caused constant friction — DI errors whenever new services were added, stale data from HTTP polling, and the operational burden of starting two processes. The embedded dashboard in the Runner (port 5050) has full in-process access to all services and works reliably.

**Current architecture (single-process):**
1. Start only the Runner: `cd src\VirtualDevTeam.Runner && dotnet run` (or `.\scripts\start-runner.ps1`)
2. Dashboard at http://localhost:5050 — all 18 pages, real-time data, zero DI issues

**Standalone dashboard (optional, for remote monitoring):**
- `cd src\VirtualDevTeam.Dashboard.Host && dotnet run` → port 5051
- Connects to Runner REST API — useful if monitoring from another machine
- Has known DI stub synchronization issues (every new service needs registration in `StandaloneServiceRegistration.cs`)

**Why single-process won:**
- Zero DI mismatch errors (pages access real services directly)
- Real-time data (in-process events vs HTTP polling latency)
- Simpler operations (one process to manage, not two)
- All pages visible (no NavMenu hiding based on IsStandalone)

---

## 28. Stale Merged PRs Cause False Task Drops

**Lesson:** `GetMergedPullRequestsAsync` returned ALL-TIME merged PRs instead of scoping to the current run. This caused the Leader SE to detect false file overlap between stale PRs from previous runs and current engineering tasks, triggering automatic task drops (closing issues, marking tasks complete when they hadn't been started).

**What happened:**
- The SE's post-plan dedup logic and worker-level pre-execution check both called `GetMergedPullRequestsAsync` to detect file overlap with already-completed work.
- A 50% file overlap threshold would auto-drop tasks and close their GitHub issues.
- Small tasks that touch shared files (Program.cs, .csproj) easily hit 50% overlap against any historical PR.
- Task T5 was auto-dropped because a stale PR from a previous run had modified the same files.

**The fix (two-part):**
1. **Scope merged PRs to current run**: Added `_runStartedUtc` filter to `GetMergedPullRequestsAsync` in `GitHubService.cs` — matching the filter already applied to open PRs and open issues.
2. **Change auto-drop to warning-only**: Both the post-plan dedup (~line 958) and worker-level pre-execution check (~line 1627) in `SoftwareEngineerAgent.cs` now log warnings instead of auto-dropping tasks or closing issues. Overlap detection is still passed as context to the AI code generator.

**Key insight:** File overlap ≠ task completion. Multiple tasks legitimately modify the same files (Program.cs, .csproj, shared models). Never auto-close issues based on file overlap analysis alone.

---

## 29. Standalone Dashboard Must Use HTTP-Based Data Service

**Lesson:** The standalone dashboard (port 5051) must use `HttpDashboardDataService` — never `DashboardDataService`. The in-process `DashboardDataService` reads from the local `AgentRegistry` which is always empty in standalone mode because agents run in the Runner process, not the Dashboard process.

**What happened:**
- Dashboard `Program.cs` registered `DashboardDataService` (the in-process implementation) instead of `HttpDashboardDataService` for standalone mode.
- Result: the standalone dashboard showed zero agents, no activity, no data.
- Same pattern affected `CostBadge.razor` (read from in-process `UsageTracker`, always $0.00) and `PlaywrightStatusBadge` (used bare `HttpClient` with no base address).

**The fix:**
1. **Dashboard/Program.cs**: Register `HttpDashboardDataService` as `IDashboardDataService` in standalone mode. It polls the Runner API at `/api/dashboard/*` for all data.
2. **CostBadge.razor**: In standalone mode, polls `/api/dashboard/cost-summary` via the `RunnerApi` named `HttpClient`.
3. **PlaywrightStatusBadge**: Switched from bare `HttpClient` to `IHttpClientFactory.CreateClient("RunnerApi")`.

**Audit rule:** Grep for `ServiceProvider.GetService<DashboardDataService>()` or any component using in-process services directly — these are standalone bugs. Every dashboard component must work via HTTP polling when running standalone.

---

## 30. Persisted SME Definitions Auto-Respawn on Startup

**Lesson:** SME (Subject Matter Expert) agents persist their definitions to `sme-definitions.json`. Definitions marked as `Continuous` mode auto-respawn on startup. If this file isn't cleaned up during reset, stale specialists from previous runs load before the PM creates new ones for the current project.

**The fix:**
- Added deletion of `sme-definitions*` files during cleanup Phase 3 in `ConfigurationService.cs`.
- Added SME definitions check to the mandatory verification block in `Session.md`.

**Rule:** Any file that causes agent behavior changes on startup must be cleaned during reset.

---

## 31. JSON Case Sensitivity Breaks Dashboard Polling

**Lesson:** `System.Text.Json` is case-sensitive by default. The standalone dashboard polls the Runner's REST API, which returns camelCase JSON. Without `PropertyNameCaseInsensitive = true`, deserialization silently returns default/null values instead of throwing, causing subtle data display bugs.

**What happened:**
- Step tracking data deserialized from the Runner API with all properties null/default.
- Dashboard showed empty step timelines despite the Runner having valid step data.

**Fix:** Always use `PropertyNameCaseInsensitive = true` in `JsonSerializerOptions` when deserializing API responses. This is a one-line fix but easy to forget on every new polling endpoint.

---

*This document was compiled from 80+ checkpoints, 400+ conversation turns, and 90+ end-to-end test runs across seven Copilot CLI sessions building the VirtualDevTeam system.*

---

## 32. Exact String Skill Matching Fails for Semantic Concepts

**Lesson:** The SE leader's task-to-engineer matching used `string.Equals` to compare task tags against engineer capabilities. This works when both sides use identical vocabulary (e.g., `frontend` ↔ `frontend`) but fails for semantic relationships: a `Frontend Engineer` with skills `[html, css, javascript]` won't match a task tagged `[react, ui, timeline]` even though they're the best candidate.

**What happened:**
- PM created a `Frontend Engineer` with capabilities like `html`, `css`, `javascript`
- SE plan had a task tagged `react`, `ui`  
- Exact match found zero overlapping tags → task assigned to a generalist instead

**Fix:** Replaced exact-string matching with a single budget-tier LLM call that semantically matches all tasks to all engineers. The LLM naturally understands that a frontend developer should handle React work. Falls back to exact-match if the LLM call fails.

**Rule:** When matching involves human-readable concepts (skills, roles, domains), use LLM-based semantic matching. Reserve exact-string matching for machine identifiers (IDs, enums, status codes).

---

## 33. Per-PR Rework Counting Causes Premature Exhaustion

**Lesson:** Rework cycles were tracked globally per PR (one counter for all reviewers). With `MaxReworkCycles = 3` and 3 reviewers, the engineer could exhaust all cycles with just one reviewer's feedback, leaving other reviewers unable to request changes.

**What happened:**
- Architect requested changes → rework attempt 1
- SE requested changes → rework attempt 2  
- PM requested changes → rework attempt 3 → limit reached, force-approval
- But each reviewer only got ONE round of feedback addressed

**Fix:** Changed tracking to per `(PR, reviewer)` pairs. Each reviewer gets their own independent cycle limit (default: 1). A PR with 3 reviewers gets up to 3 total rework rounds. Reviewer-specific limits use config: `MaxArchitectReworkCycles`, `MaxPmReworkCycles`, `MaxReworkCycles` (SE default), `MaxTestReworkCycles`.

**Rule:** When limiting retries in multi-party workflows, track limits per participant, not globally.

---

## 34. Blank Screenshots from Unstyled Placeholder Components

**Lesson:** AI-generated scaffold code creates placeholder components like `<div>Heatmap placeholder</div>` with no CSS styling. The Blazor app compiles and runs, but the page renders as a blank white screen because there's no background color, border, or visible formatting. Playwright screenshots capture a white image, and the SE reviewer can't tell if the page is broken or just unstyled.

**What happened:**
- Foundation PR created valid components with placeholder text
- `dotnet run` succeeded, app responded on its port
- Playwright screenshot showed a completely blank white image
- SE reviewer couldn't distinguish "working but unstyled" from "broken"

**Fix:** Updated scaffold prompts (both SE plan and engineer step-1) to require visually distinct placeholders: colored backgrounds, dashed borders, padding, and bold labeled text. Added a `.placeholder` CSS class specification. Screenshots should now show a clear grid of labeled sections.

**Rule:** For web/UI projects, placeholder components must be visually verifiable. "Valid but invisible" is not good enough for automated screenshot review.

---

## 35. Don't Gitignore Data Files — They Break Screenshots and Clones

**Lesson:** AI-generated `.gitignore` files often exclude `data.json` (treating it as user-specific or sensitive). But for dashboard apps, `data.json` is the app's required input — without it, the app shows an error page or blank screen, producing misleading screenshots.

**What happened:**
- Scaffold PR created `.gitignore` with `data.json` excluded
- `data.json` was created locally but not committed
- When Playwright checked out the branch and ran the app, `data.json` was missing → error page
- PlaywrightRunner had a workaround (copy `data.template.json` → `data.json`) but it didn't always find the template

**Fix:** 
1. Updated gitignore prompt rule: explicitly instruct "Do NOT gitignore data files"
2. Removed `.gitignore` from reset preserve list — scaffold PR creates it fresh
3. Removed hardcoded `.gitignore` preservation from `ConfigurationService.cs` reset logic

**Rule:** Data files required for the app to function must be committed. Only ignore build artifacts, secrets, and user-specific config.


---

# April 2026 Session — Playwright Robustness, Comment Guards, Context Propagation

## 36. Port-Binding Bugs Are a Recurring Class — Unify the Launch Pipeline

**Lesson:** Port-binding bugs in AI-generated apps have broken UI tests **25+ times across prior sessions**. Each new project discovered a new pattern to hardcode a port: `app.Run("url")`, `ListenAnyIP`, `Configuration["urls"]`, `ConfigureKestrel` variants, `launchSettings.json` `applicationUrl`, hardcoded `builder.WebHost.UseUrls`. Chasing each variant with a new regex patch is a losing game.

**What happened:**
- Over many sessions, each Playwright failure triggered a targeted fix for whatever pattern that run used.
- The fixes accumulated into 6+ layers of regex-based source mutation inside `PlaywrightRunner`.
- Despite the layers, new generations kept finding untouched patterns.
- Rubber-duck critique identified the real problem: scattered fixes with no single verification point.

**Fix (PR 68618e0 + 409276d):**
- Introduced `LaunchVerifiedAppAsync` as the **single canonical launch path**. All callers (TE UI tests, SE screenshot capture, foundation smoke test) funnel through it.
- The pipeline runs: (1) patch known hardcoded-port patterns, (2) inject `--no-launch-profile` into `dotnet run` to bypass `launchSettings.json`, (3) start the process with `ASPNETCORE_URLS` env var, (4) poll the expected port, (5) accept **ANY** HTTP response (including 404, 500) as proof "the app is listening on this port," (6) if unhealthy, self-heal by killing the process, backing up `launchSettings.json` (`.playwright-bak`), deleting `bin/` + `obj/`, and retrying.
- **Do not strip `CreateBuilder(args)`** — it's required for configuration binding; earlier attempts to remove it broke DI.
- File renames/backups use a mutex so concurrent agents don't clobber each other's `.playwright-bak` files.

**Rule:** For any failure class that has recurred 3+ times with different surface symptoms, stop patching symptoms and consolidate to a single verified pipeline. The verification step (accept any HTTP response) is more valuable than any source-patching heuristic.

---

## 37. Layer Periodic Health Checks on Top of Event-Driven Ones

**Lesson:** Event-driven healing only runs when agents invoke UI tests. If the UI-test subsystem is broken (stale `.playwright-bak` files, missing browser binaries, port already held by a dead process), the system stays quietly broken between test invocations — sometimes for hours.

**What happened:**
- Playwright healing logic was added to `LaunchVerifiedAppAsync` (reactive).
- User flagged: "What if nothing invokes the launcher for an hour? The system could be dead and we wouldn't know."
- Evidence: several sessions had tests silently failing because a previous run left `.playwright-bak` files in place, corrupting the next launch.

**Fix (PR 68618e0):**
- Added `PlaywrightHealthService` (a `BackgroundService`) running every **5 minutes**.
- On each tick: sample the expected port range, clean up `.playwright-bak` files older than **1 hour**, validate Playwright browser binaries exist and are executable, log anomalies to the activity log.
- Reactive checks still run inside `LaunchVerifiedAppAsync`; the periodic service is the safety net.

**Rule:** For any critical invariant, have **both** reactive (on-demand) and proactive (periodic) verification. Event-only checks mean you discover breakage only when a user-triggered action exposes it.

---

## 38. Duplicate-Action Guards Are Mandatory for Multi-Agent State Transitions

**Lesson:** When multiple agents can react to the same state transition, you **will** get duplicate actions unless every agent checks state before acting.

**What happened (PR 4ea4e38 + 2e051c2):**
- PM posts `ready-for-review` comment when a PR is ready.
- Architect approves the PR and ALSO posts `ready-for-review`.
- Result: two identical comments on the PR, confusing downstream reviewers and triggering duplicate notifications.

**Fix:**
- Before posting any phase-transition comment, agents must check existing PR comments for a matching marker string.
- The comment includes a stable marker (e.g., `<!-- virtual-dev-team:ready-for-review -->`) so presence detection is exact, not fuzzy.
- Applied symmetrically across PM, Architect, and SE — not just the agent that caused the reported bug.

**Rule:** When adding a state-change side-effect to one agent, audit **every other agent** that can observe the same state and add the same guard. Duplicate-notification bugs are a symptom of asymmetric guards. The marker comment (HTML comment with stable ID) is the idiomatic implementation.

---

## 39. Re-Inject Source Artifacts at Every Prompt Hop > 1

**Lesson:** Each prompt layer downstream of the source loses fidelity. If SE prompts only see the engineering plan's summary of the architecture, they hallucinate requirements that contradict the architecture itself.

**What happened (PR b00d00b):**
- Research → PMSpec → Architecture → EngineeringPlan → SE task PR. Five hops.
- SE implementations started diverging from architectural decisions because the engineering plan's summary had drifted.
- Specific bug: engineer generated a REST endpoint with a completely different response shape than Architecture.md specified, because the EngineeringPlan paraphrased it inaccurately.

**Fix:**
- SE implementation prompts now include the **full relevant sections** of Research.md, PMSpec.md, and Architecture.md — not just the engineering plan's summary.
- Added a validation pass: the engineering plan itself is checked against the design docs before PRs are created. Contradictions block plan approval.

**Rule:** Any prompt more than **one hop** from a source artifact should re-inject the source, not rely on the intermediate summary. Intermediate summaries are navigation aids for humans; LLMs should see the primary source.

---

## 40. Every GitHub API Call Must Assume the Target State Has Changed

**Lesson:** A cluster of bugs all shared the same root cause: code assumed a GitHub resource was in the state it was in when the agent first observed it. Between observation and action, humans, other agents, or retries mutate state.

**What happened (PRs 522d429, dde0cdd, and related):**
- `MarkDoneAsync` crashed with HTTP 422 when the issue was already closed (closed by a human between the agent's read and write).
- Inline review comments were **lost** when posting a PR comment on one's own PR returned 422 — the code threw and bailed before falling back to a regular comment.
- Infinite recursion in the test-removal loop when the same test kept re-appearing after "removal" because the removal wasn't idempotent.

**Fix:**
- `MarkDoneAsync`: treat "already closed" as success, not failure. Catch `ApiException` with 422/409/404 and inspect the current state.
- Own-PR comment path: on 422, fall back to posting an issue comment with the same body so review content is never lost.
- Test removal: check for actual change in the post-state, break the loop if no progress after N iterations.

**Rule:** Idempotent success conditions are the default: "already in the desired state" = success, not failure. Catch-and-fallback for 422/409/404 is mandatory, not optional. Never throw from a "did we complete the side-effect?" function — return a richer result type.

---

## 41. Surface AI Reasoning in the UI, Not Just the Logs

**Lesson:** When an AI evaluates an artifact (screenshot, code, design), the human triaging failures needs to see *what the AI thought it saw* at a glance — not dig through log files.

**What happened (PR 13ac013):**
- Dashboard cards showed screenshots but not the AI's description of them.
- When a PR was rejected "due to screenshot issues," the human had to open logs, find the relevant AI call, and read the description to understand why.
- Triage time per failed PR was 3-5 minutes just to locate the reasoning.

**Fix:**
- Dashboard cards now render the AI-generated screenshot description inline.
- Description is persisted alongside the screenshot artifact, not re-derived.

**Rule:** If an AI's judgment drives a decision, surface the one-paragraph "why" in the UI next to the artifact. This is not a nice-to-have — it's the difference between 30-second and 5-minute triage.

---

## 42. Partial-Reset Scripts Dramatically Speed Up Late-Stage Debugging

**Lesson:** A full pipeline reset re-runs Research → PMSpec → Architecture (20-40 minutes, significant token cost) before reaching the engineering phase where the bug actually lives. For late-stage debugging, this is a massive waste.

**What happened:**
- Debugging the engineering/testing phases required 20+ iterations per session.
- Each iteration required a fresh reset to reproduce, burning 30+ minutes and dollars of token spend on phases that were already validated.

**Fix (`scripts/minimal-reset.ps1`):**
- Preserves `Research.md`, `PMSpec.md`, `Architecture.md`.
- Clears engineering artifacts (PRs, issues, workspace directories, SQLite activity log).
- Pipeline fast-forwards to the `EngineeringPlanning` phase on next start.

**Rule:** For any multi-phase pipeline with expensive upstream phases, provide a partial-reset option that preserves phases that are known-good. Full reset remains available for clean-slate runs.

---

## 43. MCP Server Auth Changes Require Process Restart

**Lesson:** Running `cli-mcp auth` (or equivalent) successfully does **not** make a running MCP server pick up the new credentials. The server continues returning "No cached credentials" until it's restarted.

**What happened:**
- `cli-mcp auth` completed successfully, user confirmed token stored.
- All subsequent MCP calls returned `No cached credentials` errors.
- Wasted ~20 minutes debugging before the restart hypothesis was tested.

**Fix / workaround:**
- Documented in the session notes: after MCP `auth` commands, restart the host (VS Code, Copilot CLI) to reload the MCP server.
- Candidate improvement: MCP servers should hot-reload credentials from the store on each request, or expose a `reload-credentials` RPC.

**Rule:** Assume cached-credential MCP servers require a full restart after auth changes. If you're debugging "credentials should work but don't," restart first, investigate second.

---

## 44. Centralize Model Version Strings to a Single Constant

**Lesson:** Upgrading `claude-opus-4.6 → claude-opus-4.7` required edits in **8+ files**: `appsettings.json`, `VirtualDevTeamConfig.cs`, `ConfigWizard.cs`, `ModelRegistry` allowlist, `ModelPricing.cs`, `Configuration.razor`, `copilot-instructions.md`, `Requirements.md`. Every missed location causes a runtime allowlist rejection or incorrect cost math.

**What happened:**
- First pass of the upgrade missed `ModelPricing.cs`, resulting in `$0` cost calculations for runs using the new model.
- Second pass missed `ConfigWizard.cs` defaults, so new installs kept defaulting to the old model.
- Each miss required a targeted fix and re-validation.

**Fix (next time):**
- Introduce `ModelDefaults.PremiumModel`, `ModelDefaults.StandardModel`, etc. as `public const string` references.
- All config files reference the constant by key name; all code paths read from the single source.
- Next model upgrade becomes a one-line change plus a pricing-table entry.

**Rule:** Any string that appears in 3+ files and represents a versioned external identifier must live in a single `const` declaration. Scattered model/version strings are a maintenance tax that compounds with every upgrade.

---

## 45. Rubber-Duck Critique Between Plan and Implementation Prevents Over-Engineering

**Lesson:** Critique agents are most valuable **between plan approval and implementation start** — not after the code is written. Post-hoc critique finds bugs; pre-implementation critique prevents entire architectural detours.

**What happened:**
- Initial Playwright robustness plan proposed **6 layers of regex + file mutation** to chase every hardcoded-port pattern an AI might generate.
- A rubber-duck critique agent pushed back: "Why are you pattern-matching source code? The proof is whether the app answers HTTP. Verify the outcome, not the input."
- Revised plan: single unified launch pipeline (`LaunchVerifiedAppAsync`) + "any HTTP response = listening" check + self-heal loop.
- Final implementation was ~40% smaller and more reliable than the original plan.

**Rule:** Insert a critique gate between planning and implementation for any non-trivial change. The critique prompt should explicitly ask "is there a simpler invariant we could verify instead of enumerating all failure modes?" Post-implementation critique still has value for correctness, but architectural simplification has to happen before the code is written.

---

# April 2026 Session — Strategy Framework val-e2e

## 46. `Configure<T>.Bind` Appends to Collection Defaults — It Does Not Replace

**Lesson:** `IConfiguration.Bind` / `services.Configure<T>` calls the GETTER of a `List<T>` property and `.Add`s bound items. If the C# default initializer already populates the list (`public List<string> EnabledStrategies { get; set; } = new() { "baseline", "mcp-enhanced" };`), binding `["baseline","mcp-enhanced"]` from `appsettings.json` produces a 4-item list, not a 2-item list.

**What happened:**
- `StrategyFrameworkConfig.EnabledStrategies` defaulted to `["baseline","mcp-enhanced"]`.
- `appsettings.Development.json` specified `["baseline","mcp-enhanced"]` (matching the intent).
- At runtime the orchestrator saw 4 enabled strategies and logged `Orchestrating 4 strategies` — each strategy ran twice.
- Val-e2e surfaced it immediately because the dashboard showed duplicate candidate rows per run.

**Fix:** Defensive `.Distinct(StringComparer.OrdinalIgnoreCase)` in `StrategyOrchestrator.RunCandidatesAsync` on the enabled list. Kept the default initializer (a dependent unit test relies on it).

**Rule:** For any `List<T>` / `IList<T>` / `IEnumerable<T>` options property, **either** (a) initialize the list empty and require config to populate it, **or** (b) apply a dedup (`.Distinct()` or `.Where(...).ToList()`) at the consumer. Never assume the configured list "replaces" the default — it doesn't.

---

## 47. `.git/config.lock` Races Invisibly Under Parallel `git worktree add`

**Lesson:** Parallel `git worktree add` calls against the same source repo race on `.git/config.lock` during the pre-add phase (when git writes `extensions.worktreeConfig` and reads repo-level config). The failure mode is a cryptic `warning: unable to access '.git/config': Permission denied; fatal: unknown error occurred while reading the configuration files` — with zero mention of "lock" in the message.

**What happened:**
- Two candidate strategies launched in parallel from `StrategyOrchestrator.RunCandidatesAsync`.
- Both called `GitWorktreeManager.CreateAsync` on the same `agentRepoPath`.
- Race condition: one process holds `.git/config.lock`, the other fails with permission-denied cascading errors.
- One candidate silently lost its worktree; the other succeeded. Orchestration proceeded with only one survivor.

**Fix:** Static `ConcurrentDictionary<string, SemaphoreSlim>` keyed by repo path in `GitWorktreeManager`. Wrap the **pre-add phase only** (prune + `git config extensions.worktreeConfig` + `git worktree add`) in `await repoLock.WaitAsync(ct)`. Post-add, each candidate writes to its own per-worktree `config.worktree` file, so parallel `ExecuteAsync` runs stay fully concurrent.

**Rule:** Git's "worktree is fully parallel" promise has fine print: **the add itself is serialized per repo**. Execution in the worktree is parallel. Any code that calls `git worktree add` from multiple threads/tasks must synchronize at repo granularity.

---

## 48. Emit `Completed(false)` Synchronously on `Started` Path Failures — Never Let Exceptions Propagate to `Task.WhenAll`

**Lesson:** When an orchestrator fans out N tasks via `Task.WhenAll` and each task emits `Started`/`Completed` events, **every `Started` MUST have a matching `Completed` — even on the exception path**. Letting an exception propagate out of one task aborts the whole `WhenAll`, leaves state-store records stuck at `Running`, and corrupts dashboards that filter by state.

**What happened:**
- `StrategyOrchestrator.RunOneAsync` emitted `CandidateStarted` before calling `_worktree.CreateAsync`.
- If `CreateAsync` threw, the exception bubbled up to `Task.WhenAll`, which aborted sibling candidates.
- `CandidateStateStore` never saw a `Completed` event, so the orphaned candidate sat at `state=Running` forever.
- Dashboard's "active runs" query kept showing the orphan. Restart didn't clear it (checkpoint recovery preserved the stuck state).

**Fix:** Inner `try`/`catch` around `CreateAsync`. Synthesize a failed `StrategyExecutionResult`, emit `CandidateCompleted(succeeded=false, reason="worktree-create: {ex.Message}")`, and return a non-faulted tuple. `WhenAll` sees all N tasks as completed; sibling candidates run to completion; state store sees matching Started/Completed pairs.

**Rule:** Any `Started → Completed` event pair in fan-out code must be paired via `try/finally` or an explicit `try/catch` that synthesizes a failure result. Never rely on the exception path to reach the Completed emitter. Regression test: concurrent + one forced failure + assert sibling tasks completed successfully + assert zero orphans in state store.

---

## 49. val-e2e: Close Open PRs Before Live Runs — Checkpoint Recovery Bypasses New Features

**Lesson:** `SoftwareEngineerAgent` has two independent code paths: (1) resume-existing-PR via `StateStore.LoadAgentTaskCheckpointAsync`, which goes to `single-pass for continued implementation` and **bypasses any new feature added behind a flag**, and (2) fresh-task-assignment, which goes through the new feature. A stale open PR from a prior partial run will route to path 1 and silently defeat the new feature under test.

**What happened:**
- Twice in a row, val-e2e runs appeared to "ignore" `StrategyFramework.Enabled=true`.
- Root cause: both runs had a lingering open PR (from the previous partial run that was stopped mid-orchestration). Checkpoint recovery found it and took the resume path.
- No log line said "bypassing Strategy Framework" — the symptom was just that `/api/strategies/recent` was empty and no ndjson was written.

**Rule:** Before any live validation run of a feature-flagged path, enumerate open PRs and close them (script it — `scripts/close-pr-<n>.ps1`). Assume checkpoint-recovery paths will bypass your feature flag unless you've explicitly audited them. Better fix long-term: route both SE code paths through the same feature-flag gate.

---

## 50. Copilot CLI Doesn't Report Tokens — Cost Attribution Is `$0` Until API-Key Fallback

**Lesson:** The `copilot` CLI binary does not emit usage/token counts in its output. Any cost-tracking infrastructure built on top of it (per-agent budgets, per-strategy cost attribution, cost-based routing) resolves to `$0` and does not fire its enforcement paths. This is a **correctness-adjacent** limitation: the code looks right, the numbers are just always zero.

**What happened:**
- `StrategyOrchestrator` calls `_budget.Charge` and `_usage.RecordStrategyTokens` after each candidate run.
- With the default Copilot CLI provider, `exec.TokensUsed=0`, so both calls are no-ops.
- `/api/strategies/cost` permanently returns `$0` totals.
- For EMU-pool users this is fine (Microsoft pays the pool), but it means the "cost premium justified" success criterion in the original Interactive CLI Plan can't be measured without switching tiers to an API-key provider (Anthropic/OpenAI/Azure OpenAI direct).

**Rule:** When a provider doesn't report cost data, **document it at the config/README/requirements layer**, don't silently report zeros as if the budget worked. Dashboards should show "N/A — provider does not report" when usage=0 and provider=CopilotCli, to avoid false confidence.

---

## 51. Experiment Data Paths: Relative Paths Resolve Against Runner Cwd (Bin Dir), Not Repo Root

**Lesson:** In `dotnet run --no-build` scenarios, `Environment.CurrentDirectory` is the runner's `bin/Debug/net8.0/` directory, not the repo root. Any relative config path (e.g., `ExperimentDataDirectory = "experiment-data"`) resolves there. Users looking in the repo root see "missing" artifacts that are actually one directory level down in `bin/`.

**What happened:**
- Val-e2e validated the framework end-to-end, but my first `ls` on `experiment-data/` at repo root was empty.
- Panic moment — "did ndjson not write?"
- Actually written fine, just to `src/VirtualDevTeam.Runner/bin/Debug/net8.0/experiment-data/20260419T231321Z.ndjson`.

**Rule:** Either resolve relative paths against `IHostEnvironment.ContentRootPath` in service constructors, or set absolute paths in `appsettings.json`. Document the behavior loudly for anyone debugging "missing" artifacts.

---

# Late April 2026 Session — SinglePRMode, Strategy Screenshots, Review Workflow

## 52. SinglePRMode Task Leak — `ValidateEnhancementCoverageAsync` Must Respect Mode

**Lesson:** In SinglePRMode, the SE creates a single monolithic task (T1) covering all enhancements. But `ValidateEnhancementCoverageAsync` ran unconditionally and checked whether each enhancement had a task with a matching `ParentIssueNumber`. T1 only stored the FIRST enhancement's number as its `ParentIssueNumber`, so the remaining enhancements appeared "uncovered" and the LLM created phantom tasks T2–T7, defeating the purpose of SinglePRMode.

**What happened:**
- SE entered SinglePRMode and created T1 with `ParentIssueNumber` pointing to enhancement #1.
- `ValidateEnhancementCoverageAsync` iterated all 7 enhancements and found only #1 covered.
- The method asked the LLM to generate "MISSED" tasks for #2–#7.
- The system now had 7 tasks — identical to multi-PR mode — and created 7 PRs.

**Fix:**
- Skip validation entirely in SinglePRMode at the call site.
- Defense-in-depth: added inner guard inside `ValidateEnhancementCoverageAsync` itself to early-return when SinglePRMode is active.
- Added `RelatedEnhancementNumbers` collection field to `EngineeringTask` so T1 can express multi-enhancement coverage without relying solely on the scalar `ParentIssueNumber`.

**Rule:** When a feature has a "single vs. multi" mode toggle, EVERY downstream validation must check that toggle. A method that creates work items must be guarded by mode checks at BOTH the call site and inside the method itself (defense-in-depth). Data models must support the cardinality of the mode — if one task covers N enhancements, the model needs a collection field, not just a scalar.

---

## 53. Per-Candidate Strategy Screenshots — Capture at Build Gate, Not at Winner Selection

**Lesson:** The strategy framework runs multiple code-generation approaches (baseline, MCP-enhanced, agentic-delegation) and picks a winner. Originally, only the winner got a screenshot via `MarkReadyForReviewWithScreenshotAsync`, so the dashboard gallery showed "Capturing…" spinners for non-winners forever.

**What happened:**
- Three candidates ran through `CandidateEvaluator.RunGatesAsync` — build, test, screenshot gates.
- Only the winner was passed to the screenshot capture step after selection.
- Losing candidates' dashboard tiles permanently displayed spinner placeholders.
- No visual comparison between strategies was possible.

**Fix:**
- Capture screenshots in `CandidateEvaluator.RunGatesAsync` right after the build gate passes — at that point the scratch worktree has the candidate's code applied and built.
- Store bytes on `CandidateResult.ScreenshotBytes`.
- After winner selection, commit ALL candidates' screenshots to `.screenshots/pr-{N}-{strategyId}.png`.
- Write `<!-- winner-strategy: {key} -->` in PR body for dashboard winner detection.

**Rule:** Capture artifacts at the point of maximum information (post-build worktree), not at the point of decision (winner selection). Losers' artifacts are valuable for comparison and debugging. Also: when a helper like PlaywrightRunner mutates its `WorkspaceConfig` internally, always clone the config before calling.

---

## 54. Dashboard Strategy Key Mismatch — Use Canonical IDs Everywhere

**Lesson:** The dashboard hardcoded strategy key `"agentic"` but the actual strategy class's `Id` property returned `"agentic-delegation"`. This caused the agentic tile to never match its screenshot URL, rendering a permanent placeholder.

**What happened:**
- `StrategyTile.razor` used a hardcoded string `"agentic"` to build screenshot URLs.
- The `AgenticDelegationStrategy` class returned `Id = "agentic-delegation"`.
- Screenshot files were saved as `pr-42-agentic-delegation.png`.
- The tile looked for `pr-42-agentic.png` — file not found, permanent spinner.

**Rule:** Strategy IDs should be sourced from one canonical location (the strategy class's `Id` property) and propagated through the entire pipeline — never hardcoded in UI code. A simple constant or enum shared between strategy classes and UI components would prevent this class of bug.

---

## 55. Own-PR Review Downgrade Loses Inline Comment Positions

**Lesson:** When using a single PAT, GitHub's API rejects `REQUEST_CHANGES` and `APPROVE` review events on your own PRs. The fallback code downgraded to a `COMMENT` event but concatenated inline comments into the review body text instead of keeping them as per-line review comments, losing their file/line positions.

**What happened:**
- Test Engineer submitted a review with 5 inline comments on specific file locations.
- GitHub rejected `REQUEST_CHANGES` because the PAT owner authored the PR.
- Fallback logic caught the 422 and re-submitted as `COMMENT`, but built the body by joining comment text, discarding the `path` and `line` fields.
- All 5 comments appeared as a single block in the Conversation tab instead of on the Files-changed tab.

**Fix:**
- Use `COMMENT` event type for ALL reviews (which GitHub allows on own PRs) and include inline comments in the review payload's `comments` array.
- GitHub renders them on the Files-changed tab even for `COMMENT` reviews.

**Rule:** The single-PAT setup is a fundamental constraint that affects review workflows. Test the full review pipeline with the actual PAT permissions, not just with mocked GitHub responses. `COMMENT` events are the safe universal path for inline comments.

---

## 56. Wave Ordering Collisions — Hash-Based IDs Prevent Task Drops

**Lesson:** Sequential task IDs caused collisions when multiple waves of tasks were assigned concurrently during rate-limit recovery. Tasks from later waves overwrote earlier wave tasks in the cache, silently dropping work.

**What happened:**
- Rate-limit recovery triggered two waves of task assignment simultaneously.
- Both waves used a sequential counter starting from the same base (e.g., T1, T2, T3).
- Wave 2's T1 overwrote Wave 1's T1 in the task cache.
- Three tasks from Wave 1 were silently dropped — no error, no log, just missing PRs.

**Fix:**
- Use collision-safe hash-based task IDs (content-addressed from task title + enhancement number + timestamp).
- Merge (not replace) cache entries on API delay recovery, preserving both waves' tasks.

**Rule:** Any ID generation scheme used in concurrent workflows must be collision-resistant. Sequential counters are dangerous when multiple producers run in parallel. Content-addressed or UUID-based IDs eliminate this class of bug entirely.

---

## 57. Premature Enhancement Closure After Mini-Reset — Guard Against Vacuously True Conditions

**Lesson:** When checking "are all PRs merged?" by testing `openPRs.Count == 0`, a freshly-reset repo with zero PRs satisfies the condition vacuously. This caused PM to close all enhancement issues immediately on startup after a mini-reset.

**What happened:**
- After a mini-reset (which closes all PRs and issues), the runner started fresh.
- PM's SinglePRMode closure logic checked `openPRs.Count == 0` — trivially true because no PRs existed yet.
- PM immediately closed all newly-created enhancement issues and declared "all reviews complete — all merged."
- This broke a live demo — the user saw issues closing within seconds of creation.

**Fix:**
- Added a positive-evidence guard: `GetMergedPullRequestsAsync().Count > 0` — there must be at least one actually-merged PR. Applied to both the "all merged" status declaration and the enhancement issue closure path.
- Same pattern: never treat an empty set as proof of completion. Require at least one positive example.

**Rule:** Boolean conditions that check "nothing is X" (e.g., no open PRs) are dangerous in bootstrapping scenarios where "nothing exists at all" is also true. Always pair them with a positive-evidence check (e.g., at least one merged PR exists). This is the multi-agent equivalent of the "vacuous truth" trap in logic.

---

## 58. In-Memory State Flags Lost on Process Restart — Recover from Durable State

**Lesson:** Flags like `_allTasksComplete`, `_integrationPrCreated`, and `_engineeringSignaled` existed only in memory. When the runner restarted without a reset, these were lost, causing the SE to re-create tasks and PRs that already existed.

**What happened:**
- Runner was killed and restarted during a demo (no reset script).
- SE lost all in-memory progress flags.
- SE re-created T1 and T-FINAL engineering tasks as new GitHub issues (#2312, #2313) and opened duplicate PR #2314.
- The duplicate artifacts confused the review pipeline and required manual cleanup via REST API.

**Fix:**
- Added a state recovery block in `CreateEngineeringPlanAsync` after `LoadTasksAsync` restores tasks from GitHub issues.
- Recovery logic: (1) if all non-integration tasks are Done → `_allTasksComplete = true`, (2) scan merged+open PRs for integration PR → `_integrationPrCreated = true`, (3) if 0 open PRs + merged PRs exist → `_engineeringSignaled = true`.
- Each recovery is logged at Information level with evidence (PR numbers, task counts).

**Rule:** Every in-memory flag that controls "should I do X?" must either be (a) persisted to durable storage, or (b) recoverable from existing durable state (GitHub issues, PRs, labels) on startup. If neither is true, a process restart will cause duplicate work. The recovery approach is usually cheaper than persistence because the durable state already exists — you just need to read it.

---

## 59. EMU GitHub Restrictions — `gh` CLI Fails for Enterprise Managed Users

**Lesson:** Enterprise Managed User (EMU) accounts cannot use the `gh` CLI for certain operations (closing PRs/issues) due to GraphQL restrictions. REST API with PAT works as a fallback.

**What happened:**
- Needed to close duplicate PRs and issues created during a restart.
- `gh pr close` and `gh issue close` failed with authentication/permissions errors specific to EMU accounts.
- The `.NET User Secrets` store contained a valid PAT that worked with REST API calls.

**Fix:**
- Use `curl` or `Invoke-RestMethod` with the PAT from `dotnet user-secrets list` as the workaround.
- Token extraction pattern: `$secrets = dotnet user-secrets list ...; $token = (($secrets | Select-String "GitHubToken") -split "= ")[1].Trim()`

**Rule:** Don't assume the `gh` CLI works in all enterprise environments. Always have a REST API fallback path for critical operations. Store the PAT extraction pattern in your operational runbook.

---

## 60. First Successful End-to-End Run — What Made It Work

**Lesson:** The first complete end-to-end run (all PRs merged, all issues closed, Completion phase reached) succeeded because of the cumulative effect of 6+ targeted fixes, not any single change.

**What happened:**
- Prior runs failed at various stages: premature issue closure, stuck review gates, SE duplicates on restart, stale status displays, missing TeamMembers.md entries.
- Session committed 3 fix batches: (1) `bc37be7` with 6 fixes (issue closure, SE stale status, step tracking, TeamMembers), (2) `f7eff0f` fixing premature closure, (3) `c751e49` fixing SE restart recovery.
- The successful run: PM created enhancements → SE created PR #2357 → TE tested → PM approved → SE merged → PM closed all issues → Completion phase.

**Fix:** N/A — this was a cumulative success. The lesson is that multi-agent systems fail in combinatorial ways. Each individual fix seems small, but their interaction effects are what make the system work. You need to fix ALL the failure modes in a single pass, not just the most recent one.

**Rule:** Multi-agent orchestration systems have emergent failure modes that only surface when all agents interact end-to-end. A "fix → monitor → fix" loop with live runs is essential. Plan for 3-5 fix-and-run cycles to get from "mostly works" to "reliably completes."

## 61. External Agentic Framework Integration — Spike Before You Abstract

**Problem:** We wanted to integrate Squad (and eventually Claude Code, GitHub Copilot Agent, etc.) as pluggable coding frameworks under VirtualDevTeam's orchestration. The natural instinct was to start by designing the adapter interfaces and abstraction layer — but that would have been building on unverified assumptions.

**What went wrong (avoided):** A rubber-duck critique caught the "abstract first" anti-pattern before we committed to it. Without a feasibility spike, we would have designed interfaces around assumed capabilities (e.g., expecting `.squad/log/` files for telemetry, expecting token reporting, assuming clean containment) — all of which turned out to be wrong.

**Key discoveries from the feasibility spike:**
- Squad's `.squad/log/` and `.squad/orchestration-log/` directories are **NOT created** during headless execution — stdout is the ONLY real-time telemetry source.
- Squad contaminates far beyond `.squad/`: also creates `.copilot/` (37 files), `.github/agents/` (82KB), `.github/workflows/` (4 files). The exclusion list had to be much larger than expected.
- Token metrics ARE parseable from stdout (`Tokens ↑ 620.4k · ↓ 3.2k`) — we originally assumed $0 cost attribution for external frameworks.
- Pre-populating `.squad/team.md` bypasses Squad's interactive Init Mode — enabling true headless execution.
- `TokensUsed = 0` (the default for unknown) biases the evaluator in favor of frameworks that can't report tokens. This had to be fixed (nullable `long?`) BEFORE adding Squad.

**Architecture decisions that worked:**
1. **Composable interfaces** (IAgenticFrameworkAdapter + optional IFrameworkLifecycle + optional IFrameworkTelemetrySource) instead of one fat interface. Adapters only implement what their framework supports.
2. **Wrapping existing strategies** (BaselineAdapter, McpEnhancedAdapter) as adapters enables gradual migration — old `ICodeGenerationStrategy` code paths keep working.
3. **External adapter filtering** in the orchestrator: adapters with IDs matching built-in strategies are excluded from `_externalAdapters` to prevent double-execution.
4. **Pre/post execution gates** for external frameworks: log the sandbox summary before running, log the result metrics after. Built-in strategies skip gates (they're already fully visible).

**Fix:** Always spike first. Run the framework in a real environment, inspect every output, file, and side effect before designing interfaces. The abstractions should fit the reality, not the documentation.

**Rule:** When integrating external tools into an orchestration system: (1) prove headless execution works, (2) audit all side effects and containment requirements, (3) map actual telemetry sources (not assumed ones), (4) fix any evaluation biases before adding new competitors, (5) THEN design the minimal abstraction that fits what you've proven. Defer mass renaming and UI polish until the integration is working end-to-end.

## 62. Standalone Dashboard DI Must Mirror Runner Registrations

**Problem:** VirtualDevTeam has two hosting modes — the Runner (full orchestration host) and the standalone Dashboard Host (lightweight UI-only mode on port 5051). They have separate DI registration paths: `AddStrategyFramework()` in the Runner vs `AddStandaloneStubs()` in `StandaloneServiceRegistration.cs`. Every time a new service is registered in the Runner's DI container and consumed by a Dashboard page (e.g., via `ServiceProvider.GetService<T>()`), the standalone Dashboard will fail at runtime because the service was never registered there.

**What went wrong (repeatedly):**
- `SquadReadinessChecker` was registered in `AddStrategyFramework()` (Runner-side), but the Configuration page calls `ServiceProvider.GetService<SquadReadinessChecker>()`. When the standalone Dashboard tried to resolve it, it returned null and the UI showed an error.
- This is a recurring pattern, not a one-time mistake. The same class of bug has appeared with other services that were added to the Runner but forgotten in the standalone Dashboard registration.
- The failure only surfaces at runtime when someone clicks the relevant UI element — there's no compile-time or startup-time check that catches missing registrations.

**Why it's easy to miss:**
1. The Runner and Dashboard share the same Blazor component library (`VirtualDevTeam.Dashboard`), but have different DI containers.
2. When developing against the Runner (which registers everything), the pages work perfectly — the bug only appears in standalone mode.
3. `GetService<T>()` returns null silently instead of throwing, so the failure manifests as a UI error message rather than a crash with a clear stack trace.

**Fix:** When adding ANY new service that a Dashboard page or component might resolve — either directly via `GetService<T>()` or through constructor injection — you MUST also register it (or a stub/mock) in `StandaloneServiceRegistration.AddStandaloneStubs()` (`src/VirtualDevTeam.Dashboard.Host/StandaloneServiceRegistration.cs`).

**Rule:** Treat the standalone Dashboard DI container as a first-class deployment target. Every new feature that adds a service registration consumed by Blazor components requires a corresponding entry in `StandaloneServiceRegistration.cs`. Add this as a mandatory step in the implementation checklist for any feature that touches DI.

---

## 62. NEVER Put Secrets in Tracked Config Files

**Severity: CRITICAL — Security**

**The near-miss:** During a debugging session, a Copilot CLI agent wrote the GitHub PAT directly into `src/VirtualDevTeam.Runner/appsettings.json` to fix a runner startup failure. The file is **tracked by git** (it is NOT in `.gitignore`). Had the next commit included this file, the PAT would have been pushed to the remote repository and exposed to anyone with read access.

**Why it happened:**
1. The runner crashed because `GitHubToken` was empty in appsettings.json (cleared during a mini reset).
2. The token existed in `dotnet user-secrets`, but user-secrets only load when `ASPNETCORE_ENVIRONMENT=Development`.
3. Starting the runner with `--no-launch-profile` skipped the launch profile that sets the environment variable.
4. Detached processes (via `setsid` or background shells) lose `$env:` variables set in the parent shell.
5. The "quick fix" was to write the token directly into appsettings.json — this is **always wrong**.

**The correct fix:**
- Add explicit user-secrets loading in `Program.cs` that works in ALL environments:
  ```csharp
  builder.Configuration.AddUserSecrets<Program>(optional: true);
  ```
- This single line ensures secrets are loaded regardless of `ASPNETCORE_ENVIRONMENT`, making detached/production-mode processes work correctly.

**Rules:**
1. **NEVER** write secrets, tokens, API keys, or credentials to any file tracked by git.
2. **ALWAYS** use `dotnet user-secrets` for sensitive configuration values.
3. If the runner can't find a secret at startup, fix the secret-loading code — do not put the secret in a tracked file.
4. Only `appsettings.Development.json` and `appsettings.Production.json` are gitignored. The base `appsettings.json` is **tracked and committed**.
5. Before writing any value to a config file, verify whether that file is tracked: `git ls-files <path>`. If it's tracked, the value must not be sensitive.

---

## 63. Strategy Results Must Survive Process Restarts — Persist to SQLite

**Lesson:** In-memory strategy/framework results (CandidateStateStore's ring buffer) are lost on every process restart. For a multi-hour competitive evaluation pipeline, losing results means re-running expensive AI work.

### What happened:
1. The `CandidateStateStore` used an in-memory `LinkedList` for recent completed tasks and a `ConcurrentDictionary` for active tasks.
2. Every runner restart wiped all historical framework comparison results — screenshots, scores, execution summaries, winner selections.
3. The dashboard showed nothing after restart even though prior runs had completed successfully.

### Root cause:
No persistence layer existed for strategy/framework results. The `AgentStateStore` (SQLite) had tables for agent state, messages, and workflow — but nothing for the strategy pipeline output.

### Fix applied:
1. Added `strategy_tasks` and `strategy_candidates` tables to `AgentStateStore.cs` with UPSERT semantics.
2. `CandidateStateStore` now accepts an optional `AgentStateStore` via constructor injection.
3. On construction, `HydrateFromSqlite()` loads the last 100 completed tasks into the ring buffer.
4. On each `PushRecent()` call, `PersistToSqlite()` saves the task and all candidates (including base64 screenshots and JSON execution summaries).
5. Persistence is **best-effort** — failures are logged but don't crash the pipeline.

### Key design decisions:
- **Screenshot storage**: Base64-encoded in SQLite TEXT column. Not ideal for large images but simple and self-contained.
- **Execution summaries**: Serialized as camelCase JSON in a TEXT column.
- **Trim on archive**: Active tasks hold up to 200 activity entries; trimmed to 50 when moved to recent buffer.

---

## 64. Capability-Based Interfaces Beat Monolithic Abstractions for Platform Providers

**Lesson:** When abstracting multiple platform backends (GitHub, Azure DevOps), splitting into many small capability interfaces (7 in our case) is far better than one `IPlatformService` monolith.

### What happened:
1. Initial design considered a single `IPlatformService` with ~60 methods covering PRs, work items, branches, files, reviews, info, and URL generation.
2. Rubber-duck critique identified this would force every provider to implement ALL methods (even ADO doesn't support work item deletion) and create a lowest-common-denominator trap.
3. Redesigned as 7 capability interfaces: `IPullRequestService`, `IWorkItemService`, `IRepositoryContentService`, `IBranchService`, `IReviewService`, `IPlatformInfoService`, `IPlatformHostContext`.

### Why this works better:
- **Incremental adoption**: Can implement `IPullRequestService` first and leave others as GitHub-only.
- **Interface segregation**: ADO's `DeleteAsync` returns `false` gracefully (no work item deletion API) without breaking the contract.
- **Testability**: Each capability can be mocked independently.
- **Config discovery**: `IPlatformInfoService.Capabilities` lets code check at runtime what the platform supports (e.g., `SupportsWorkItemDeletion`, `SupportsAtomicTreeReset`).

### Key design decisions:
- **GitHub adapters wrap, not replace**: The existing `IGitHubService` (97KB) stays untouched. GitHub adapters are thin wrappers that delegate to it, preserving all existing behavior.
- **ADO config is nested**: `DevPlatformConfig.AzureDevOps` is a separate class so GitHub config and ADO config coexist independently — switching platforms doesn't lose the other config.
- **Bearer token support**: For enterprises like Microsoft where PATs are restricted, `AzureCliBearerProvider` uses `az account get-access-token` with auto-refresh 5 minutes before expiry.

---

## 65. Never Use IGitHubService Directly for Agent Work Artifacts

**Lesson:** When agents create PRs, work items, or commit files, they must use the platform abstraction interfaces (`IPullRequestService`, `IWorkItemService`, `IRepositoryContentService`), never `IGitHubService` directly. Direct GitHub calls bypass ADO support entirely and create invisible platform lock-in.

### What happened:
1. Early agent code called `IGitHubService.CreatePullRequestAsync()` directly from `EngineerAgentBase`, `ProgramManagerAgent`, and `TestEngineerAgent`.
2. When ADO support was added, every direct `IGitHubService` call was a hard-coded dependency that had to be found and migrated to the capability interface equivalent.
3. Some paths were missed initially (review thread replies, screenshot URLs using `raw.githubusercontent.com`), causing subtle failures only visible during ADO end-to-end runs.

### Rule:
- **Agent-facing code** (anything in `VirtualDevTeam.Agents` or `VirtualDevTeam.Orchestrator`) must only use the 7 capability interfaces from `DevPlatform/Capabilities/`.
- **`IGitHubService`** is an implementation detail — only the GitHub adapter classes in `DevPlatform/Providers/GitHub/` should reference it.
- When adding new agent behaviors that touch PRs/work items/files, always code against the interface, not the concrete GitHub service.

---

## 66. DI Dual-Registration Pattern — Runner and StandaloneServiceRegistration Must Stay in Sync

**Lesson:** The Runner (`Program.cs`) and the standalone Dashboard (`StandaloneServiceRegistration.cs`) have independent DI registrations. When a new service is added to the Runner, it must also be registered in `StandaloneServiceRegistration` or the standalone dashboard will crash at runtime with missing service exceptions.

### What happened:
1. `DevelopSettingsService` was added to the Runner's DI container for the new Develop wizard page.
2. The standalone dashboard crashed on `/develop` because `StandaloneServiceRegistration` didn't register the service.
3. Similar issues occurred with `IWorkItemSearchService`, `IRepositoryManagementService`, and `IConfigurationService` — all needed by the Develop wizard.

### Rule:
- Every new service registered in `Program.cs` that is consumed by any Dashboard page must also be registered in `StandaloneServiceRegistration.cs`.
- The standalone dashboard uses `HttpDashboardDataService` (HTTP polling) instead of `DashboardDataService` (in-process). Some services need HTTP-proxied equivalents.
- When in doubt, search for `StandaloneServiceRegistration` after adding any new DI registration.

---

## 67. Task/Step Tracking Hierarchy — Tasks Are Groups, Steps Are Atomic

**Lesson:** The agent card display uses a two-level hierarchy: **Tasks** (named groups from `WellKnownTaskNames` or dynamically registered names) represent high-level activities, while **Steps** (individual `BeginStep`/`CompleteStep` calls) represent atomic operations within a task. The dashboard shows "Task: {name}" and "⚡ Step: {description}" separately.

### What happened:
1. Initially, only steps were tracked. The dashboard showed "Working: Building project…" which gave no context about *what* the agent was doing at a higher level.
2. Adding task-level grouping (e.g., "PM Specification", "Engineering Planning", "Code Review") gave the overview cards meaningful context.
3. The `WellKnownTaskNames` dictionary maps task IDs (like `pm-spec`, `pe-planning`, `te-rework`) to human-readable names for all core agent lifecycle phases.
4. When no step is active within a task, the card falls back to `StatusReason` for monitoring/waiting states.

### Design:
- `BeginTask(taskId)` / `CompleteTask(taskId)` — container-level tracking
- `BeginStep(name)` / `CompleteStep(name)` — leaf-level tracking with timing
- `RegisterTaskDisplayName(taskId, displayName)` — dynamic registration for PR-specific or work-item-specific task names
- Dynamic patterns: task IDs starting with `te-pr-` auto-format to "Test PR #N"; `se-task-` to "Engineering Task"
---

## 68. Concurrent Label Writes Cause Silent Overwrites — Always Re-Fetch Before Write

**Lesson:** When multiple agents concurrently modify PR labels via read-modify-write patterns, later writes silently overwrite earlier labels. The platform API replaces the entire label set, not individual labels.

**What happened:**
- TE added `tests-added` label to a PR.
- PM concurrently read the PR labels (before TE's write landed), added `pm-approved`, and wrote back.
- PM's write replaced the entire label set with its stale copy, dropping `tests-added`.
- The merge gate required BOTH labels and never triggered — pipeline stalled.

**Fix:**
- Created `PullRequestServiceExtensions.AddLabelAsync` that re-fetches the current labels *immediately* before writing.
- All agents now use this safe helper instead of raw label-set operations.
- The helper is idempotent (no-op if label already present) and handles both GitHub and ADO.

**Rule:** For any shared mutable resource accessed by multiple concurrent agents, always use "fetch-then-mutate" just before the write. Caching the resource state from an earlier read guarantees a stale-data overwrite.

---

## 69. Recovery Must Cross-Reference PRs and Tasks — In-Memory State Is Not Durable

**Lesson:** After a runner restart, in-memory task caches are rebuilt from the platform (ADO/GitHub) issue state. But `PullRequestNumber` is never stored in issue metadata — it only lives in memory. If the runner stops before `MarkDoneAsync` closes the issue, the task appears "Pending" on restart even when its PR is fully approved.

**What happened:**
- Runner stopped after PR #180 was approved (had `pm-approved`, `tests-added`, `architect-approved` labels).
- On restart, `LoadTasksAsync` fetched the still-open work item and mapped it to `Status = "Pending"`.
- SE agent picked up the "Pending" task and re-ran the strategy framework — wasting 15+ minutes of compute on duplicate work.

**Fix:**
- During `RecoverReadyForReviewPRsAsync`, cross-reference open PRs with past-implementation labels against the task cache.
- Match via three strategies in priority order: (1) `PullRequestNumber` match (runtime only), (2) linked work items via `GetLinkedWorkItemIdsAsync` (platform-agnostic), (3) exact title match.
- Call `MarkDoneAsync` to close the work item — subsequent `LoadTasksAsync` calls see it as closed → "Done".
- Set the recovery flag AFTER success (not before) to allow retry on transient API failures.

**Rule:** Any agent state that depends on in-memory-only fields (like PR↔task linkage) MUST have a recovery path that re-derives the state from durable platform artifacts. Never trust in-memory state to survive a process restart.

---

## 70. TE Must Guard Against PRs With Zero Changed Files

**Lesson:** The Test Engineer should never mark a PR as "tested" if it has zero changed files. This happens when the SE creates a PR but hasn't pushed code yet — the branch exists but contains no diff.

**What happened:**
- SE created a PR and branch, but hadn't pushed implementation code yet.
- TE picked up the PR (it had `ready-for-review` label), saw 0 changed files, but still added `tests-added` label.
- Downstream agents thought the PR was tested and tried to merge an empty PR.

**Fix:**
- Added a guard in TE's PR processing: if `changedFiles.Count == 0`, skip the PR entirely and do NOT add it to `_testedPRs`.
- TE will naturally re-encounter the PR on the next loop iteration once SE has pushed code.

**Rule:** When processing work artifacts from other agents, always validate that the artifact is in a meaningful state before acting on it. An empty diff, a PR with no commits, or a work item with no description are all signs the upstream agent hasn't finished yet.

---

## 71. Generic "AI Call in Progress" Status Is Useless for Monitoring

**Lesson:** Dashboard agent cards showing "AI call in progress" for every LLM call provides zero diagnostic value. Operators need to know *what* the agent is doing (e.g., "Creating architecture design", "Generating PMSpec — Pass 1"), not just that it's making an AI call.

### What happened:
- `ActiveLlmCallTracker.NotifyCallStarted()` accepted a `context` parameter, but all callers passed `null`.
- The dashboard fell back to `currentStep?.Name ?? "AI call in progress"` — often landing on the generic fallback.
- When an agent appeared stuck for 30+ minutes, the only visible status was "AI call in progress" — no indication of what it was trying to generate.

### Fix:
- Added `AgentCallContext.CurrentCallContext` (AsyncLocal) — agents set descriptive context before each LLM call.
- `CopilotCliChatCompletionService` passes `CurrentCallContext ?? ExtractCallContext(chatHistory)` to the tracker.
- `ExtractCallContext()` auto-extracts a truncated summary from the last user message in the chat history as a fallback.
- All agents (PM, Architect, Researcher, SE) now set explicit context like "Creating PMSpec — Pass 1", "Architecture design (single-pass)", "Generating code for: TaskTitle".

**Rule:** Every status message displayed to operators must provide actionable context. Generic status values like "Working", "Processing", or "AI call in progress" should always be accompanied by a specific description of *what* is being worked on.

---

## 72. Stale Local Gate Approvals Auto-Approve Subsequent Resources

**Lesson:** Gate approvals keyed only by `gateId` (not by resource) silently auto-approve all subsequent PRs/resources after the first dashboard approval, bypassing human review.

### What happened:
- `_localApprovals` in `GateCheckService` was keyed by gateId only (e.g., `"FinalPRApproval"`), not per-resource.
- User approved `FinalPRApproval` for PR #1 from the dashboard.
- `_localApprovals["FinalPRApproval"]` was set and NEVER cleared.
- When PR #2 hit `CheckGateAsync("FinalPRApproval")`, it found the stale approval and returned `Proceed` instantly — PR #2 auto-merged with NO human review.

### Fix:
- Made local approvals resource-scoped for multi-fire gates: key as `{gateId}:{resourceNumber}` when resource is present.
- Approvals are consumed (removed from `_localApprovals`) after the agent processes them.
- Global-key fallback preserved for single-fire gates (PMSpecification, ArchitectureDesign) that only fire once per run.

**Rule:** Any caching or approval mechanism that maps a decision to future actions MUST scope the decision to the specific artifact it applies to. Global decisions silently extend to unreviewed artifacts and break safety invariants.

---

## 73. Absolute Workspace Paths Break on Repo Rename or Move

**Lesson:** If `appsettings.json` contains an absolute workspace path (e.g., `C:\Git\VirtualDevTeam\src\VirtualDevTeam.Runner\.agents`), `WorkspaceConfig.ResolveRootPath()` detects it as already absolute and skips resolution — pointing to a stale location after the repository is renamed or moved.

### What happened:
- The repo was renamed from `VirtualDevTeam` to `VirtualDevTeam`, but `appsettings.json` still had the old absolute path.
- `ResolveRootPath()` checks `Path.IsPathRooted()` first — absolute paths bypass the `Path.Combine(CWD, ...)` resolution.
- Result: `.agents/` was created at `C:\Git\VirtualDevTeam\...` (the old location), not under the current repo.
- Additionally, three reset scripts hardcoded `C:\Agents` as a third different location.

### Fix:
- Changed `appsettings.json` to use relative `.agents` — `PostConfigure` resolves it against the Runner's CWD.
- Updated all reset scripts to read `Workspace.RootPath` from `appsettings.json` and resolve relative paths against `$RunnerDir`.
- All paths now converge to the same location: `{RunnerDir}\.agents`.

**Rule:** Configuration paths should always be relative. Absolute paths create invisible coupling to a specific machine layout that breaks silently on renames, moves, or clones to different machines.

---

## 74. develop-settings.json Is the Runtime Source of Truth, Not appsettings.json

**Lesson:** At runtime, project-specific settings (repo URL, auth method, gate preferences, work item mode) come from `develop-settings.json` — not from `appsettings.json`. Code that reads `IOptions<VirtualDevTeamConfig>` for project context may get stale defaults instead of the wizard-configured values.

### What happened:
- The Configuration page's "Repository Cleanup" section called `_config.Project.GitHubRepo` to display the active repo name.
- This read from `appsettings.json` static defaults, which showed a hardcoded test repo instead of the user's actual project.
- The Develop wizard stores settings in `develop-settings.json`, and `RunCoordinator.ReconfigureServicesAsync` applies them to the runtime `IOptions` — but only after a run starts.
- Before a run starts (or on the Configuration page before any run), `IOptions` still holds the static defaults.

### Fix:
- Injected `DevelopSettingsService` into the Configuration page.
- Read repo/branch from `develop-settings.json` directly for display purposes.
- When no `develop-settings.json` exists (no project configured), show an empty state with guidance instead of stale defaults.
- Blanked all project-specific defaults in `appsettings.json` to prevent any confusion.

**Rule:** When a system has both "static defaults" (committed config) and "dynamic user settings" (per-user/per-run), always clearly document which one wins at runtime. UI components should read from the same source that agents use, not from the static defaults.

---

## 75. Run Switching Requires Explicit Cancellation of Paused Runs

**Lesson:** When a user wants to start a completely different project, the system must explicitly cancel any paused run before allowing a new one. Simply creating a new run while one is paused leads to database conflicts, stale agent states, and confusing UI.

### What happened:
- A run was paused (agents stopped, state saved to SQLite).
- User tried to configure and start a completely different project through the Develop wizard.
- The final "Launch" step rejected the request: "Cannot start — a run is already paused."
- There was no way to abandon the paused run from the wizard — user had to go to Configuration → Repository Cleanup first.

### Fix:
- Added `RunCoordinator.CancelRunAsync()` which explicitly cancels paused runs.
- Develop wizard's Review page now detects paused runs and auto-cancels them before starting the new run.
- Previous run's SQLite database is preserved on disk (named by repo) for potential future resumption.
- Added cancel API at `/api/runs/cancel` for programmatic access.

**Rule:** Workflow systems that support pause/resume MUST also support explicit abandonment. Never block new work because old work is paused — provide a clear path to switch contexts while preserving the ability to return.

---

## 76. Framework Orphan Recovery — Clean Up Worktrees and Processes on Crash

**Lesson:** When the strategy framework crashes mid-evaluation, orphaned git worktrees and candidate processes persist on disk and can interfere with subsequent runs.

### What happened:
- A runner restart during strategy evaluation left orphaned git worktrees in the agent workspace.
- On the next run, worktree creation failed because paths already existed.
- "No winner" scenarios left candidates in an indeterminate state instead of being properly archived.

### Fix:
- Added orphan recovery logic that cleans up stale worktrees on startup.
- "No winner" scenarios now properly archive all candidates and emit clear status events.
- Framework processes are tracked and killed during graceful shutdown.

**Rule:** Any system that creates temporary resources (worktrees, child processes, temp files) must have a recovery path that cleans up orphans on startup. Crash-resilient systems assume crashes WILL happen and self-heal.

---

## 77. Agent Status Must Reflect Actual Work, Not Assumed Work

**Lesson:** Agent dashboard status should reflect what the agent is *actually* doing, not what it *should* be doing based on the current phase. Misleading status causes operators to wait for progress that will never come.

### What happened:
- The Test Engineer showed "Analyzing test coverage" when no complete PR existed to analyze — the SE was still implementing.
- SE 1 showed "Working (AI)" with a generic LLM call status when there was no task assigned to it — another engineer had taken the only PR.
- Both agents appeared busy to the operator, who waited 30+ minutes expecting progress before investigating.

### Fix:
- TE now guards against acting on PRs that have no changed files or aren't truly complete.
- SE agents that have no assigned task show an idle/waiting status instead of a misleading "working" state.
- LLM call context (lesson #71) ensures that when agents ARE working, the status describes the actual activity.

**Rule:** Dashboard status must never be aspirational — it must be factual. If an agent is idle because there's no work, say "Idle — no tasks available", not "Analyzing" or "Working". Misleading status is worse than no status because it prevents operators from diagnosing issues.

---

## 78. LLM Call Tracker Must Not Override Agent Status

**Lesson:** A secondary status overlay (like an active-LLM-call indicator) must never forcibly change the agent's canonical status. The overlay exists to *annotate* the status, not replace it.

### What happened:
- `AgentSnapshotService.RefreshLlmCallState()` forced the dashboard status to `Working` whenever `ActiveLlmCallTracker` reported an active LLM call for any agent.
- This masked `Idle`, `Blocked`, and other real statuses — agents appeared busy when they had no work.
- The same override existed in `ToSnapshot()`, applied on every initial snapshot creation.
- The dashboard already had a separate `🤖 AI` badge displaying `LlmCallElapsedTime` — the status override was redundant.

### Fix:
- `RefreshLlmCallState()` now only updates `LlmCallElapsedTime` without touching `Status` or `StatusReason`.
- `ToSnapshot()` sets `llmElapsed` for the badge but preserves `effectiveStatus` from the agent's actual state.
- The UI's `🤖 AI` badge (already implemented in `AgentOverview.razor`) shows LLM activity independently.

**Rule:** When two UI signals convey the same information (status text AND activity badge), pick one canonical source. Never let a secondary signal overwrite the primary. Prefer *additive* indicators (badges, icons, elapsed timers) over *substitutive* ones (replacing the status entirely).

---

## 79. Screenshot Capture Must Not Be Gated on Task-Type Heuristics

**Lesson:** Don't gate screenshot capture on a keyword heuristic like `IsWebTask`. The screenshot pipeline already handles non-web apps gracefully via fallback paths — let it try and fail rather than skip entirely.

### What happened:
- `CandidateEvaluator.RunGatesAsync()` only attempted screenshots when `task.IsWebTask == true`.
- `IsWebTask` was set by `LooksLikeWebTask()`, a keyword scanner checking for "blazor", "react", "html", etc.
- Valid web projects whose task descriptions didn't contain these keywords got `IsWebTask=false` and no screenshots.
- The `PlaywrightRunner.CaptureAppScreenshotAsync()` already had a multi-layer fallback: web server → static HTML → file:// protocol. It handles non-web gracefully by returning null.
- Additionally, `CandidateStateStore.RecordScored()` unconditionally overwrote `ScreenshotBase64` with the event's value, so even if a screenshot was captured early via `RecordEvaluated`, a later `CandidateScoredEvent` with `ScreenshotBase64=null` would wipe it.

### Fix:
- Removed `task.IsWebTask` guard from screenshot capture — always attempt screenshots when Playwright is ready.
- Fixed `RecordScored()` to use `e.ScreenshotBase64 ?? existingCandidate.ScreenshotBase64` to preserve existing screenshots.
- Integration task now uses `LooksLikeWebTask` heuristic instead of hardcoded `false`.

**Rule:** When a pipeline has graceful degradation built in (try web server → try static HTML → return null), don't add an upstream gate that prevents the pipeline from running at all. Let the pipeline self-select its path. And when state transitions can overwrite earlier data, always use null-coalescing (`??`) to preserve existing values.

---

## 80. PR Merge Path Must Cover All Approval Label Combinations

**Lesson:** When multiple agents can approve a PR (PM, Architect, Test Engineer), the merge logic must handle every valid combination of approval labels — not just the "happy path" where inline tests run.

### What happened:
- `MergeTestedPRsAsync` was only called when `IsInlineTestWorkflow == true`, leaving PRs stranded when `TestWorkflow="none"`.
- The Architect removes the `ready-for-review` label when approving (replacing it with `architect-approved`), so `RecoverReadyForReviewPRsAsync` couldn't find approved PRs.
- SE agents looped endlessly showing "Working" with no actionable merge path.

### Fix:
- Removed `IsInlineTestWorkflow` guard — `MergeTestedPRsAsync` is now always called.
- For non-inline workflows, merge PRs with `pm-approved` + `architect-approved` (no `tests-added` required).
- Expanded `RecoverReadyForReviewPRsAsync` to also match `architect-approved` PRs.

**Rule:** When designing multi-agent approval workflows, enumerate every possible label state matrix at design time. If Agent A removes Label X and adds Label Y, every downstream consumer of Label X must also handle Label Y. Draw the state machine on paper first.

---

## 81. Stale Gate Approvals Must Not Leak Across Resources

**Lesson:** Gate approval storage must be strictly scoped to the resource (PR number, issue number) that was approved. A global fallback key causes one resource's approval to leak to subsequent resources.

### What happened:
- `GateCheckService.TryGetLocalApproval()` had a fallback: if no approval found for scoped key `FinalPRApproval:1`, it checked the global key `FinalPRApproval`.
- Approving PR #1 set the global key. When PR #2 arrived, the fallback found the global approval and auto-approved it.
- Same bug existed in `TryGetLocalRejection()`.

### Fix:
- When `resourceNumber` is provided, only check the scoped key `MakeLocalKey(gateId, resourceNumber)` — no global fallback.
- Applied the same fix to `TryGetLocalRejection()`.

**Rule:** Scoped lookups must never fall back to unscoped storage. If a resource-specific key is requested and not found, the answer is "not found" — not "let me check the global one". This is the gate equivalent of the classic "ambient authority" security anti-pattern.

---

## 82. JSONL Output Mode Breaks Direct `ExecutePromptAsync` Callers

**Lesson:** When `CopilotCli.JsonOutput` is `true` in configuration, the CLI binary receives `--output-format json`, causing ALL stdout to be JSONL (one JSON object per line with `type` and `data` fields). The `CopilotCliChatCompletionService` handles this internally, but any code that calls `CopilotCliProcessManager.ExecutePromptAsync()` directly receives **raw JSONL**, not plaintext.

### What happened:
- The Develop wizard's clarifying questions feature calls `ExecutePromptAsync` directly to generate questions from a project description.
- With `JsonOutput: true` (line 148 in `appsettings.json`), the CLI returned JSONL like `{"type":"assistant.message","data":"1. What compliance..."}`.
- The `ParseNumberedQuestions()` regex expected plaintext numbered lines (`1. Question text`), so it matched zero questions.
- The wizard displayed "No clarifying questions needed" even for short 2-sentence descriptions that should generate 8–10 questions.
- The CLI itself worked perfectly — running the same prompt manually via stdin pipe returned 10 perfect questions in JSONL format.

### Fix:
- Added `CliOutputParser.ParseJsonOutput(result.Output)` call before `ParseNumberedQuestions()`, with fallback to raw output if JSONL parsing returns null.
- `ParseJsonOutput()` extracts text from `assistant.message` events in the JSONL stream and concatenates them.

**Rule:** `CopilotCliProcessManager.ExecutePromptAsync()` returns raw stdout — it does NOT parse JSONL. Any code that calls it directly (outside of `CopilotCliChatCompletionService`) MUST check if `CopilotCli.JsonOutput` is enabled and handle the JSONL format via `CliOutputParser.ParseJsonOutput()`. This is an easy-to-miss integration point because the feature works fine in non-JSON mode.

### Key files:
- `src/VirtualDevTeam.Core/AI/CopilotCliProcessManager.cs` — `ExecutePromptAsync()` (returns raw stdout)
- `src/VirtualDevTeam.Core/AI/CliOutputParser.cs` — `ParseJsonOutput()` (JSONL → plaintext)
- `src/VirtualDevTeam.Dashboard/Components/Pages/Develop.razor` — Fixed caller

---

## 83. Complexity-Based PR Sizing Prevents Task Explosion

**Lesson:** Without explicit task count guardrails, the LLM generates too many small tasks for simple projects and too few large tasks for complex ones. A complexity assessment step before engineering planning produces better-scoped PRs.

### What happened:
- For a simple compliance documentation project (3 issues, 8 architecture sections), the LLM generated 12 separate tasks — each producing a tiny PR with minimal changes.
- For complex projects, the LLM sometimes produced only 2–3 monolithic tasks.
- Small PRs caused excessive review overhead; large PRs caused merge conflicts and review difficulty.

### Fix:
- Added `AssessProjectComplexity()` that scores projects as Small (≤3 issues, ≤10 arch sections → cap 3 tasks), Medium (≤7 issues, ≤25 sections → cap 6 tasks), or Large (8+ issues → cap 10 tasks).
- Added `NormalizeTaskPlan()` that merges the smallest tasks into nearest siblings when count exceeds the target. Merge priority: same wave + same parent > same wave > shared dependency > any.
- Added the "CSS-with-feature" rule: CSS/styling changes must be bundled with their feature task, never standalone. This prevents orphan CSS PRs that break styling when applied out of order.
- Normalization runs BEFORE issue creation so no orphan work items are created.

**Rule:** Always assess project complexity before generating a task plan, and enforce task count caps based on that assessment. Prefer fewer, well-scoped tasks over many tiny ones. Bundle related changes (especially CSS/styling) with their feature task.

---

## 84. Cross-Cutting Features Must Cover All Agent Task Pickup Paths

**Lesson:** When adding a new feature that gates or modifies the start of PR work (pre-PR questions, pre-flight checks, etc.), you must wire it into EVERY path that picks up a task — not just the base class path.

### What happened:
- The Pre-PR Clarification Questions feature was wired into `EngineerAgentBase.WorkOnIssueAsync()`, which is used by `SpecialistEngineerAgent`.
- `SoftwareEngineerAgent` has its OWN task pickup path via `WorkOnOwnTasksAsync()` that does NOT call `WorkOnIssueAsync()`. It creates branches, generates PR descriptions, and starts implementation independently.
- Result: Specialist engineers got clarification questions, but the lead SE (the primary engineer) silently skipped them.
- This is the same pattern as Lesson #14 (self-assessment had to be wired into both `EngineerAgentBase.MarkPrCompleteAsync` AND `SoftwareEngineerAgent.FinalizeReadyForReviewAsync`).

### Fix:
- Injected `GeneratePrePRQuestionsAsync()` call into `SoftwareEngineerAgent.WorkOnOwnTasksAsync()` between task claim and PR description generation.
- Clarification context is appended to PR description under "## Implementation Decisions" heading.

**Rule:** `SoftwareEngineerAgent` has two independent paths that bypass `EngineerAgentBase`: (1) `WorkOnOwnTasksAsync` for task pickup, (2) `FinalizeReadyForReviewAsync` for completion. ANY cross-cutting behavior added to the base class MUST also be explicitly injected into these SE overrides. Always grep for the feature in both `EngineerAgentBase` and `SoftwareEngineerAgent` after implementation.

### Key files:
- `src/VirtualDevTeam.Agents/EngineerAgentBase.cs` — `WorkOnIssueAsync()` (base path)
- `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs` — `WorkOnOwnTasksAsync()` (SE override path)

---

## 85. Timeline Issue Colors Must Reflect Linked PR State

**Lesson:** On the timeline, closed GitHub/ADO issues should show amber "Awaiting Merge" status — not magenta "Closed/Merged" — when their linked PR is still open and not yet merged.

### What happened:
- GitHub auto-closes issues linked to PRs when certain conditions are met, but in this system issues are closed programmatically when the SE marks work complete.
- The timeline used the raw issue `state` (closed → magenta) without considering the linked PR's merge status.
- Users saw confusing timelines where issues appeared "done" (magenta) but their PRs were still green/open.
- The mismatch implied work was complete when it wasn't yet merged.

### Fix:
- Added `isAwaitingMerge` logic: if an issue's linked PR exists and is NOT merged, treat the issue as "in-progress" (amber "Awaiting Merge") regardless of its closed state.
- Only show magenta "Closed/Merged" when BOTH the issue is closed AND its linked PR is merged (or no linked PR exists).

**Rule:** In multi-artifact workflows (Issue → PR → Merge), the visual status of upstream artifacts (issues) should reflect the state of the entire chain, not just their own state. A closed issue with an unmerged PR is NOT complete — it's "awaiting merge."

### Key files:
- `src/VirtualDevTeam.Dashboard/Components/Pages/Timeline.razor` — Color/status logic

---

## 86. Auth-Protected Apps Show SSO Pages in Screenshots

**Lesson:** When engineer agents take screenshots of apps that have authentication middleware (Microsoft.Identity.Web, MSAL, etc.), the screenshot captures the SSO login page instead of the actual application UI — even in Development mode.

### What happened:
- `CaptureAppScreenshotAsync` launches the app with `ASPNETCORE_ENVIRONMENT=Development`, but this doesn't disable auth middleware.
- Microsoft.Identity.Web and similar middleware redirect unauthenticated requests to the IdP login page regardless of environment.
- Each agent has its own worktree, so auth configuration varies by branch state.
- The PM reviewer saw login pages in screenshots and couldn't assess the actual UI, causing review failures.

### Fix:
- Added auth-bypass environment variables to screenshot capture: `DISABLE_AUTH=true`, `Authentication__DisableForScreenshots=true`.
- After taking the screenshot, check page content for common auth indicators (sign-in forms, SSO redirect text).
- If auth page is detected, log a warning and annotate the screenshot metadata as `auth-blocked`.
- The PM review prompt now includes guidance that auth-blocked screenshots should not be penalized.

**Rule:** Screenshot capture for review purposes should attempt to bypass authentication. Apps in the Development environment should respect well-known disable flags. When auth can't be bypassed, annotate the screenshot rather than letting it fail silently or mislead reviewers.

---

## 87. FlowMonitor Must Be Simpler Than the System It Watches (AutoGen "Supervisor" Principle)

**Lesson:** When you build an autonomous watchdog over an AI multi-agent system, the watchdog itself must NOT be AI-driven for its core decisions. It must be more reliable than the system it observes — which means deterministic detectors, vetted action catalogs, no LLM in the hot path.

### What happened:
- The 11-agent research synthesis on FlowMonitor v2 (May 2026) collected ~110 recommendations across multiple areas. Several proposals would have made FlowMonitor itself agentic (LLM-decided escalation rungs, AI-chosen action sequences, dynamic detector creation).
- Cross-referenced AutoGen and LangGraph supervisor-pattern docs — both repos hit the same wall: when the supervisor is itself an LLM, supervisor failures cascade and there's no one watching the watcher.

### Fix / design decisions adopted:
- All detectors are deterministic — pure logic over `DetectorContext`, no LLM calls
- Escalation ladder rungs (T1.2) are picked by `GetAttemptCount(dedupKey)` — no AI
- Verification (T1.3) re-runs the originating detector — no AI
- AI is allowed ONLY in the FixRecommendation flow (T1.5) — and only as advisory output gated behind operator Approval, never auto-applied for code-level fixes
- LiveFixApplicator (T1.6) DOES use Copilot CLI but is gated by: (a) operator approval, (b) `git status --porcelain` snapshot diff verifying no out-of-scope file was touched, (c) tier classification confirming the changes are config-only

**Rule:** A deterministic supervisor watching an AI-driven system is more reliable than an AI watching another AI. Use AI in the supervisor only for advisory output that an operator approves before action, never for the supervisor's own control flow.

### Key files:
- `src/VirtualDevTeam.Orchestrator/FlowMonitorService.cs`
- `src/VirtualDevTeam.Core/HealthMonitor/Actions/*.cs`
- `src/VirtualDevTeam.Core/HealthMonitor/Detectors/*.cs`

---

## 88. Escalation Ladder Rate Limit Applies Globally, Not Per-Rung

**Lesson:** When a finding type goes through multiple action rungs (kick → comment → label+notify), the global rate limit applies to the SUM of all actions across all findings — not to each rung separately. Otherwise a flapping condition can blow through every rung in seconds and the runner spams the platform.

### What happened:
- T1.2 escalation ladder shipped with `MaxActionsPerHour=12` on the global counter
- During an end-to-end run, the AgentStuckDetector's 30m threshold fired Warning at 30m for the SE Leader (legitimately running strategy framework). Verification (T1.3) confirmed the condition persisted, severity bumped Warning→Critical, escalating to Rung 2 (post-explicit-ask) and Rung 3 (escalate-to-human label+notify)
- Rate limit hit at 12 actions/hr globally — the detector kept LOGGING findings (no rate limit on findings) but stopped TAKING actions, which is correct behavior

### Why this matters:
- Without the global cap, a flapping detector firing every 30s could fire 60-120 actions/hr per finding type
- With per-rung caps (e.g., 4-per-rung-per-hr), a multi-rung escalation could still execute 12 actions for one finding type alone — defeating the global budget intent
- The global cap acts as a circuit breaker on the entire FlowMonitor surface

**Rule:** Rate limit by aggregate action count, not by rung or by finding type. If you want different behavior for different finding severities, encode that in the `IFlowAction.CanHandle` predicate, not in separate per-rung budgets.

### Key files:
- `src/VirtualDevTeam.Orchestrator/FlowMonitorService.cs:165-173` (rate-limit gate)
- `src/VirtualDevTeam.Core/HealthMonitor/FlowMonitorPersistence.cs` (`GetAttemptCount`)

---

## 89. AgentStuckDetector Threshold Tradeoff — 30m Is Too Aggressive for Strategy Framework

**Lesson:** The "30 minutes without status change" stuck-detection threshold fires false positives on legitimate long-running tasks. Strategy framework + Copilot CLI candidates + LLM Judge + Playwright eval can take 30-45m on a complex task (T1 Project Foundation observed at 33m wall-clock).

### What happened:
- AgentStuckDetector's threshold is hardcoded to `TimeSpan.FromMinutes(30)` in `Program.cs:228`
- During the May 2026 end-to-end run, SoftwareEngineer agent was on "Strategy candidates: Project Foundation & Scaffolding" for 33m — completely legitimate work but caught by the detector
- Triggered the entire 3-rung escalation ladder + verification — 6 findings + 12 actions before rate limit, none of which actually fixed anything (because nothing was broken)
- The status reason text doesn't change during strategy candidate generation, so a status-change-time-based detector can't distinguish "stuck" from "working hard on a long task"

### Two possible fixes:
1. **Bump threshold + make configurable** — easy change, but masks the underlying pattern. Tasks legitimately running >45m exist too
2. **Detect activity, not status change** — track LLM call recency (`ActiveLlmCallTracker`) or file write recency in agent's worktree. Genuinely stuck agents have NO activity; busy agents have recent activity even if status text is unchanged

**Rule:** Time-since-status-change is a poor proxy for stuckness. Combine it with a positive-activity signal (LLM call recency, file modifications, bus message volume) before triggering action. The status field is for humans; stuckness detection should look at actual work.

### Open work:
- TODO `post-run-stuck-threshold` — make AgentStuckDetector threshold configurable + bump default to 45m
- TODO future — add `IActivitySignal` interface that detectors can consult for genuine activity

### Key files:
- `src/VirtualDevTeam.Core/HealthMonitor/Detectors/AgentStuckDetector.cs`
- `src/VirtualDevTeam.Runner/Program.cs:228`

---

## 90. Side-Effect Labels From Escalation Actions Need Cleanup On Resolve

**Lesson:** When a FlowMonitor action has a side effect on the platform (applies a label, posts a comment, opens a tracking issue), that side effect persists even after `T1.3 verify-acted-on` marks the finding `Resolved`. The label survives the agent moving on.

### What happened:
- T1.2 Rung 3 `EscalateToHumanAction` applied an `agent-stuck` label to PR #1344 during a false-positive escalation
- After the agent moved past the stuck-detection condition (it was just slow), T1.3 verification marked the finding `Resolved`
- But the `agent-stuck` label remained on the PR. Even after the PR merged. Even after the project completed.
- The dashboard still showed the label, misleading anyone reviewing the merged work

### Two design options:
1. **Each `IFlowAction` grows an optional `UndoAsync` method** — verification calls `UndoAsync` for actions on a finding when their condition clears. Symmetric and clean, but requires every action to think about reversibility
2. **Move side-effects out of "actions" and into "notifications"** — Rung 3 only emits a notification (which auto-resolves when verification clears), no platform-state mutation. The operator sees the notification and can manually apply labels if they want them tracked

**Rule:** Any FlowMonitor action that mutates platform state should either be reversible (with a verification-driven undo) or constrained to truly idempotent additions. Sticky labels from a transient false-positive degrade signal-to-noise on the platform.

### Open work:
- TODO `post-run-stuck-label-cleanup` — wire `IFlowAction.UndoAsync` and call from `VerifyActedOnFindingsAsync`

### Key files:
- `src/VirtualDevTeam.Core/HealthMonitor/Actions/EscalateToHumanAction.cs`
- `src/VirtualDevTeam.Orchestrator/FlowMonitorService.cs` (`VerifyActedOnFindingsAsync`)

---

## 91. Squad Framework Crashes Need Strategy-Level Fallback, Not Silent Failure

**Lesson:** When the squad framework (`copilot --agent squad`) crashes mid-task — including hard Windows runtime crashes like `STATUS_STACK_BUFFER_OVERRUN` (exit code `-1073740791` / `0xC0000409`) — the strategy orchestrator shouldn't just declare "no winner" and proceed. It should fall through to other strategies (copilot-cli) for retry.

### What happened:
- During the May 2026 end-to-end run, the strategy framework delegated T-FINAL (Final Integration) to squad
- Squad ran for 12.2 seconds and exited with `-1073740791` (STATUS_STACK_BUFFER_OVERRUN — Windows runtime memory corruption, not a logic failure)
- Only 1 file modified before crash; not enough to constitute a coherent patch
- The strategy orchestrator logged "Patch will be evaluated by the standard pipeline" and proceeded — but the standard pipeline found insufficient changes to open a PR
- T-FINAL silently produced no output. SE Leader went Idle "Waiting for integration PR to merge" forever.
- Project effectively delivered 6/7 features but workflow never finalized

### Why squad-specific:
- Squad is a Node-based agentic framework that can hit Windows native runtime issues (large MCP stack frames, process tree depth, etc.)
- Copilot CLI strategy is more stable on Windows for the same task
- Both should be tried before declaring T-FINAL impossible

**Rule:** When a strategy candidate crashes with a non-business-logic exit code (any signal that's a Windows runtime / OS crash, OOM, or timeout), the strategy orchestrator should retry on a different strategy automatically rather than treating it as "winner=nothing." A retry-budget keeps this bounded.

### Open work:
- TODO `post-run-squad-crash-retry` — strategy orchestrator detects squad runtime crashes (exit codes -1073740791, -1073741819, etc.) and falls through to copilot-cli for the same task
- Alternative: surface the crash to FlowMonitor as a Critical finding without an action handler, letting T1.5 FixRecommendation generate a "retry T-FINAL via copilot-cli" plan for operator approval

### Key files:
- `src/VirtualDevTeam.Core/Frameworks/SquadFrameworkAdapter.cs`
- `src/VirtualDevTeam.Core/Strategies/StrategyOrchestrator.cs`

---

## 92. PR Merge Conflict Auto-Recovery Requires an Active SE Leader Loop

**Lesson:** The `TryCloseAndRecreatePRAsync` auto-recovery for stuck PRs only fires while the SE Leader is in its merge loop. If SE Leader moves to "waiting for integration PR" idle state before a PR's conflict surfaces, the recovery never runs and the PR sits CONFLICTING for hours.

### What happened:
- 6 feature PRs merged in parallel during the May 2026 run
- PR #1347 (T3 Guidance Domains) was the LAST to come up for merge
- The other PRs that merged before it modified `tests/Compliance.UITests/PlaywrightFixture.cs`, leaving #1347 in `mergeStateStatus=DIRTY` (CONFLICTING)
- By the time the conflict surfaced, SE Leader had already advanced to "Creating integration PR" (T-FINAL) state and the worker that authored #1347 (SE 3) had moved on too
- No agent was running the merge loop for #1347. FlowMonitor caught the stall and escalated all 3 rungs but its actions don't auto-rebase — they only nudge agents
- Operator manually rebased + force-pushed; the PR auto-merged within 60s

### Why FlowMonitor's existing actions don't help:
- Rung 1 kick wakes an agent's poll loop, but there's no "rebase PR X" loop to wake
- Rung 2 PR comment is informational, doesn't trigger code action
- Rung 3 label is a marker; no automation responds to `agent-stuck` directly
- The closest existing automation (`TryCloseAndRecreatePRAsync`) lives in EngineerAgentBase but only runs during active task work

**Rule:** Self-healing automation must run on a schedule, not just on an active agent's loop. PR merge conflicts ARE detectable from periodic platform polling (`mergeStateStatus=DIRTY` for >X minutes) — that's a perfect FlowMonitor detector.

### Open work:
- TODO `post-run-pr-merge-conflict-detector` — new `IFlowDetector` for stale CONFLICTING PRs (>15min). Pairs with new `IFlowAction` for rebase-or-close-and-recreate. Uses `T1.1 DetectorContext.Platform.ListOpenPullRequestsAsync` + per-PR mergeStateStatus

### Key files:
- `src/VirtualDevTeam.Agents/EngineerAgentBase.cs` (existing `TryCloseAndRecreatePRAsync`)
- Future: `src/VirtualDevTeam.Core/HealthMonitor/Detectors/StalePullRequestDetector.cs`

---

## 93. "Cannot Advance From X to Y" Log Spam Is Mandatory To Dedup

**Lesson:** Phase-gate evaluation logs at `Information` level on every tick the gate is unmet. Across a multi-hour run, this produces hundreds of identical lines that drown real signal in the runner log.

### What happened:
- During the May 2026 run, `WorkflowStateMachine.TryAdvancePhase` logged `Cannot advance from EngineeringPlanning to ParallelDevelopment: Engineering plan must be produced...` 87 times in a single phase
- Multiplied across 8 phases, that's 700+ near-identical lines
- When investigating the run for actual failures, these lines hid the real events (squad crash, conflict, T-FINAL anomalies)

### Fix:
- Track last-logged blocker reason per phase-pair in `_lastLoggedBlockerByPair` dictionary
- When current blocker matches the last logged for the same pair, downgrade to `LogTrace` (hidden by default)
- When current blocker differs (gate set changed), log at `Information` again
- When a phase advances, clear the cached entry for that pair so the next blocker logs fresh

**Rule:** Periodic gate evaluation is fine; logging every evaluation at Information is not. Always dedup repeated polling messages by content. The polling logic is right; only the messaging needs to change.

### Key files:
- `src/VirtualDevTeam.Orchestrator/WorkflowStateMachine.cs:46-54` (`_lastLoggedBlockerByPair`), `:160-186` (dedup logic)

---

## 94. Workspace Clone On Cold Start Wastes ~30s/Agent For Finished Projects

**Lesson:** Engineer agents (`EngineerAgentBase.OnInitializeAsync`) always clone the target repo into `.agents/{agent-id}/<repo>/` on startup. On a finished project (no open engineering tasks, no open PRs), every agent re-clones unnecessarily, then immediately goes Idle when the SE Leader's recovery short-circuits.

### What happened:
- After the May 2026 run completed (6/6 features merged), restarting the runner cold caused all 4 SE workers + the SE Leader + TE to clone Compliance repo into 5 separate `.agents/<id>/Compliance/` directories
- Each clone took ~30s. Total: 2-3 minutes of wasted setup time per restart
- Followed immediately by SE Leader's "Engineering already complete on restart: 6 merged engineering PR(s), 0 open engineering-task issues — short-circuiting plan creation" — the workspaces were never used

### Fix:
- Before the clone, probe `WorkItemService.ListByLabelAsync("engineering-task", state="open")` AND `PrService.ListOpenAsync()` filtered to the agent's role
- If both come back empty, set status `Idle "Engineering complete from prior run — workspace not needed"` and skip the clone entirely
- Probe is best-effort — any platform exception falls through to normal clone

**Rule:** Don't pay for resources you might not need on startup. A cheap platform probe costs ~500ms vs the 30s clone — net win even when the probe sometimes leads to a clone anyway.

### Key files:
- `src/VirtualDevTeam.Agents/EngineerAgentBase.cs:148-205` (probe + conditional clone)

---

## 95. Premature `engineering.all.complete` Signal — HealthMonitor Auto-Detect Was Too Eager

**Lesson:** `HealthMonitor.cs` has an auto-detect block that fires `WorkflowStateMachine.Signals.AllEngineeringComplete` based on heuristic phrases in agent status reasons. The phrase list previously included `"integration pr"` — which matched both "Creating integration PR" (post-T-FINAL) AND "Waiting for integration PR" (pre-T-FINAL). The latter caused the signal to fire BEFORE T-FINAL had a PR.

### What happened:
- May 2026 run: SE Leader transitioned `Idle → Working "Creating integration PR"` (good — T-FINAL starting)
- HealthMonitor detected "integration pr" substring → fired `AllEngineeringComplete`
- WorkflowStateMachine saw the signal + other already-met gates → advanced phase Testing → Review → Completion
- Then squad crashed silently. T-FINAL never produced a PR. But phase already said Completion.
- Dashboard banner read "Project Done" while T-FINAL was actually broken/missing

### Fix:
- Tightened the trigger phrase list to `"engineering complete"`, `"all tasks complete"`, `"all tasks done"` only — removed `"integration pr"`
- Added a defensive guard: signal does NOT fire if any SE Leader's status reason contains "integration pr" (catches the "Waiting for integration PR" case explicitly)
- The SE Leader's `SignalEngineeringCompleteAsync` (the canonical path) is unchanged — it correctly fires only after `_integrationPrCreated=true`

**Rule:** When you have multiple paths that emit the same signal — an explicit one and an auto-detect heuristic — make sure the heuristic is STRICTLY MORE conservative than the explicit one. The heuristic exists to catch failures of the explicit path, not to race ahead of it.

### Key files:
- `src/VirtualDevTeam.Orchestrator/HealthMonitor.cs:394-415` (auto-detect block with new guard)
- `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs` (`SignalEngineeringCompleteAsync` — unchanged, the canonical path)

---

## 96. Stale Step On Idle Agent Cards — UI Layer Should Suppress When Status Doesn't Match

**Lesson:** `IAgentTaskTracker.GetCurrentStep(agentId)` returns the last `InProgress` step regardless of agent status. When an agent transitions Working → Idle without explicitly `CompleteStep`'ing the in-progress step (common in error-recovery paths), the dashboard agent card shows stale "step N/M" text under an Idle badge.

### What happened:
- May 2026 run: agents recovering from prior project state went to `Status=Idle "Engineering complete (recovered from prior run)"`. But their previous tasks' steps were never explicitly completed because recovery short-circuited the normal flow.
- `AgentSnapshotService.ToSnapshot` returned the stale step + task name from `GetCurrentStep` regardless of status.
- Two of three call sites in the file already guarded with `if (Status == Working)`. `ToSnapshot` did not — caused inconsistent presentation.

### Fix:
- In `ToSnapshot`, when `agent.Status == Idle`, set `currentStep = null` and `taskName = null`. The underlying tracker still has the data (visible in the timeline view); the dashboard's primary status display just stops showing it.

### Alternative considered (not adopted):
- Subscribing AgentTaskTracker to AgentRegistry.AgentStatusChanged and auto-marking InProgress steps as Skipped when status flips to Idle. More thorough but adds cross-component wiring; the UI-layer suppression is sufficient for the symptom.

**Rule:** For "the agent is doing X" UI claims, the source of truth is the agent's status field, not the work tracker. Suppress at the snapshot layer when the two disagree, and let the timeline show the full historical record separately.

### Key files:
- `src/VirtualDevTeam.Dashboard/Services/AgentSnapshotService.cs:456-485` (`ToSnapshot`)

---

## 97. Stale `cli-mcp` Orphan Processes Hold Workspace Directory Handles, Hang Reset Scripts

**Lesson:** GitHub Copilot CLI sessions spawn `node <copilot-cli-mcp>/dist/cli.js start` MCP servers as children. When the parent CLI session dies (window close, crash), these MCP servers can leak — accumulating across sessions. They hold open file handles on whatever directory was the parent's CWD.

### What happened:
- During the minimal-reset before the May 2026 run, `robocopy /MIR` got stuck deleting `.agents/<agent-id>/<repo>/Compliance` for ~10 minutes
- Investigation: 117 stale `cli-mcp` node processes from prior Copilot CLI sessions, ages 30+ hours, holding handles on the workspace dirs
- The `kill-orphan-runner-procs.ps1` script's surgical filter does NOT match `cli-mcp` (only `@playwright/mcp`, `@modelcontextprotocol/server-`, `blazor-devserver`, `--agent squad`) because cli-mcp is a Copilot CLI MCP, not a runner-spawned MCP — killing them might affect the user's interactive Copilot sessions
- Mitigation: target ONLY orphans older than 6 hours (this Copilot CLI session was <14h old). Killing 117 processes >6h freed the directory locks instantly

### Why this matters going forward:
- This isn't really a "runner" cleanup problem — it's a "Copilot CLI doesn't kill its MCPs cleanly on shutdown" problem
- Each interactive Copilot CLI window leaks its own MCPs when closed/crashed
- Manual cleanup is required periodically; automation must NOT use blanket "kill all node" because the user's active Copilot CLI is also `node`

**Rule:** When designing a cleanup script, age + cmdline pattern is the safest filter. >6h with `cli-mcp` in cmdline is a near-certain orphan. Always log what you're going to kill before killing it; provide a `-WhatIf` mode.

### Future automation:
- `scripts/kill-orphan-runner-procs.ps1` could grow a `-IncludeStaleMcps` switch (default off) with the age safety check
- Or a separate `scripts/clean-stale-mcps.ps1` script focused on this specific class

### Key files:
- `scripts/kill-orphan-runner-procs.ps1` (existing, narrow filter)
- Manual mitigation pattern documented in this lesson

---

## 98. T1.1 DetectorContext Extension — Lazy + Cached + Fault-Tolerant Platform Views

**Lesson:** When you add platform-resource visibility to detectors (PRs, work items, reviews, commits), don't have each detector inject `IPullRequestService` etc. directly. Build a per-tick lazy/cached view interface that all detectors share — pays the API cost ONCE per tick regardless of detector count.

### Why this design:
- Detectors are stateless and run every poll interval (default 30s)
- Multiple detectors might want "open PRs" — naive design = N detectors × M API calls per tick
- Some detectors might never need platform data — naive design forces unused dependencies
- Platform calls flake (rate limit, network) — every detector having to handle that = boilerplate

### Design adopted:
- `IPlatformView` interface on `DetectorContext` with 4 lazy methods (`ListOpenPullRequestsAsync`, `ListOpenWorkItemsAsync`, `ListUnresolvedThreadsAsync(prNumber)`, `GetLatestCommitAsync(prNumber)`)
- `PerTickPlatformView` implementation: `Task<...>?` cached fields populated on first call per tick, dictionary cache for per-PR queries, all wrapped in lock for thread safety
- Each method swallows exceptions, logs at Warning, returns empty/null — detectors stay simple
- `NullPlatformView.Instance` for pre-project-open state — every method returns empty/null without null-checks needed

### Why detector-friendly view records (`PullRequestView`, `WorkItemView`, etc.):
- Trims platform models to read-only fields detectors care about
- Uses UTC-tagged `DateTimeOffset` instead of bare `DateTime` (the platform models have `Kind=Unspecified` which led to UTC/local bugs in past)
- Pre-filters where useful (e.g., `ListUnresolvedThreadsAsync` returns ONLY unresolved — every detector would otherwise re-filter)

**Rule:** When extending a context object that's shared across N consumers per tick, use lazy + cached + fault-tolerant views. The supervisor must be MORE reliable than the system it watches (see lesson #87) — empty results on platform errors are the safe default.

### Key files:
- `src/VirtualDevTeam.Core/HealthMonitor/Detectors/IFlowDetector.cs` (extended `DetectorContext`, view records, `IPlatformView`, `NullPlatformView`)
- `src/VirtualDevTeam.Orchestrator/PerTickPlatformView.cs` (caching impl)
- `src/VirtualDevTeam.Orchestrator/FlowMonitorService.cs:250-295` (`BuildContext` populates `Platform` + real `WorkflowSignals`)
- `src/VirtualDevTeam.Orchestrator/WorkflowStateMachine.cs` (`GetSignals()` snapshot getter)

---

## 99. Compile-Pass + Unit-Tests-Pass ≠ App Actually Works (the "white screenshot" failure mode)

**Lesson:** During the 2026-05-11 tower-defense monitoring run, six PRs in a row got approved + merged with completely broken runtime behaviour. The agents' pipeline silently approved a broken target app for 4+ hours. Anatomy of the failure:

1. The target backend (`GridGuardians.Api`) had non-idempotent seed code that ran on every startup. First run: seeds OK. Every subsequent run: crashes with `SQLite Error 19: UNIQUE constraint`.
2. `dotnet build` passed (compile-time only — never executes the seed)
3. Unit tests passed (mocked the DB or ran against in-memory fresh state — didn't exercise the seed→serve flow)
4. Architect + PM reviews approved on **code structure** (the code "looks right")
5. TE launched the app via Playwright to capture a screenshot. Backend crashed during launch. Frontend got HTTP 500 on every `/api/config/*` endpoint. Phaser logged "Scene key not found: MenuScene". Canvas stayed solid white.
6. Playwright captured the blank canvas — every `menuscene-screenshot.png` was an identical 4158 bytes (PNG of uniform white compresses aggressively).
7. TE uploaded the blank image to the PR as "App Preview". Vision-AI dutifully described it as "blank canvas with no visible content" in a log line that nobody acted on.
8. PM saw the comment + image, approved on the assumption that "code looks fine, screenshot is just a render glitch".

The whole pipeline approved a fundamentally broken app because **no agent was responsible for verifying the target app actually starts and renders what its PR promised**.

### Fix shipped (multi-layer defence):

**Layer 1 — cheap heuristic (catches the obvious case):**
- `ScreenshotQualityChecker.Check(byte[] png)` in `Core/Workspace/ScreenshotQualityChecker.cs`
- File-size threshold: PNG < 15 KB on a 1000+px canvas is almost certainly uniform fill (no decode needed — System.Drawing/ImageSharp would be 100ms+ per capture)
- Wired into `PlaywrightRunner.CaptureAppScreenshotAsync` itself: returns `null` on blank capture so EVERY consumer (TE, EngineerBase, Researcher) routes through their existing "App Preview Unavailable" branch automatically — no per-consumer changes needed.

**Layer 2 — semantic vision check (catches subtle cases):**
- `PullRequestWorkflow.EvaluateScreenshotAgainstExpectationsAsync(screenshot, prTitle, prBody, chat, ct)` returns `ScreenshotEvaluation { MatchesExpectations, Confidence, Observed, Expected, BlockingIssues, Verdict }`
- Verdict: `MATCHES` / `DOES_NOT_MATCH` / `INCONCLUSIVE`. Blocks only on `DOES_NOT_MATCH` with confidence ≥ 0.6. Inconclusive on backend-only PRs so they aren't false-flagged.
- Catches: blank canvases, wrong-scene rendered, error pages, stuck loading spinners, login-redirect leaks, backend-error toasts.

**Layer 3 — engineer self-catches BEFORE submitting (most important):**
- `RunPrePublishScreenshotCheckAsync` on `EngineerAgentBase`, called from BOTH `MarkPrCompleteAsync` (specialists) AND `SoftwareEngineerAgent.FinalizeReadyForReviewAsync` (lesson #14, #16 — dual-path always wires both).
- Captures + evaluates screenshot BEFORE the self-assessment LLM runs. Surfaces the verdict as an implementation note in the handoff context.
- `engineer-base/self-assessment-system.md` updated: treats `DOES_NOT_MATCH ≥ 0.6` as a HARD GAP. The existing rework loop (1 retry budget) attempts a targeted fix.

**Layer 4 — TE blocks upload on confident mismatch:**
- `TestEngineerAgent` standalone-screenshot path now runs the semantic check BEFORE uploading. On `DOES_NOT_MATCH ≥ 0.6`: doesn't upload the blank PNG, posts "App Preview Rejected — Mismatch With PR Intent" with Expected vs Observed reason. PM review sees the rejection rationale instead of a buried log line.

**Layer 5 — prevent at generation time:**
- `prompts/architect/multi-turn-data-model.md`: Data Model section mandates idempotent seed (HasData / INSERT OR IGNORE / check-then-insert)
- `prompts/engineer-base/single-pass-implementation.md`: every implementation that touches DB seed or startup must mentally run `dotnet run` twice. Explicit note: backend crash ⇒ blank canvas.

### Safety properties of the fix:
- Each layer is independently effective. Layer 1 alone catches every blank-canvas case observed. Layers 2-4 catch subtler mismatches (wrong scene, error page).
- Safe-by-default: any check failure (no vision model, INCONCLUSIVE verdict, no workspace) is a no-op. Only confident `DOES_NOT_MATCH` blocks.
- Layer 5 prevents the bug from reaching capture in the first place.

### Why this matters beyond GridGuardians:
This failure class isn't game-specific. ANY agent-built project that has a backend + SPA + seed data is susceptible. The agents trust each other's `dotnet build` + `npm test` outputs and never verify "does the running app match the screenshot a human would expect to see?" until reviewers catch it manually — which they often don't, because they're reviewing code, not behaviour.

### Key files:
- `src/VirtualDevTeam.Core/Workspace/ScreenshotQualityChecker.cs` (new, ~110 LOC + 5 tests)
- `src/VirtualDevTeam.Core/Workspace/PlaywrightRunner.cs:1879-1957` (CaptureAppScreenshotAsync returns null on blank)
- `src/VirtualDevTeam.Core/GitHub/PullRequestWorkflow.cs:1758-1900` (`EvaluateScreenshotAgainstExpectationsAsync` + `ScreenshotEvaluation` record)
- `src/VirtualDevTeam.Agents/EngineerAgentBase.cs:1642+` (`RunPrePublishScreenshotCheckAsync` wired into `MarkPrCompleteAsync`)
- `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs:3280+` (parallel call in `FinalizeReadyForReviewAsync` — dual-path)
- `src/VirtualDevTeam.Agents/TestEngineerAgent.cs:1707+` (rejects upload on confident mismatch)
- `prompts/architect/multi-turn-data-model.md` (idempotent-seed mandate)
- `prompts/engineer-base/single-pass-implementation.md` (startup-twice mental test)
- `prompts/engineer-base/self-assessment-system.md` (HARD GAP if screenshot verdict is DOES_NOT_MATCH)

### Rule:
**Compile + unit tests + code review ≠ "the app works".** For any project with a runtime surface, add a layer that asks: *"does what's actually rendered match what this PR promised?"* — and ensure the engineer catches it BEFORE reviewers do. Vision-AI is cheap (~$0.001/check) and catches an entire class of silent regressions that no other gate can.


---

## 100. Avoid Project-Specific Whitelists — Use Capability Scoring With Peer Deferral

**Lesson:** During the 2026-05-12 image-gen integration cleanup, an initial fix to prevent the "Game Engine Engineer" from snatching art-asset tasks intended for the "Artist SME" used a hard-coded keyword whitelist (`artKeywords = ["sprite", "art", "illustration", ...]`) in `SpecialistEngineerAgent`. The rubber-duck immediately surfaced the obvious problem: this only works for game/art projects. Tomorrow's SME might be "Database Migrator" or "Compliance Auditor" or "ML Feature Engineer" — every new domain would require touching this list. That's the wrong abstraction.

VirtualDevTeam ships arbitrary projects (games, CRUD, audit tools, ML, infra, anything the wizard takes). Any routing fix that only works for one domain is the wrong fix.

### The general solution: capability scoring with peer deferral

Replace whitelists with a numeric match score that every agent computes against every task using the same function. Then defer based on peer-comparison:

```text
For each idle specialist agent A, each candidate task T:
  my_score = count(my_capability_keywords ∩ words_in(T.title + T.body))
  peer_scores = { B in peer_specialists } map (B.capability_keywords ∩ words_in(T)).count
  best_peer = max(peer_scores)

  IF my_score == 0 AND I have capabilities declared → SKIP T (zero overlap)
  IF my_score < best_peer                          → SKIP T (defer to better peer)
  ELSE                                             → ELIGIBLE for T (race resolves naturally)
```

### Why this works for every project type

- **Game-art project, "sprite sheet" task:** Artist SME (caps: art, sprites, image-generation) scores 5. Game Engine Engineer (caps: frontend, phaser, typescript, pathfinding) scores 1. Game Engine Engineer defers. No "art" string anywhere in code.
- **Compliance project, "PCI-DSS Section 4 audit" task:** Compliance Auditor SME scores 4. Backend Engineer scores 0. Backend defers. No "compliance" string in code.
- **ML project, "ETL pipeline" task:** Data Engineer SME scores 6. ML Engineer scores 2. ML Engineer defers. No "data" string in code.
- **Pure-generalist team:** Everyone scores 0, all tasks eligible, race resolves naturally. No "is-this-a-specialist" branch.

### Key properties

1. **Same function, every agent.** No per-domain branch. The function operates only on `Capabilities` (set declared by SME definition) and task text. No knowledge of project type required.
2. **New domains add zero code.** Adding a "Security Auditor" SME with capabilities `["security", "audit", "vulnerability", "owasp"]` immediately participates in routing — no `SpecialistEngineerAgent` edit needed.
3. **Naturally extends to N specialists.** Whether the team has 2 or 20 distinct specialists, the comparison loop scales linearly.
4. **Generalists stay last-resort.** Empty capabilities → my_score always 0 → defer to ANY specialist with score ≥ 1.
5. **No "WRONG SPECIALIST" hard-fail.** Soft deferral via score-comparison is a self-correcting equilibrium; a hard-fail predicate (`if (this is the wrong specialist) post comment and stop`) requires knowing what "wrong" means, which requires a domain whitelist.

### Test before merging any routing/matching change

Ask these four questions:

1. Would this still work if every keyword in my whitelist were replaced with a totally different domain? If no → too specific.
2. Would this still work if a new SME role appeared tomorrow with capabilities I've never heard of? If no → too specific.
3. Would this still work for a pure-generalist team (no specialists)? If no → too specific.
4. Would this still work for a 1-of-each team? If no → too specific.

If you answered yes to all four, the fix is general.

### The implementation pattern

`SpecialistEngineerAgent.RunAdditionalLoopWorkAsync` (the specialist's idle-loop self-claim):

```csharp
var myKeywords = ExtractCapabilityKeywords(Definition.Capabilities);
var peerKeywordSets = CollectPeerCapabilityKeywords();   // queries AgentRegistry, returns peer specialists only

int Score(IEnumerable<string> kws, PlatformWorkItem t) =>
    !kws.Any() ? 0 : kws.Count(kw => $"{t.Title} {t.Body}".ToLowerInvariant().Contains(kw));

var eligible = ready
    .Select(t => new { Task = t, Mine = Score(myKeywords, t), Best = peerKeywordSets.Any() ? peerKeywordSets.Max(p => Score(p, t)) : 0 })
    .Where(x => myKeywords.Count == 0 || x.Mine > 0)   // require ≥1 overlap if I have caps
    .Where(x => x.Mine >= x.Best)                       // defer to strictly-better peer
    .OrderByDescending(x => x.Mine);
```

That's it. No special-cased domains, no static category lookups, no per-project tuning.

### Why prompt-level rules complement this

The SE Leader's LLM-driven router (`SoftwareEngineerAgent.MatchTasksToEngineersAsync`) is the FIRST line of defence. Its prompt also avoids domain whitelists — it tells the LLM in natural language: "engineers whose capabilities are exclusively visual-asset-focused should not get code-implementation tasks; defer to data-driven semantic matching." The LLM handles novel domains gracefully because it reasons over the capability names + task content; it doesn't pattern-match a hardcoded list.

### The rule

**ALWAYS prefer data-driven / capability-driven routing. NEVER add domain-specific keyword whitelists, category predicates, or project-type branches in core agent logic. If the fix only works for one project type, it's the wrong fix.**

### Key files

- `src/VirtualDevTeam.Agents/SpecialistEngineerAgent.cs` — peer-scoring self-claim loop
- `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.TaskAssignment.cs` — LLM-driven router prompt (rule 7: exclusive-scope deferral via natural language, no whitelists)
- `Session.md` § 3c — operator-facing version of this rule


## 101. Image-Gen Deployment Ladder Requires Multiple Deployments — and gpt-image-1.5 Is the Quality Sweet Spot

**Date:** 2026-05-12
**Context:** Day-long Artist sprite-generation marathon. Every fallback layer of our recipe assumes a deployment ladder, but a single-deployment Azure resource collapses the ladder to single-shot retries.

### The trap
The shared image-gen recipe (prompts/_shared/image-gen-instructions.md) walks deployments in order on transient failures (429 rate limit, 503 capacity, 404 deployment-not-found). With only one model deployed in your Azure OpenAI resource, the "fallback" path is no fallback at all — you just retry the same throttled deployment.

### The fix is in Azure, not in code
Provision **all four** gpt-image-* deployments in the same Azure OpenAI resource:

| Deployment | Typical RPM (operator-tunable in Azure) | Per-call wall clock | Use case |
|---|---|---|---|
| `gpt-image-2` | 2 RPM (very gated) | 60s+ | Fallback only — too slow + tight quota for primary |
| `gpt-image-1.5` | 9 RPM (operator-tunable) | ~32s | **Recommended primary** — best quality observed in side-by-side test |
| `gpt-image-1` | 9 RPM | ~33s | Solid mid-tier fallback |
| `gpt-image-1-mini` | 12 RPM | ~30s | High-throughput fallback for bulk multi-frame work |

### Quality finding (operator-validated 2026-05-12)
A side-by-side test (single goblin prompt, single cannon-tower prompt, 3 deployments in parallel, 6 calls in 34.5s wall-clock):

- **gpt-image-1.5**: dramatically more detail. Cannon tower had ornate carved base + flag detailing; goblin came back as a full warrior with helmet, armor, weapon. ~0.5-0.8 MB file size (efficient compression).
- **gpt-image-1**: simpler / less detailed outputs at the same prompt. ~1.5-1.6 MB.
- **gpt-image-1-mini**: similar fidelity to image-1 at this prompt complexity. ~1.5-1.7 MB.

**File size is NOT a quality predictor** — image-1.5 produces SMALLER files but more detail (better compression + likely a more recent encoder). Always verify by eye, not by byte count.

### Recommended deployment order (by primary use)
`jsonc
// develop-settings.json → AzureOpenAIImage
{
  "PrimaryDeployment": "gpt-image-1.5",       // ← BEST QUALITY at human-verified standards
  "FallbackDeployments": [
    "gpt-image-1",        // proven, similar wall-clock
    "gpt-image-1-mini",   // higher RPM (12) — fallback for rate-limit storms
    "gpt-image-2"         // last resort — slow + tightly gated, but premium when it succeeds
  ]
}
`

### Why this matters for the deployment-ladder code
The 5/10/15s exponential-backoff retry policy in the recipe assumes 3 retries per deployment then move on. **Within an animation cycle, every frame must come from the same model** — switching mid-animation (e.g., frames 0-3 on image-1.5, frames 4-7 on mini) produces visually different characters across frames and breaks the animation. The retry budget protects continuity; the ladder protects throughput across DIFFERENT entities.

### Operator action when bringing up a new project
1. Provision all 4 deployments (or at least 3) in your Azure OpenAI resource
2. Set RPM in Azure portal (Quotas tab) — the defaults (~6 RPM) are too tight for parallel image-gen
3. Set `AzureOpenAIImage.PrimaryDeployment = "gpt-image-1.5"` for highest quality
4. Run the Develop wizard's "Validate Image Gen" smoke test — POSTs a tiny prompt to the primary deployment

### Anti-pattern to avoid
- "We'll just use one model and rely on retries" — this works for low-volume (<10 frames) but at any production scale you WILL hit rate limits. Single-deployment resources are for prototyping, not running.
- Falling back to `gpt-image-1-mini` mid-animation to dodge rate limits — yes you'll get a frame faster, but your goblin's hat will be a different color in frame 5 than in frame 4.

### Key files
- `prompts/_shared/image-gen-instructions.md` — single source of truth for the recipe + deployment ladder + per-call retry policy
- `src/VirtualDevTeam.Core/Configuration/AzureOpenAIImageConfig.cs` — config schema
- `src/VirtualDevTeam.Core/AI/AzureImageAuthProvider.cs` — env var injection for child processes
- `src/VirtualDevTeam.Core/AI/ImageGenerationService.cs` — operator-side smoke test (Validate Image Gen button)


## 102. Strategy Framework Cleanup Race — Three Layers, All Required

**Date:** 2026-05-12
**Context:** The Squad candidate generated 30+ real gpt-image-1 PNGs over 27 minutes (operator visually confirmed quality), but ALL of it was destroyed by the framework's worktree cleanup. Investigation revealed **three independent bugs compounding** — each layer alone wasn't enough.

### What happened
1. Squad finished, exited with code -1 (non-crash, partial completion via the soft-success path)
2. Framework's POST hook called `HasPostBaseCommittableChangesAsync` → returned true (had `.squad/` infra changes)
3. Framework called `ExtractPatchAsync` → patch was extracted successfully
4. Framework called `WorktreeHandle.DisposeAsync` → `git worktree remove --force` succeeded at the git level
5. `Directory.Delete` failed with `"is being used by another process"` — Squad's child Python process still held file locks on `.git/worktrees/squad-.../`
6. After 6 retries with backoff, fallback `Directory.Delete(recursive: true)` partially succeeded — wiped working tree contents but couldn't remove the dir itself
7. Framework moved on to evaluation — but `CountModifiedFilesAsync` had been using `git diff --name-only` (which IGNORES untracked files), so the 30+ untracked PNGs were never staged or committed
8. Patch was effectively empty for sprite content; commit was never created; PNGs gone forever

### The three-layer fix (all required)

**Layer 1 — Auto-commit before patch extraction (PREVENTS loss-by-omission):**
`GitWorktreeManager.ExtractPatchAsync` now runs `git status --porcelain` after `git add -A`; if dirty, commits with `--no-verify` + `-c core.hooksPath=NUL` (bypasses worktree's potentially-mutated hooks). Patch generation runs against COMMITTED history. Without this, untracked files were never durable.

Also: `CountModifiedFilesAsync` was switched from `git diff --name-only` to `git status --porcelain --untracked-files=all` so the framework's "files modified" count is truthful for untracked content.

**Layer 2 — Process tree drain before cleanup (PREVENTS file-lock race):**
`RunnerProcessJob.WaitForDescendantsAsync(rootPid, TimeSpan.FromSeconds(10))` gives child processes a 10s grace period after their parent exits. Lets OS file handles release before `git worktree remove` tries to delete the dir. Wired into both `SquadFrameworkAdapter.RunSquadProcessAsync` and `CopilotCliProcessManager.RunAgenticSessionAsync` between "process exited" and "request worktree cleanup".

**Layer 3 — Ref preservation (RECOVERS if Layer 1+2 fail):**
`WorktreeHandle` now carries `TaskId` + `StrategyId`. `RemoveWorktreeQuietAsync` calls `git update-ref refs/candidates/{taskId}/{strategyId} HEAD` BEFORE any cleanup attempt. Even if cleanup destroys the worktree dir, the commit stays git-reachable from a stable ref — no `git fsck --unreachable` archaeology needed for recovery. `CandidateEvaluator` can resurrect the work from this ref.

### The new contract (sequence)
`
candidate process exits
    ↓
WaitForDescendantsAsync(10s)  ← Layer 2: drain children
    ↓
ExtractPatchAsync (which auto-commits via Layer 1) ← Layer 1: persist
    ↓
update-ref refs/candidates/{runId}/{strategyId} HEAD  ← Layer 3: preserve
    ↓
git worktree remove --force   ← can now safely fail; ref keeps the commit
`

### Why all three (not just Layer 1)
- Layer 1 alone covers 99% of cases. But if `git commit` itself fails (lock contention, hooks fighting back), work still drops on the floor.
- Layer 2 alone fixes the file-lock race but doesn't help when the candidate never staged its files (the actual 2026-05-12 case — Squad's `git status` was non-empty for 30 PNGs but Squad never ran `git add`).
- Layer 3 alone is recovery only — work was lost from the eval pipeline even if the commit is salvageable later.

### Companion: live artifact streaming
While the framework is running, `CandidateArtifactWatcher` polls the worktree every 5s for newly-created/modified files (PNG/JSON/code) and emits a `FrameworkActivityEvent` per file. The Strategy Framework dashboard renders these as `🎨 path/to/image.png (size KB)` activity entries with timestamps. Operator sees assets land in real time instead of waiting for post-execution. Excludes scaffolding (`.git`, `.squad`, `.copilot`, `.sandbox`, `node_modules`, `bin`, `obj`).

### Lesson
**Defensive depth in cleanup paths is mandatory when the cleanup target contains generative work.** Any framework that produces outputs in a sandboxed worktree must commit BEFORE teardown, drain children BEFORE removing, and preserve refs as a backup. None of these alone is sufficient.

### Key files
- `src/VirtualDevTeam.Core/Strategies/GitWorktreeManager.cs` — Layer 1 auto-commit + Layer 3 ref preservation
- `src/VirtualDevTeam.Core/AI/RunnerProcessJob.cs` — `WaitForDescendantsAsync` for Layer 2
- `src/VirtualDevTeam.Core/Frameworks/SquadFrameworkAdapter.cs` + `Strategies/AgenticDelegationStrategy.cs` — wired the drain at the right moment
- `src/VirtualDevTeam.Core/Frameworks/CandidateArtifactWatcher.cs` — live streaming companion


## 103. CLI Candidate's Missing Image-Gen Env Vars Was a One-Line DI Bug

**Date:** 2026-05-12
**Context:** The agentic CLI candidate kept logging "No Azure OpenAI credentials are available" and producing zero PNGs while the Squad sibling (using the same auth provider, same env-var injection helper) worked perfectly. Days of suspecting credential rotation, IOptionsMonitor races, env var scrubbing, and config postconfigure ordering. Actual cause: a one-line DI factory mistake.

### The bug
`SemanticKernelExtensions.AddSemanticKernelModels()` registered `CopilotCliProcessManager` with this factory:

`csharp
services.AddSingleton<CopilotCliProcessManager>(sp =>
    new CopilotCliProcessManager(config, frameworkConfig, gate, logger, monitor));
`

Five constructor args. But the constructor signature is:

`csharp
public CopilotCliProcessManager(
    IOptions<VirtualDevTeamConfig> config,
    IOptions<StrategyFrameworkConfig> frameworkConfig,
    StrategyConcurrencyGate globalGate,
    ILogger<CopilotCliProcessManager> logger,
    IOptionsMonitor<VirtualDevTeamConfig>? configMonitor = null,
    RunnerProcessJob? runnerJob = null,
    IAzureImageAuthProvider? imageAuth = null)   // ← never passed → defaults to null
`

`_imageAuth` was always null. `ApplyImageGenEnvVars` short-circuits silently when `_imageAuth is null`. CLI children never saw `AZURE_OPENAI_IMAGE_*` env vars.

Why Squad worked: `SquadFrameworkAdapter` is registered with parameterless `services.AddSingleton<SquadFrameworkAdapter>()`, which lets the DI container populate ALL constructor parameters (including the optional `IAzureImageAuthProvider`).

### The lesson — for ALL factory registrations
**When using services.AddSingleton<T>(sp => new T(...)) factory lambdas, you must explicitly pass EVERY optional ctor parameter via sp.GetService<>(). The container does NOT auto-populate them like it does for parameterless ctor injection.**

Easy class of regression: someone adds a new optional dependency to a constructor and forgets to update the factory registrations. The default-value `null` then silently disables features. When reviewing PRs that add ctor params or DI registrations, scan all `AddSingleton<T>(sp => new T(...))` factory calls in the codebase to confirm they pass the new optional parameter.

### The fix (commit `c68a30b`)
`csharp
services.AddSingleton<CopilotCliProcessManager>(sp =>
{
    var config = sp.GetRequiredService<IOptions<VirtualDevTeamConfig>>();
    // ... required services ...
    var runnerJob = sp.GetService<RunnerProcessJob>();        // ← now passed
    var imageAuth = sp.GetService<IAzureImageAuthProvider>();  // ← now passed
    return new CopilotCliProcessManager(config, frameworkConfig, gate, logger, monitor, runnerJob, imageAuth);
});
`

Use `GetService` (not `GetRequiredService`) for optional parameters so the runner can still boot in test harnesses that don't register these singletons.

### Key files
- `src/VirtualDevTeam.Core/Configuration/SemanticKernelExtensions.cs:27-46` — the fix
- `src/VirtualDevTeam.Core/AI/CopilotCliProcessManager.cs:151` — the silent-null-guard that made this hard to debug

## 100. SE Must Not Add `tests-added` — TE Owns the Testing Lifecycle

**Date:** 2026-05-14

**What happened:** The SE's T-FINAL path added the `tests-added` label directly after strategy build-verify, bypassing the Test Engineer entirely. The PM agent's defense-in-depth check requires BOTH the `tests-added` label AND a TE completion comment before reviewing. Since TE was bypassed, no comment appeared — the PM silently skipped PR #1628 every poll cycle for 6+ hours. FlowMonitor escalated 5 times, all blaming PM (wrong target).

**Root cause chain:** SE adds `tests-added` → TE sees label, skips PR (no comment posted) → PM requires TE comment, skips PR → PM appears stuck → FlowMonitor blames PM instead of TE.

**Fix:** Remove `tests-added` from both T-FINAL SE paths (strategy + legacy). TE handles T-FINAL: for 0 changed files → posts "[TestEngineer] No Tests Needed" + applies `tests-added`. For code changes → normal AI testability assessment. SE merge gate conditional: `requireTestsAdded = IsInlineTestWorkflow && TestEngineerReviews` (prevents deadlock when TE disabled).

**Lesson:** Never bypass an agent's role by pre-applying their output label. Each agent must own its lifecycle contribution end-to-end (label + comment + assessment). The PM's defense-in-depth comment check was correct — the bug was in the SE shortcutting the lifecycle.

### Key files
- `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs` — removed `tests-added` from both T-FINAL paths
- `src/VirtualDevTeam.Agents/TestEngineerAgent.cs:481-497` — empty T-FINAL PR handling
- `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs:4774` — conditional merge gate

## 101. FlowMonitor Rung-2 PR Comments Are Never Read by Agents

**Date:** 2026-05-14

**What happened:** GPT-5.5 research + Sonnet rubber duck confirmed that FlowMonitor's rung-2 escalation (`PostExplicitAskAction`) posts PR/Issue comments asking agents to respond — but NO agent code parses or reacts to FlowMonitor comments. Agents only read their own protocol markers (e.g., PM looks for "[TestEngineer]", SE for "[SoftwareEngineer]"). 4+ identical rung-2 comments were posted on the same issue within minutes.

**Root cause:** Rung-2 was designed as human-readable audit trail, not agent-parseable protocol. In a fully autonomous system with no human monitoring agent GitHub accounts, it's pure noise.

**Fix:** FlowMonitor diagnostic enrichment added — findings now include ✅/❌ diagnostic checklist explaining WHY an agent is stuck, with recommended fix actions. Approvals page shows collapsible details instead of verbose wall of text. Future: simplify to 2-rung ladder (actionable nudge → human escalation with diagnostics).

### Key files
- `src/VirtualDevTeam.Core/HealthMonitor/Diagnostics/PrLifecycleDiagnosticEnricher.cs` — enricher
- `src/VirtualDevTeam.Core/HealthMonitor/Actions/EscalateToHumanAction.cs` — notification includes diagnostics
- `src/VirtualDevTeam.Dashboard/Components/Pages/Approvals.razor` — collapsible details UI

## 102. Multi-Process Preview for Split API + Frontend Architectures

**Date:** 2026-05-14

**What happened:** The Compliance project has a separate .NET API backend (port 5062) and Vite/React frontend (port 5173). `DetectAppStartCommand` prompt says "determine the SINGLE command" — it picked `dotnet run` for the API. Playwright navigated to the API root URL (JSON endpoints, no HTML) → 8512-byte white screenshot on every candidate.

**Fix:** Added `TryDetectCompanionFrontend` — scans for `package.json` with dev script + `index.html` alongside a .NET API. `StartCompanionProcessAsync` starts the frontend headless. Playwright navigates to frontend URL. Both processes cleaned up in `finally` block. Validated: 32,656-byte screenshot with 250 chars of real UI content.

**Lesson:** Preview pipelines must support multi-process architectures. The "detect single start command" pattern fails for any project with separate backend + frontend.

### Key files
- `src/VirtualDevTeam.Core/Workspace/PlaywrightRunner.cs` — `TryDetectCompanionFrontend`, `StartCompanionProcessAsync`

## 103. Always Re-Fetch PR Labels After MarkReadyForReviewAsync

**Date:** 2026-05-14

**What happened:** The T-FINAL path called `MarkReadyForReviewAsync` (which swaps `in-progress` → `ready-for-review` via `UpdateAsync`), then immediately used the ORIGINAL `pr.Labels` object to add `tests-added`. The `UpdateAsync` at line 6846 replaced the entire label set with `in-progress` + `tests-added` — silently overwriting the `ready-for-review` swap. PR #1628 was stuck with `in-progress` for 6+ hours.

**Lesson:** GitHub's label API replaces the entire label set atomically (Lesson #4 restatement). After ANY call that modifies labels (especially `MarkReadyForReviewAsync`), you MUST re-fetch the PR to get current labels before ANY subsequent label write. The original `pr` object is stale.

### Key files
- `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs:6842` — documented lesson at code site

## 104. Centralized PR Lifecycle via PrLifecycleCalculator

**Date:** 2026-05-14

**What happened:** PR lifecycle logic was scattered: agents checked labels directly, FlowMonitor detectors had hardcoded label predicates, dashboard UI parsed comments. No single source of truth for "what stage is this PR in." Configuration variants (TE enabled/disabled, SinglePR, gate preferences) weren't consistently applied.

**Fix:** Created `PrLifecycleCalculator` — pure stateless calculator in Core that derives stages from labels + comments + config. Stages built dynamically (not hardcoded matrices). 6 stages: Dev → Architect → Peer Review → Testing → PM → Merge. `PrLifecycleTimeline.razor` component renders horizontal timeline with emoji icons, CSS animations. Integrated into ProjectTimeline PR detail popup. 14 unit tests covering all config combinations.

**Lesson:** Lifecycle state computation should be centralized and config-aware. Scattered label checks across agents, detectors, and UI components inevitably diverge.

### Key files
- `src/VirtualDevTeam.Core/Lifecycle/PrLifecycleCalculator.cs`
- `src/VirtualDevTeam.Core/Lifecycle/PrLifecycleModels.cs`
- `src/VirtualDevTeam.Dashboard/Components/Shared/PrLifecycleTimeline.razor`
- `tests/VirtualDevTeam.Core.Tests/PrLifecycleCalculatorTests.cs`

## 105. ADO ListAllAsync Must Hydrate Labels for Dashboard/Lifecycle

**Date:** 2026-05-14

**What happened:** ADO's `ListAllAsync` (used by `ListAllForProjectAsync` → `DashboardDataService` → Timeline page) mapped PRs without hydrating labels. ADO PR list responses do NOT include labels (separate `/labels` endpoint required). The lifecycle calculator received empty labels → showed all stages as NotStarted. `ListOpenAsync` already hydrated labels correctly, but `ListAllAsync` was missed.

**Fix:** Added per-PR label hydration in `AdoPullRequestService.ListAllAsync`. Bounded by `$top=200` and cached via `ListOpenTtl`.

**Lesson:** When adding new consumers of PR data (lifecycle calculator, dashboard timeline), verify that BOTH platform providers hydrate all required fields. ADO's list endpoints are more restrictive than GitHub's — labels, description, and linked items often need separate API calls.

### Key files
- `src/VirtualDevTeam.Core/DevPlatform/Providers/AzureDevOps/AdoPullRequestService.cs:172-184`

## 106. ExtractTestUrlPaths Literal \n Parsing Bug

**Date:** 2026-05-15

**What happened:** Issue body text from GitHub contains literal `\n` escape sequences (from JSON serialization), not actual newlines. `String.Split('\n')` in `ExtractTestUrlPaths` missed all lines because the text contained the two-character sequence `\n` rather than the newline character. The `## Visual Verification` section with test URLs was never parsed, so MCP exploration had no URLs to navigate.

**Fix:** Normalize `\\n` to `\n` before parsing issue body text for structured sections like `## Visual Verification`.

**Lesson:** Always normalize literal escape sequences in text that has passed through JSON serialization boundaries (GitHub API responses, work item descriptions). `String.Split('\n')` only matches the newline character, not the two-character escape sequence.

## 107. MCP Exploration Prompt Ordering Matters

**Date:** 2026-05-15

**What happened:** Test URLs were injected AFTER the Steps section in the MCP exploration prompt. The AI agent read instructions top-down and saw "navigate to URLs listed above" — but the URLs were below that instruction. The agent never navigated to the test URLs because it followed instructions literally and there were no URLs "above."

**Fix:** Restructure prompts to put critical data (test URLs, acceptance criteria) at the TOP, before instructions that reference them.

**Lesson:** LLM agents process prompts sequentially. Forward references ("see below", "URLs listed above" when they're actually below) are unreliable. Place all referenced data before the instructions that use it.

## 108. Don't Dump Full Task Description into MCP Prompts

**Date:** 2026-05-15

**What happened:** The full 2000+ character issue body — containing markdown formatting, JSON decision logs, implementation notes, and acceptance criteria — was injected into the MCP exploration prompt as context. The actual test URL (`/swagger`) was buried in noise. The AI agent focused on the verbose context instead of the simple task: navigate to a URL and take a screenshot.

**Fix:** Use concise feature titles and extracted URLs for agent context, not raw issue bodies. Strip implementation details that are irrelevant to the exploration task.

**Lesson:** More context ≠ better results. For focused agent tasks (screenshot, verification), provide only the minimum context needed. Raw issue bodies contain implementation details that distract from the actual task.

## 109. Strategies.razor _refreshing Guard Dropped Bursty SignalR Events

**Date:** 2026-05-15

**What happened:** During strategy framework evaluation, 12+ media events (screenshots, videos) arrive per candidate in tight bursts via SignalR. The `if (_refreshing) return;` guard in `Strategies.razor` silently dropped all events except the first in each burst. Media data was correctly stored in `CandidateStateStore` but the page never re-pulled it because refresh requests during an active refresh were discarded.

**Fix:** Coalesce pattern — when a refresh is already in progress and a new event arrives, set a `_pendingRefresh` flag. When the current refresh completes, check the flag and re-queue another refresh. This ensures no events are permanently lost while still preventing concurrent refresh storms.

**Lesson:** `if (busy) return;` guards silently lose data under bursty event streams. Use a coalesce pattern (pending flag + re-check after completion) for any UI refresh driven by high-frequency events.

## 110. TestArtifactIndexService 30-Second Cache + Browser Negative Caching

**Date:** 2026-05-15

**What happened:** `TestArtifactIndexService` has a 30-second cache for performance. When `GetArtifactById` was called for a file written after the last index scan, it returned null (cache miss). The deterministic hash-based artifact URL returned a 404. The browser negatively cached the 404 response on that URL, so even after the 30-second cache expired and the index rescanned (now finding the file), the browser never retried the same URL.

**Fix:** Force rescan on cache miss — when `GetArtifactById` returns null, immediately re-index before returning. This ensures newly written files are found on first request rather than waiting for cache expiry.

**Lesson:** Deterministic/stable URLs combined with negative caching create permanent 404s for late-arriving files. Either use cache-busting URLs, force rescan on miss, or add `Cache-Control: no-store` to 404 responses for artifact endpoints.

## 111. CandidateVideoReadyEvent Is Dead Code

**Date:** 2026-05-15

**What happened:** `CandidateVideoReadyEvent` is defined as an event type but is never published anywhere in the codebase. `RecordVideoReady` in `CandidateStateStore` is unreachable. Media paths (screenshots, videos) only flow through `CandidateEvaluatedEvent`. The dead event type creates confusion when debugging media pipeline issues — it looks like video has a separate event path but it doesn't.

**Lesson:** Remove or document dead event types. In event-driven architectures, unused event definitions are misleading — they imply a code path exists that doesn't. Periodic audits of event publish/subscribe pairs catch these.

## 112. npm Is npm.cmd on Windows

**Date:** 2026-05-15

**What happened:** `ProcessStartInfo("npm", "install")` with `UseShellExecute=false` fails on Windows because `npm` is a `.cmd` batch file, not a `.exe`. The OS doesn't resolve `.cmd` extensions without a shell. The process silently fails to start or throws a Win32 exception.

**Fix:** Route through `BuildRunner.ParseCommand` which wraps `.cmd`-based tools as `cmd /c npm install`. Same applies to `npx`, `gh`, `squad`, `az`, and any other tool distributed as a `.cmd`/`.bat` wrapper on Windows.

**Lesson:** On Windows, many Node.js and CLI tools are `.cmd` batch files. Any `ProcessStartInfo` with `UseShellExecute=false` must either use `cmd /c` wrapping or explicitly append `.cmd` to the executable name. This is a recurring class of "works in terminal, fails in code" bugs.

## 113. ModelPricing Suffix Matching

**Date:** 2026-05-15

**What happened:** The model ID `claude-opus-4.6-1m` didn't match the exact string `claude-opus-4.6` in the pricing table, falling through to the default Sonnet pricing tier. Costs were underestimated by 5x for the entire session because Opus calls were priced as Sonnet.

**Fix:** Use `StartsWith` matching for model families to handle context-window suffixes (`-1m`, `-high`, `-xhigh`, etc.) that don't change the pricing tier.

**Lesson:** Model IDs have variable suffixes for context window sizes and capability tiers. Pricing lookups must use prefix/family matching, not exact string equality. New suffixes are added regularly by providers — exact matching creates a maintenance burden and silent cost miscalculation.

## 114. T-FINAL Must Wait for All Dependency PRs to Merge

**Date:** 2026-05-15

**What happened:** T-FINAL (integration PR creation) started while T4 was still in review, because the dependency check used issue state (open/closed) rather than PR state (merged/open). The issue was closed (task marked done) but the PR hadn't merged yet. T-FINAL's integration PR had 22 merge conflicts because T4's code wasn't in the target branch.

**Fix:** `CreateIntegrationPRAsync` now checks for open engineering PRs (by agent display name) before starting. If any are still open, it waits rather than creating a conflicting integration PR.

**Lesson:** Task completion (issue closed) ≠ code merged (PR merged). Integration steps that depend on all code being in the target branch must check PR merge state, not issue state. This is especially important in multi-wave task pipelines where later waves depend on earlier waves' code.

## 115. SE Reworks Already-Merged PRs

**Date:** 2026-05-15

**What happened:** `HandleChangesRequestedAsync` checked `_mergedPrNumbers` to skip rework on already-merged PRs, but that set only tracks PRs merged by the SE leader agent — not PRs merged by worker agents. When the Architect posted suggestions on a worker-merged PR, the SE leader didn't recognize it as merged and triggered a rework cycle on a closed PR.

**Fix:** API fallback check via `PrService.GetAsync` — when a PR isn't in the in-memory `_mergedPrNumbers` set, fetch its current state from the platform API before attempting rework.

**Lesson:** In-memory state sets are always incomplete after restarts or when multiple agents contribute to the same tracking set. Always fall back to the platform API as source of truth before taking destructive actions (rework, close, recreate).

## 116. FlowMonitor Rung-2 PR Comments Confirmed Unread Noise (Reinforces Lesson #101)

**Date:** 2026-05-15

**What happened:** `PostExplicitAskAction` posted 6+ identical escalation comments on PR #1676 from the `image-regen-anomaly` detector. No agent parses FlowMonitor comments — agents only read their own protocol markers. The comments created visual noise in the PR timeline without any corrective effect.

**Fix:** Disabled PR comment posting in rung-2; log internally only. Rung-2 now serves as an internal severity escalation step rather than an external communication.

**Lesson:** Escalation actions that produce no observable effect should be removed or repurposed. In autonomous systems, PR/issue comments are only useful if an agent or human is known to read them. Default to internal logging and reserve platform writes for actions with verified consumers.

## 117. Scenario Approval Status Not Persisted

**Date:** 2026-05-15

**What happened:** `ApproveAll`, `Approve`, `Reject`, and `SaveEdit` in `ScenarioReview.razor` updated in-memory scenario status but never called `PersistScenariosAsync()`. On Runner restart, all scenarios reverted to `Proposed` status — losing all approval decisions. Operators had to re-approve every scenario after each restart.

**Fix:** Fire-and-forget `PersistScenariosAsync()` call on every status change (approve, reject, edit save).

**Lesson:** Any UI action that changes persistent state must write-through to durable storage immediately. In-memory-only updates in Blazor Server are lost on circuit disconnect, server restart, or process crash. This is especially critical for approval workflows where re-doing work is costly.

## 118. AgentStuckDetector False Positives During Strategy Framework Runs

**Date:** 2026-05-15

**What happened:** Strategy framework evaluation sessions run 10–30+ minutes with unchanged status text (e.g., "Strategy candidates: evaluating 3 candidates"). The `AgentStuckDetector` used a flat 45-minute threshold for all agents. Agents in legitimate long-running strategy evaluations were flagged as stuck, triggering unnecessary escalation.

**Fix:** Recognize long-running activities via `StatusReason` text patterns (e.g., "Strategy candidates", "evaluating") and apply a 3x threshold multiplier for those activities.

**Lesson:** Stuck detection thresholds must be activity-aware. A single global threshold produces false positives for legitimately long-running operations and false negatives for operations that should complete quickly. Use status text or activity type to select appropriate thresholds.

## 119. Inline + Summary Review Comment Duplication

**Date:** 2026-05-15

**What happened:** SE worker agents posted inline review comments via `CreateReviewWithInlineCommentsAsync` (which includes `reviewBody` as the review summary), then separately posted the same `reviewBody` as a standalone PR comment. Reviewers saw the identical review text twice — once in the review summary and once as a standalone comment.

**Fix:** Set `reviewBody = null` after inline submission to prevent the duplicate standalone comment.

**Lesson:** GitHub's review API bundles the summary body with inline comments atomically. Posting the summary separately afterward creates duplication. When using `CreateReviewWithInlineCommentsAsync`, the review body is already posted — don't re-post it.

## 120. Health Probe Before MCP Exploration Saves 2+ Minutes

**Date:** 2026-05-15

**What happened:** MCP exploration sessions spent 138+ seconds making `browser_navigate` tool calls against crashed or dead preview applications. The Playwright MCP server dutifully attempted navigation, waited for timeouts, and reported failures — all of which could have been detected in 5 seconds with a simple HTTP GET.

**Fix:** Add a 5-second HTTP GET health probe before starting MCP exploration. If the probe fails, skip the expensive agentic session and report the app as down immediately.

**Lesson:** Always validate preconditions cheaply before expensive operations. A 5-second health probe prevents 2+ minutes of wasted agentic runtime. This applies broadly: check file existence before parsing, verify service health before integration tests, ping endpoints before screenshot sessions.

## 121. TruncateForPrompt Loses Important Context

**Date:** 2026-05-15

**What happened:** `TruncateForPrompt` hard-cut PMSpec and Architecture documents at 3000 characters before injecting into agent prompts. This dropped non-functional requirements, extensibility notes, and visual design specifications — exactly the content that downstream agents (engineers, reviewers) needed for quality implementation.

**Fix:** Removed truncation entirely. With 1M context window models (claude-opus-4.7-1m), the 3000-char limit was a legacy constraint from smaller context windows. Full documents are now passed through.

**Lesson:** Context window limits from earlier model generations create artificial constraints that silently degrade output quality. Periodically audit truncation thresholds against current model capabilities. When context windows are large enough, removing truncation is simpler and more reliable than tuning cut points.

## 122. FreshPathResolver for Tools Installed After Runner Start

**Date:** 2026-05-15

**What happened:** Tools installed via `winget` during the Welcome wizard (ffmpeg, copilot, squad, az, gh) were invisible to `Process.Start` because the Runner process inherits the PATH environment variable from its launch time. New PATH entries added by installers don't propagate to already-running processes on Windows.

**Fix:** Created `FreshPathResolver` — a central Core helper that reads Machine + User PATH directly from the Windows registry and resolves executables including `.cmd`/`.bat` extensions. Used by process launch code instead of relying on the inherited PATH.

**Lesson:** Long-running .NET processes on Windows inherit a snapshot of PATH at startup. Tools installed mid-session (via winget, npm -g, or manual install) require explicit PATH refresh from the registry. This is a Windows-specific issue — Linux processes can re-read `/etc/environment` but Windows PATH is baked into the process environment block.


## 123. Visual Score Winner Selection Was Dead Code (VisualsScore Sort Ran After Winner Locked)

**Date:** 2026-05-16

**What happened:** `ApplyVisualScoresAsync` ran at line 300 of `CandidateEvaluator.cs`, AFTER winner selection at line 277. The `ThenByDescending(VisualsScore)` in the sort was dead code � VisualsScore was always null/-1 at sort time. When LLM judge scores tied (both 25/30), alphabetical tiebreak picked copilot-cli (broken error page, Visual=1) over squad (working app, Visual=6).

**Fix:** Moved `ApplyVisualScoresAsync` to run before the sort. Also re-resolve winner from results after scoring (creates new instances via `record with`).

**Lesson:** When evaluation pipelines have multiple scoring stages, execution order is everything. A sort that references a field populated by a later stage silently degrades to ignoring that field. Test tie-breaking with equal scores to verify all tiebreak criteria are actually populated at decision time.

## 124. Binary-Quality Gate False Positive Rejects Non-Art Tasks

**Date:** 2026-05-16

**What happened:** Sole-survivor binary-quality gate rejected Squad for T-FINAL with score 0/100 because 4 neutral binaries (build artifacts like .dll/.pdb, not images) triggered `TotalCount > 0 && Score < 30`. The gate was designed for AI-art detection but fired on any task producing binary files.

**Fix:** Changed condition to `(RealCount + FakeCount) > 0` so neutral-only results (non-art tasks) don't trigger rejection.

**Lesson:** Quality gates with broad trigger conditions (`TotalCount > 0`) will false-positive on categories they weren't designed for. When a gate targets a specific concern (AI-generated art), the trigger condition must match only that concern's classification buckets, not the superset.

## 125. Dashboard Refresh Buttons Calling ResetCaches Killed All Agents

**Date:** 2026-05-16

**What happened:** Timeline and Overview force-refresh buttons called `DataService.ResetCaches()` which called `AgentSnapshotService.ResetCaches()` � clearing `_trackedAgents.Clear()`. Agents were still running in the orchestrator but invisible to the dashboard. From the user's perspective, all agents vanished.

**Fix:** Refresh buttons now only reload display data, never call `ResetCaches`. `ResetCaches` is reserved for Configuration page cleanup flow only.

**Lesson:** "Refresh" and "reset" are fundamentally different operations. A UI refresh should re-read state, not destroy it. Cache-clearing methods that discard tracking state should never be exposed on routine user actions. Guard destructive cache operations behind explicit confirmation or restrict to admin-level flows.

## 126. T-FINAL CreateIntegrationPRAsync Re-Invoked 3x via Safety Check Title Mismatch

**Date:** 2026-05-16

**What happened:** The safety check at line 567 searched for "Final Integration" in PR titles. If the title didn't match (e.g., renamed or the search was too narrow), it reset `_integrationPrCreated=false` and called `CreateIntegrationPRAsync` again, which ran the full strategy framework each time � 3 redundant invocations.

**Fix:** Added `CurrentPrNumber` to the search criteria + recreate counter (max 1 attempt).

**Lesson:** Safety checks that reset boolean flags can become infinite-retry loops when the verification condition doesn't match the creation condition. Always include a retry counter or use the concrete resource identifier (PR number) instead of text-matching to verify existence.

## 127. ScenarioReview Child Component Re-Read Stale develop-settings.json from Bin Directory

**Date:** 2026-05-16

**What happened:** `ScenarioReview` called `SettingsService.LoadAsync()` independently which read from the bin directory copy (0 scenarios) while the parent Develop page already had 12 scenarios loaded from the src copy. The child component showed an empty state despite the parent having data.

**Fix:** Pass `PersistedScenarios` + `PersistedScenarioHash` as parameters from parent (same pattern as ClarifyingAnswers).

**Lesson:** Blazor child components that independently load settings files may read a different copy than their parent. In development, `bin/` and `src/` copies of the same file can diverge. Pass loaded data as component parameters instead of having children re-read files. This is the standard Blazor data-flow pattern for a reason.

## 128. FlowMonitor Rung-2 Issue Comments Still Posting Despite Lesson #28 Disabling PR Comments

**Date:** 2026-05-16

**What happened:** The `PostExplicitAskAction` suppressed PR comments (log only) per Lesson #28 but the issue comment path at lines 142�159 still called `AddCommentAsync`. No agent parses FlowMonitor comments on issues either. The partial fix left one code path active.

**Fix:** Suppressed issue comments too � all rung-2 paths now log-only.

**Lesson:** When disabling a behavior across multiple code paths, audit ALL paths � not just the one that was reported. The same "post comment" logic existed for both PRs and issues; fixing only PRs left the issue path active. Search for all call sites of the suppressed API method, not just the one in the bug report.

## 129. PlaywrightRunner.cs Grew to 219KB (4766 Lines) � AI Code Bloat

**Date:** 2026-05-16

**What happened:** Classic AI-assisted accumulation: each feature was appended to one class instead of extracting services. 38% of the file was string literals (84KB of inline prompt templates and HTML). 7 unrelated concerns lived in one file: app launching, screenshot capture, MCP exploration, video recording, GIF generation, API smoke testing, HTML rendering.

**Fix:** Refactored: extracted `AppLauncher.cs` (1720 lines), `MediaRecorder.cs` (276 lines), `ApiSmokeRunner.cs` (254 lines). `PlaywrightRunner` reduced to 2603 lines as a facade delegating to extracted services.

**Lesson:** AI code generation naturally accumulates in the file that's open. Without periodic extraction, classes grow monotonically. Set a hard size budget (~1000 lines) and extract when exceeded. Inline string literals (prompts, templates, HTML) are the biggest hidden contributor � they don't feel like "code" but dominate file size. Track file sizes in CI or code review checklists.

## 130. CaptureAppScreenshotAsync Ran Full Video+GIF Pipeline Then Discarded Output

**Date:** 2026-05-16

**What happened:** The refactored delegation from `CaptureAppScreenshotAsync` ? `CaptureAppInteractionAsync` meant every ready-for-review screenshot request ran MCP exploration + video recording + GIF generation (2�3 min) only to delete the artifacts afterward since only the screenshot was needed.

**Fix:** Added `CaptureMode.ScreenshotOnly` that skips MCP + video/GIF, going straight to `DirectCapture`.

**Lesson:** When refactoring to delegate through a higher-level method, verify the callee doesn't perform expensive side-effects the caller doesn't need. Delegation should narrow scope, not widen it. Add explicit mode parameters (enums > booleans) so callers declare exactly what they need.

## 131. No "Is This a UI Task?" Pre-Flight Check Before Playwright Capture

**Date:** 2026-05-16

**What happened:** Every task (including pure backend/library PRs with no UI) paid 2�3 minutes of Playwright startup and capture cost. The system assumed all tasks produce visible UI output.

**Fix:** Added `MediaCaptureGate.ShouldCapture()` that checks for Visual Verification sections in the issue body, UI file extensions (.razor, .tsx, .css, .html), UI keywords, and swagger/API patterns before spawning Playwright.

**Lesson:** Expensive optional operations should have a cheap pre-flight gate. Don't run screenshot/video capture on tasks that can't possibly produce visual output. Pattern: check issue metadata and file extensions before spawning heavyweight tools. The gate should be conservative (capture when uncertain) but filter obvious non-UI work.

## 132. Strategies Page Duplicate @key Crash from Multiple Strategy Runs per Task

**Date:** 2026-05-16

**What happened:** Multiple strategy framework runs for the same task (retries, re-evaluations) produced duplicate `RunId+TaskId` entries in `_recentTasks`, causing Blazor `InvalidOperationException` on duplicate `@key` values. The page crashed on render.

**Fix:** Dedup by `GroupBy RunId+TaskId` in both UI (`Strategies.razor`) and data layer (`CandidateStateStore.PushRecent`).

**Lesson:** Blazor `@key` values must be unique within a render loop � duplicates crash the differ. Any data source that can produce duplicates (retries, re-runs, event replays) needs deduplication before rendering. Apply dedup at the data layer (`PushRecent`) so all consumers are safe, and defensively in the UI as a second layer.
## 133. TestRunner/BuildRunner Unbounded Pipe Reads Blocked Indefinitely After Kill

**Date:** 2026-05-16

**What happened:** `ReadToEndAsync` used the outer cancellation token instead of the linked timeout token. After `WaitForExitAsync` timeout + Kill, orphan grandchild processes kept stdout handles open — `await stdoutTask` blocked indefinitely (5+ hours).

**Fix:** Create timeout CTS BEFORE starting IO tasks, use linked token for pipe reads, add bounded 5s/10s drain after kill.

**Lesson:** When killing a process, stdout/stderr pipe reads can block forever if grandchild processes inherit the handles. Always use a separate bounded timeout for post-kill pipe drain — never rely on the outer cancellation token. The kill terminates the direct child but not grandchildren holding the pipe open.

## 134. Wave Gate Must Require PR Merged, Not Just Pushed

**Date:** 2026-05-16

**What happened:** `IsWaveEligible` used `IsTaskPastImplementation` (fires at push) instead of `IsTaskDone` (fires at merge). Later-wave engineers branched from stale main — guaranteed merge conflicts.

**Fix:** Changed gate to `IsTaskDone` and added 30-min grace period fallback to prevent deadlock from stuck merges.

**Lesson:** Wave dependency gates must check the strongest completion signal (merge), not an intermediate one (push). Branching from main before dependencies are merged guarantees conflicts. Grace period fallbacks are essential to prevent the entire pipeline from deadlocking on a single stuck merge.

## 135. CLI Rework Commits Behind the Pipeline's Back

**Date:** 2026-05-16

**What happened:** Copilot CLI can `git add` + `git commit` during rework, leaving the working tree clean. `HandleReworkAsync` checked `git status` which returned empty — misclassified as "no changes" — wasted rework cycles.

**Fix:** Compare HEAD SHA before/after CLI edit to detect commits made by the CLI.

**Lesson:** External tools invoked in a subprocess can commit directly to the repo. Checking `git status` only detects uncommitted changes — it misses committed-but-unpushed work. Always compare HEAD SHA before and after invoking tools that have git access to detect all forms of changes.

## 136. GitHub API Rate Limit Exhaustion from Parallel Agents

**Date:** 2026-05-16

**What happened:** 5 parallel SE strategy candidates + FlowMonitor 30s ticks + review fan-out consumed 8400 API calls in 90 min (5000/hr limit). `RateLimitManager` paused ALL calls for 50 min.

**Fix:** SE per-iteration cache for `ListMergedAsync` (12 call sites), FlowMonitor tick 30s→90s, PR review context cache shared between reviewers.

**Lesson:** Parallel agent architectures multiply API costs non-linearly. Each agent's "reasonable" call rate becomes unreasonable at scale. Cache aggressively at the service layer (not the agent layer) so all consumers benefit. Monitoring loops (FlowMonitor, HealthMonitor) must use the longest interval that still meets SLA — 30s ticks are rarely justified.

## 137. Never Fix Hangs with Timeout Bandaids — Always Find the True Root Cause

**Date:** 2026-05-16

**What happened:** When processes hung, the initial instinct was to add timeouts. But timeouts cause retries that double end-to-end time, and the underlying hang remains — it just manifests as repeated timeout-retry cycles instead of a single block.

**Fix:** Use multiple parallel research agents across different models to identify real root causes before adding any timeout.

**Lesson:** Timeouts are symptoms management, not fixes. A 30-minute timeout on a hang that should complete in 5 seconds means you've accepted 30 minutes of waste per occurrence, multiplied by retry count. Invest the time to find the actual deadlock, resource leak, or blocking call. Parallel research (multiple models, different angles) dramatically accelerates root cause identification.

## 138. FlowMonitor Auto-Approval Must Route Gate-Stuck Findings Directly to AutoApproveGateAction

**Date:** 2026-05-16

**What happened:** `PickActionForRung` always preferred `kick-agent-poll` or `escalate-to-human` — `AutoApproveGateAction` was never selected. Gate-stuck findings must short-circuit to auto-approve, bypassing the escalation ladder.

**Fix:** Gate-stuck findings now short-circuit directly to `AutoApproveGateAction`, bypassing the normal escalation ladder.

**Lesson:** The escalation ladder pattern (nudge → comment → human) assumes the agent can self-recover. For gate-stuck conditions, the agent is blocked on an external approval — no amount of nudging helps. Action routing must match the finding type: self-recoverable findings use the ladder; external-dependency findings bypass it and go directly to the appropriate automated resolution.

## 139. Decision Gates Have No REST API — Only Blazor UI Could Approve Them

**Date:** 2026-05-16

**What happened:** Decision gates could only be approved through the Blazor UI. There was no programmatic way for automated systems (FlowMonitor, scripts, external tools) to approve or reject pending decisions.

**Fix:** Added `/api/decisions/pending`, `/api/decisions/{id}/approve`, `/api/decisions/{id}/reject` REST endpoints for programmatic approval.

**Lesson:** Any human-in-the-loop gate that might need automated bypass MUST have a REST API from day one. UI-only approval creates a hard dependency on a browser session and blocks all automation. The API-first pattern ensures both humans (via UI) and machines (via API) can interact with the same workflow gates.

## 140. Architect Doesn't Pick Up PRs After Restart — In-Memory Bus Messages Lost

**Date:** 2026-05-16

**What happened:** The Architect agent only processes PRs from in-memory `ReviewRequestMessage` bus queue. Bus messages are not persisted — they are lost on process restart. After a restart, PRs with `ready-for-review` label sat unreviewed indefinitely.

**Fix:** Added recovery scan of open PRs with `ready-for-review` label on startup to re-enqueue missed review requests.

**Lesson:** Any agent that relies solely on in-memory message bus delivery for work discovery MUST have a startup recovery scan. The bus is volatile — messages are lost on crash or restart. Recovery should scan the durable platform state (PR labels, issue states) and re-enqueue any work items that were in-flight. This is the same pattern as SE agent restart recovery (Lesson #7 in copilot-instructions) — every agent role needs it.

## 141. DecisionGateService Must Store Decisions Before Plan Generation

**Date:** 2026-05-19

**What happened:** `DecisionGateService.ClassifyAndGateDecisionAsync` previously stored the Pending decision and created the Approvals page notification only AFTER both LLM calls (impact classification + plan generation) completed. If plan generation was slow or hung, the decision was invisible on the Approvals page — `/api/decisions/pending` returned empty and the agent appeared permanently stuck with no way to unblock.

**Fix:** Store the decision in Pending state and create the gate notification immediately after classification (Turn 1). Plan generation (Turn 2) runs afterwards and updates the existing decision. `AgentDecision.Plan` was changed from `init` to `set` to support post-creation plan updates.

**Lesson:** Human approval artifacts must be persisted before any optional or potentially slow follow-up work begins. If visibility into a pending gate depends on a second LLM turn finishing, a slow or hung call can make the whole workflow look deadlocked. Create the pending decision and notification as soon as the gate is known, then enrich the existing record later.

## 142. LocalRepositoryContentService Git Commit "Nothing to Commit" Is Not an Error

**Date:** 2026-05-19

**What happened:** `RunGitAsync` in `LocalRepositoryContentService` threw `InvalidOperationException` when `git commit` returned exit code 1 with "nothing to commit" — this is expected when file content is unchanged (for example, branch marker files). Also, only stderr was captured while stdout was discarded, losing the actual error message.

**Fix:** Capture both stdout and stderr, and tolerate "nothing to commit" for commit commands.

**Lesson:** Git's exit codes are command-specific — exit code 1 from `git commit` can mean a benign no-op rather than a failure. Error handling wrappers around CLI tools must interpret results in the context of the command, not treat every non-zero exit identically. Always capture both stdout and stderr so diagnostics preserve Git's real explanation.

## 143. LocalPullRequestService Must Auto-Add AI-Generated Label

**Date:** 2026-05-19

**What happened:** The Dashboard Timeline page requires the `AI-Generated` label on PRs to create synthetic work item entries for doc phases (Research, PMSpec, Architecture). `GitHubService` adds this label automatically on PR creation, but the new `LocalPullRequestService` did not — causing the Research column to be missing from the Project Timeline.

**Fix:** `CreateAsync` now auto-adds `AI-Generated` to the label list if it is not already present.

**Lesson:** Replacement platform services must preserve hidden behavioral contracts, not just method signatures. If downstream UI or orchestration logic depends on an automatically-added label, parity requires reproducing that side effect in every provider. When introducing a new implementation, compare it against the existing service's implicit behaviors — not only its explicit API surface.

## 144. AzureImageAuthProvider Can Block Blazor Circuits

**Date:** 2026-05-19

**What happened:** `AzureImageAuthProvider` attempting `DefaultAzureCredential` (specifically `VisualStudioCredential`) can timeout and block the entire Blazor server-side rendering pipeline, causing pages like Approvals to hang indefinitely. This manifests as HTTP requests to those pages timing out. A runner restart resolves it, but the underlying issue is synchronous credential acquisition on the render path.

**Fix:** A runner restart clears the immediate symptom, but the real fix is to keep credential acquisition off the synchronous render path and avoid blocking Blazor circuit work on slow Azure credential probes.

**Lesson:** Authentication discovery that can invoke external tooling or network-bound credential providers must never run inline on a Blazor render path. Server-side UI pipelines are extremely sensitive to blocking calls — one slow credential probe can stall the entire circuit and make unrelated pages appear dead. Resolve credentials asynchronously ahead of time, cache them, or move the work behind a non-blocking boundary.

## 145. PM Deadlock From Missing `tests-added` Label

**Date:** 2026-05-21

**What happened:** When the Test Engineer errored out during test addition and `ApplyTestsAddedLabelAsync` also failed (platform error, rate limit, transient API failure), neither the `tests-added` label nor a TE comment reached the PR. The PM's Phase 3 gate in `ProgramManagerAgent.cs:1457` required the `tests-added` label before proceeding, so the PM never reviewed the PR and the pipeline deadlocked silently for hours.

**Fix:** PM now treats TE completion/error comments (`"Test Engineer:"` or `"[TestEngineer]"`) as a fallback signal when the `tests-added` label is missing. The PR comments are fetched once and reused for both the label-fallback path and the defense-in-depth checks, avoiding duplicate API calls.

**Lesson:** Downstream gates must tolerate partial failure of upstream signaling. If one completion signal is a label and the other is a comment, treat them as redundant channels rather than assuming both always arrive.

**Rule:** Always post error/comments BEFORE applying labels, so at least one durable signal reaches downstream agents even when the label write fails.

## 146. WorktreeWorkspace Was Missing Stale-State Cleanup

**Date:** 2026-05-21

**What happened:** `LocalWorkspace` already had comprehensive `AbortInProgressOperationsAsync` cleanup that probes for stale `.git/rebase-merge`, `.git/rebase-apply`, `MERGE_HEAD`, `CHERRY_PICK_HEAD`, and `REVERT_HEAD` and aborts them before git operations. `WorktreeWorkspace` (used in Worktree and InPlace modes) did not. It only had inline `rebase --abort` and `merge --abort` handling inside `CheckoutBranchAsync`, and nothing equivalent in `SyncWithMainAsync`. When TE called `SyncWithMainAsync` on a worktree left behind by a crashed prior run, stale `.git/rebase-merge` state caused `InvalidOperationException: git rebase ...` failures on 3/10 PRs.

**Fix:** Added the full `AbortInProgressOperationsAsync` probe/cleanup flow to `WorktreeWorkspace` and call it at the start of `SyncWithMainAsync`, matching the existing `LocalWorkspace` reliability pattern.

**Lesson:** Workspace implementations that share the same git lifecycle must share the same stale-state recovery behavior. Porting only the happy-path git commands without the crash-recovery probes guarantees mode-specific flakiness.

## 147. Centralize RunScope Filtering in `PullRequestWorkflow`

**Date:** 2026-05-21

**What happened:** RunScope PR filtering logic (`HeadBranch.Contains($"/{runScope}/")` with `Closes #N` body fallback) was duplicated in 5+ places: `EngineerAgentBase.IsCurrentRunScopePr`, `TestEngineerAgent.IsCurrentRunScopePr`, `PullRequestWorkflow.FindExistingPullRequestAsync`, and `ProjectTimeline.razor` (including `ComputeTotalProjectTime`). The copies drifted. `ComputeTotalProjectTime` missed the `Closes #N` fallback entirely, so cross-run adopted PRs were excluded from project-duration calculation.

**Fix:** Created `PullRequestWorkflow.IsCurrentRunScopePr(headBranch, prBody, runScope)` as the shared utility and updated all call sites to delegate to it.

**Lesson:** Duplicated filtering rules inevitably diverge, especially when they encode subtle fallback behavior. Any rule that defines run membership or project scoping should live in one shared utility so fixes land everywhere at once.

**Rule:** When adding new RunScope-aware filters, always use `PullRequestWorkflow.IsCurrentRunScopePr(...)` rather than open-coding branch/body checks.

## 148. `LocalBareRepoManager` Merge Conflicts Need Rebase Fallback

**Date:** 2026-05-21

**What happened:** `LocalBareRepoManager.MergeBranchAsync` originally performed a single `git merge`, and on any conflict immediately ran `merge --abort` and threw. In LocalDevPlatform, parallel PRs touching shared files like `Program.cs` or `.csproj` therefore failed consistently instead of self-healing. The GitHub provider already had a multi-step recovery path (UpdateBranch API → force-rebase → retry merge), but LocalDevPlatform had no equivalent.

**Fix:** Added `TryRebaseAndMergeAsync`. On merge conflict, the manager creates a temporary worktree on the source branch, rebases it onto the target branch, and retries the merge. This rebase is safe because the agent PR branch is exclusively owned by the merge operation. If the rebase also conflicts, the original merge error is still surfaced.

**Lesson:** A local platform provider must match the resilience behavior of the hosted provider, not just its basic merge API. If the remote implementation already has conflict recovery, the local implementation needs a comparable fallback or it will look dramatically less reliable under the same workload.

## 149. CLI Wrapper Freezes Under PowerShell 5.1 — Always Start Runner from PowerShell 7+

**Date:** 2026-05-21

**What happened:** When the runner was launched from Windows PowerShell 5.1 with `CopilotCli.WrapperCommand` set, the wrapper process could start successfully but never spawn `copilot.exe`. The new wrapper liveness watchdog consistently observed an empty child-process tree under PS 5.1, while the same setup launched from PowerShell 7 (`pwsh`) spawned the child CLI within roughly 3 seconds.

**Fix:** `scripts/start-runner.ps1` now hard-fails on PowerShell versions below 7 with a clear "run from pwsh" error. The wrapper liveness watchdog was also updated to probe with `pwsh` first (falling back to `powershell` only if `pwsh` is unavailable) and to log startup plus each empty-child check at Information level so the failure is visible in production logs.

**Lesson:** On Windows, the parent shell environment can materially change wrapper-process behavior. For wrapper-based Copilot CLI sessions, always start the runner from PowerShell 7+ and inspect watchdog logs before adding timeout bandaids or retry loops.

---

## 150. Dead Retention Code — Always Verify Callers Exist

**What happened:** `AgentStateStore.PruneOldEntriesAsync()` was documented in `architecture.md` as "provides retention cleanup" and existed since early development, but had **zero callers** anywhere in the codebase. The `activity_log` and `metrics` tables grew unbounded across runs with no pruning. After months of use, the main DB could reach hundreds of MB of stale activity log entries.

**Fix:** Wired `PruneOldEntriesAsync` into `HealthMonitor`'s existing periodic health check timer (once per 24h, 30-day retention, best-effort — mirrors `FlowMonitorService`'s proven prune pattern). No new background service needed.

**Lesson:** When writing retention/cleanup methods, also write the caller that schedules them. Dead retention code is worse than no retention code — it gives false confidence that cleanup is happening. During code reviews, grep for callers of any "prune", "cleanup", or "retention" method to verify they're actually wired.

---

## 151. Candidate Worktrees Leak After Crashes — Always Add Startup Sweep

**What happened:** Strategy framework candidate worktrees (`.candidates/{taskId}/{strategyId}/`) were cleaned up via `WorktreeHandle.DisposeAsync()` on normal completion. But when the runner was force-killed (crash, SIGKILL, power loss), `DisposeAsync` never ran. The `git worktree prune` call in `CreateAsync` cleaned git metadata, but the physical directories remained on disk. For a large repo, each leaked worktree consumed the full working tree size (potentially hundreds of MB to GB each).

**Fix:** Added `GitWorktreeManager.CleanupStaleCandidateWorktreesAsync()` — runs `git worktree prune` then scans `.candidates/` for physical directories not tracked by `git worktree list`. Called from `HealthMonitor` on the first health tick (one-time startup cleanup). Removes empty parent directories after cleanup.

**Lesson:** Any system that creates temporary resources (worktrees, child processes, temp files) via `IAsyncDisposable` must also have a startup sweep for orphans. Dispose-based cleanup assumes graceful shutdown; crash-resilient systems assume crashes WILL happen and add redundant startup recovery.

---

## 152. Preview Build CancellationToken Must Outlive HTTP Request

**What happened:** The `/api/preview/start` endpoint launched `PreviewBuildService.StartAsync()` via `Task.Run()` but passed the HTTP request's `CancellationToken`. When the HTTP response was sent and the request completed, the token was cancelled, killing the background build. The preview build immediately showed "Stopped" with no error.

**Fix:** Changed to `CancellationToken.None` for the background task. The preview build must outlive the HTTP request that initiated it.

**Lesson:** When launching fire-and-forget background work from an HTTP handler, never pass the request's `CancellationToken` — it cancels when the response is sent. Use `CancellationToken.None` or an application-lifetime token instead.

---

## 153. Preview Build Must Not Use VDT Workspace Config for Target Projects

**What happened:** `PreviewBuildService` used `_config.Workspace.BuildCommand` ("dotnet build") and `_config.Workspace.AppStartCommand` as defaults for the preview target project. These describe the VDT agent workspace (a .NET 8 solution), not the target project agents built (which could be Node.js, Python, or a .NET project in a subdirectory). For the Compliance2 project, `dotnet build` ran in the clone root which had no `.sln` file, causing `MSB1003`.

**Fix:** Preview build now ignores workspace config entirely for build/run commands — only uses user's explicit override or auto-detection. `DetectBuildCommand`/`DetectRunCommand` search all subdirectories (not just root) since agent-built repos often have project files under `src/`. Also added AI-driven detection via Copilot CLI (`claude-haiku-4.5`) with 60s timeout and static-pattern fallback.

**Lesson:** Preview Build operates on a DIFFERENT project than the VDT workspace. Config values that describe "how to build VDT" must never be applied to "how to build the target project." Auto-detection should search recursively and prefer `src/` directories over `tests/`.

---

## 154. GetPRCodeContextAsync Must Cap Total Output Size

**What happened:** `GetPRCodeContextAsync` serialized ALL changed files into the prompt with no total size limit (only per-file limit of 15K chars). For PRs touching 20+ files, the combined context reached 250K+ characters. Combined with PMSpec (30K), Architecture (20K), and PR metadata, the PM review prompt exceeded 250K tokens — crashing the Copilot CLI process.

**Fix:** Added `maxTotalChars` parameter (default 80K). When the total exceeds the limit, remaining files are listed by name only with instruction to use file browsing tools. Cached review context is also checked against the limit.

**Lesson:** Any function that serializes file content into a prompt must have a total size cap, not just a per-file cap. The sum of individually-small files can still exceed model context limits. Always log a warning when the cap triggers so operators know the review was truncated.

---

## 155. Visual Score Hydration Must Be Symmetric

**What happened:** Strategy candidates carried `VisualsScore` correctly during the live evaluation path, but one of the recovery/hydration paths dropped the field. After a refresh or restart, the dashboard could show a winner or ordering that no longer matched the score breakdown the operator saw before the restart.

**Fix:** Treat visual scoring as durable candidate state, not a live-only embellishment. Persist and hydrate it on every path that reconstructs candidate snapshots.

**Lesson:** If a value influences winner selection or operator trust, every live path and every recovery path must preserve it symmetrically.

---

## 156. Stale `status:blocked` Labels Silently Stall the Pipeline

**What happened:** Engineering tasks could keep a `status:blocked` label after the PR that originally blocked them had already merged or closed. Engineers correctly skipped those tasks, but the pipeline then looked "quiet" instead of obviously broken.

**Fix:** Add `PipelineStallDetector` to treat stale blocked labels as a first-class failure mode, and separately detect the "all engineers idle, no PRs open, claimable work still exists" stall condition.

**Lesson:** Multi-agent pipelines need stall detection at the workflow level, not just stuck-agent detection. A perfectly idle system can still be deadlocked.

---

## 157. Preview Placeholders Need Explicit Causes

**What happened:** Strategy preview tiles reused one generic "No preview" state for three very different situations: backend-only work with no visual output, missing Playwright/browser tooling, and apps that never started successfully. Operators had no way to tell expected emptiness from infrastructure failure.

**Fix:** Split preview states into `NoVisualContent`, `CaptureUnavailable`, and `CaptureFailed`, then render different copy in the dashboard.

**Lesson:** Placeholder states are operational signals. If one placeholder covers both expected and broken outcomes, the UI hides the very diagnosis operators need.

---

## 158. Operator Feedback Must Not Consume Rework Budgets

**What happened:** Human/operator change requests initially looked like normal reviewer churn. That meant a person asking for one more change could burn through `MaxReworkCycles` and accidentally trigger force-approval behavior intended only for AI review loops.

**Fix:** Treat operator change requests as governance, not reviewer disagreement. Preserve existing approvals, exempt those cycles from the normal rework budget, and post a dedicated `**[Operator-Addressed]**` completion comment when done.

**Lesson:** Human override channels should be tracked for auditability, but they must stay outside automated churn budgets and loop breakers.

---

## 159. Operator Intent Must Flow Into Self-Assessment Context

**What happened:** A fresh self-assessment context would sometimes "clean up" or question the exact change a human operator had explicitly requested, because that request only lived in a PR comment and not in the engineer's implementation context.

**Fix:** Copy operator requests into `_implementationNotes` and feed those notes into the later self-assessment/finalization steps.

**Lesson:** Any human directive that must survive multiple AI turns needs to be promoted into durable implementation context, not left as an external side comment.

---

## 160. Sanitize PR Bodies Before Parsing or Appending Metadata

**What happened:** PR bodies were used both as human-readable summaries and as a metadata carrier (`winner-strategy` markers, linked issue parsing, dashboard hints). Raw Copilot/strategy output could include HTML-comment-like text or other chatter that interfered with parsing or polluted appended metadata.

**Fix:** Route PR bodies through `PullRequestWorkflow.SanitizePrBody(...)` before parsing existing content or appending new markers.

**Lesson:** Once a text field becomes both prose and protocol, sanitize it before every parse-and-append step. Otherwise incidental text will eventually break your machine-readable conventions.

---

## 161. Playwright Driver Files Can Vanish From Runner Bin

**What happened:** The Runner's `.playwright/` directory in `bin/Debug/net8.0/` had folder structure intact but ALL 114 files were missing (0 bytes on disk). `Playwright.CreateAsync()` couldn't find the node.js driver — the smoke test failed even though `chrome.exe` existed in `ms-playwright`. The dashboard showed 🔴 Playwright not ready for the entire session.

**Fix:** Copied 114 files (~565 MB) from the NuGet cache (`~/.nuget/packages/microsoft.playwright/1.60.0/.playwright/`) back into the Runner's bin output. The 5-minute periodic health check then validated successfully.

**Lesson:** A `dotnet build` while the Runner is running can delete old driver files but fail to copy new ones due to file locks. The `.playwright` folder is especially vulnerable because it contains large binaries (node.exe ~87MB). After any build failure or partial build, verify the `.playwright` folder has actual files, not just empty directories.

---

## 162. AppLauncher Build Recovery Has Pipe Deadlock

**What happened:** `AppLauncher.LaunchVerifiedAsync` Step 6 (build+restart recovery) redirected both stdout and stderr from `dotnet build` but called `WaitForExitAsync` without reading either pipe. When a target project produced >4KB of build errors (e.g., many CS0246 errors from missing references), the child process blocked on pipe writes and `WaitForExitAsync` hung indefinitely. An agent stuck at "Mark ready for review" for 40+ minutes with no active processes.

**Fix:** Read stdout/stderr concurrently before awaiting exit (matching the safe pattern in `StartAppUnderTestAsync`). Added 3-minute timeout with kill and 5-second post-kill drain. Same fix pattern as Lesson #44.

**Lesson:** Every `Process.Start` with `RedirectStandardOutput/Error = true` MUST read the pipes concurrently with `WaitForExitAsync`. This is the third instance of this class of bug (TestRunner, BuildRunner, now AppLauncher). Any new code spawning child processes should use the concurrent-read pattern from `StartAppUnderTestAsync` as the template.

---

## 163. Duplicate Task Assignment After Restart When Different Agent Has PR

**What happened:** Task #16 was originally assigned to SE1 (issue title: "SoftwareEngineer 1: Agent Workflow..."). SE3 later took over and created PR #17 ("SoftwareEngineer 3: Agent Workflow..."). On restart, `RecoverOrphanedAssignmentsAsync` checked for open PRs matching the task's `AssignedTo` name ("SoftwareEngineer 1") in PR titles — but PR #17's title said "SoftwareEngineer 3". All three checks (body link, engineer name, canonical parser) missed the PR, so the task was reset to Pending and reassigned to SE1. Both SE1 and SE3 then worked the same task simultaneously.

**Fix:** Two-pronged: (1) Orphan detector now extracts the task title without agent prefix and matches against PR titles — catches reassigned tasks regardless of which engineer owns the PR. Also restores `_agentAssignments` tracking to the actual PR owner. (2) `AssignTasksToAvailableEngineersAsync` adds defense-in-depth: filters out "Pending" tasks that already have an open PR before assignment.

**Lesson:** Task-to-PR correlation cannot rely solely on the original assignee's name in PR titles. After task reassignment, the issue title and PR title diverge. Use task-name matching (not just engineer-name matching) and always verify no open PR exists before assigning a task.

---

## 164. AppStartup Detection Picks Test Project Over Actual App

**What happened:** `DetectAppStartCommandFallback` in `AppLauncher` iterated over all `.csproj` files recursively and returned the FIRST one matching `IsWebSdkProject` (uses `Microsoft.NET.Sdk.Web`). Test projects using `WebApplicationFactory` also use `Microsoft.NET.Sdk.Web` for integration tests. When the test project was enumerated before the API project, both strategy candidates tried to run the test project as a web server — which exits immediately with code 1. AppStartup step showed "launch failed" for both candidates.

**Fix:** CLI prompt now explicitly says "NEVER select a test project" and "PRIORITIZE the actual application project." Fallback heuristic collects all Web SDK candidates, ranks non-test first, and only falls back to test projects with a warning. `RankCsprojCandidates` adds -200 penalty for test paths/names.

**Lesson:** Test projects that use `Microsoft.NET.Sdk.Web` are indistinguishable from real web apps by SDK alone. Any auto-detection of runnable web projects must explicitly filter out test projects by path/name patterns (tests/, .Tests.csproj, etc.).

---

## 165. PM Review Must Be Purpose-Aware for T-FINAL Integration Reports

**What happened:** The PM reviewer requested CHANGES on a T-FINAL (Final Integration) PR because it honestly identified remaining gaps (UI pages not built, webhooks not wired). The PM was comparing the report against PMSpec acceptance criteria and treating unmet criteria as blockers — but the T-FINAL task's job is to REPORT gaps, not implement features. The Architect and TE correctly approved (doc-only PR, no architectural violations, no tests needed).

**Fix:** Added REVIEW PURPOSE section to PM, Architect, and SE review prompts. Reviewers now first determine the PR's purpose: feature PRs get standard acceptance criteria checks; T-FINAL/documentation PRs are assessed on report accuracy and completeness. Updated verdict language from "code fully meets all acceptance criteria" to "PR satisfies its declared purpose and deliverables." Updated retry prompt to preserve purpose-awareness.

**Lesson:** Review prompts hardcoded to "are acceptance criteria met?" will always reject honest gap-analysis reports. Review criteria must be dynamic — derived from the PR's purpose, not a universal feature-completion bar. The same principle applies to documentation PRs, architecture PRs, and test-only PRs.
