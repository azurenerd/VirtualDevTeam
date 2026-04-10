# Lessons Learned: Building a Multi-Agent AI Development System

> **Author:** Ben Humphrey (azurenerd) with Copilot CLI  
> **Project:** AgentSquad — a .NET 8 multi-agent system where 7 AI agents (PM, Researcher, Architect, Principal Engineer, Senior/Junior Engineers, Test Engineer) collaborate through GitHub PRs/Issues to build software.  
> **Purpose:** This document captures hard-won lessons from ~50+ iterative build-run-fix cycles over multiple sessions. It's intended for engineering teams considering AI agent-based development pipelines, to help them avoid the same pitfalls and build better agent orchestration from day one.

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

---

## 1. The Plan Is Never Enough

**Lesson:** Even with a comprehensive architecture document, detailed PM specification, and engineering plan, the agent system required constant human guidance to course-correct behaviors that were never anticipated in the original design.

### What happened:
- The initial plan covered agent roles, message bus communication, GitHub integration, and a phase-gated workflow. It seemed comprehensive.
- In practice, dozens of emergent behaviors surfaced only during live execution: agents acting out of order, duplicate work on restart, review loops that never terminated, agents posting meta-commentary instead of doing work.
- Each fix revealed 2-3 more issues that couldn't have been predicted from the plan alone.

### Examples of guidance that was needed but not in the original plan:
- "The PM agent doesn't create a PM Spec document" — the original plan had agents but didn't specify the document pipeline (Research.md → PMSpec.md → Architecture.md → Engineering tasks)
- "The PE agent created the plan but hasn't asked for any new developers and no new PRs have been created" — the spawning workflow for engineer agents wasn't detailed
- "Make sure the agents don't review the code until the engineering agents are ready" — review timing relative to PR readiness wasn't specified
- "After a review we need to add a message to send back to the author when there is feedback" — the rework loop wasn't in the original design

### Takeaway:
**Plan for the plan to be incomplete.** Budget significant time for iterative observation and correction. The first 5-10 end-to-end runs will primarily surface gaps in the workflow design, not validate it.

---

## 2. Agent Context Is Everything

**Lesson:** AI agents lose all context between invocations. Every piece of information they need must be explicitly provided in their prompt, or they will produce generic, misaligned output.

### What happened:
- Reviewers (PM, Architect, PE) were approving or rejecting PRs without reading the actual code files, the linked issue, the PMSpec, or the Architecture document. They were reviewing based solely on the PR title and description.
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
| PE | Create Tasks | PMSpec.md, Architecture.md, Design Files, Repo Structure |
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
- **Approval deadlock:** The PE couldn't approve its own PR, but was listed as a required reviewer.
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
- Each reviewer has a defined scope (PM = business alignment, Architect = architecture compliance, PE = code quality)
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
- Simple tasks (Junior Engineer assignments) work fine with budget models

---

## 10. Design and UI Quality

**Lesson:** AI agents will completely ignore visual design references unless every agent in the pipeline is explicitly instructed to read, analyze, and propagate design specifications.

### What happened:
- A professional HTML design reference (`OriginalDesignConcept.html`) with SVG Gantt timelines, monthly heatmap grids, and precise color schemes was sitting in the repository root.
- **Not a single agent** — Researcher, PM, Architect, PE, or Engineers — ever read it.
- The built UI was bare, unstyled HTML that looked "like something a free local model would have created in 4 minutes."
- The design file had specific CSS grid patterns, hex color codes, typography specifications, and component layouts — all ignored.

### Guidance that was needed:
- "Look at how ugly this UI looks... this looks NOTHING like the original HTML design and picture I gave as a reference"
- "Please figure out why the example design was completely ignored"
- "Ensure the PM reads the design files, puts together a terrific and detailed description, and incorporates that into the PM Spec document with images, screenshots, or whatever design files were created"
- "Make sure the Researcher agent is looking at those design ideas as well"
- "And the architect of course too"
- "Make sure the PE agent puts in the design details in Issues and PRs where able to give perfect design guidance"
- "Ensure the testing engineer knows the designs well to know how to best test them and do the UI tests too"

### Takeaway:
**Design context must flow through EVERY layer of the pipeline:**
```
Design Files in Repo
  → Researcher: Analyzes design, recommends technologies for the specific design
  → PM: Creates "Visual Design Specification" section in PMSpec with layout, colors, interaction scenarios
  → Architect: Creates "UI Component Architecture" mapping visual sections to code components
  → PE: Includes design details in every UI-related engineering task issue
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

*This document was compiled from 50+ checkpoints, 220+ conversation turns, and 30+ end-to-end test runs across two Copilot CLI sessions building the AgentSquad system.*
