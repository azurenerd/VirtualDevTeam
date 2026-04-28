# Agent Behaviors

Detailed documentation of each agent's behavior, communication patterns, and artifacts.

## Table of Contents

- [Overview](#overview)
- [Naming Conventions](#naming-conventions)
- [Program Manager Agent](#program-manager-agent)
- [Researcher Agent](#researcher-agent)
- [Architect Agent](#architect-agent)
- [Software Engineer Agent](#software-engineer-agent)
- [Software Engineer Agent](#software-engineer-agent)
- [Software Engineer Agent](#software-engineer-agent)
- [Test Engineer Agent](#test-engineer-agent)
- [Agent Interaction Summary](#agent-interaction-summary)

---

## Overview

AgentSquad deploys 7 specialized agent roles, each implemented as a class extending `AgentBase`. Every agent runs an independent async loop (`RunAgentLoopAsync`) and communicates through the message bus and the dev platform (GitHub or Azure DevOps, via `IPullRequestService`, `IWorkItemService`, etc.).

| Role | Singleton? | Model Tier | Primary Output |
|------|-----------|------------|----------------|
| Program Manager | Yes | Premium | TeamMembers.md, issue triage, PR reviews |
| Researcher | Yes | Standard | Research.md |
| Architect | Yes | Premium | Architecture.md, PR architectural reviews |
| Software Engineer | Yes | Premium | EngineeringPlan.md, task PRs, code reviews |
| Software Engineer | No (template) | Standard | Implementation PRs |
| Software Engineer | No (template) | Budget | Implementation PRs (with self-validation) |
| Test Engineer | Yes | Standard | Test plan PRs |

## Naming Conventions

### PR Titles
```
[Agent Name]: Task Title
```
Example: `Software Engineer: Implement user authentication module`

### Issue Titles
```
[Agent Name]: Issue Title
Executive Request: Title
```
Examples:
- `Software Engineer: Task #42 exceeds Software Engineer capability`
- `Executive Request: Approve additional Software Engineer`

### Branch Names
```
agent/{agent-name-slug}/{task-slug}
```
Example: `agent/software-engineer-1/implement-user-auth`

### Labels

Labels are applied to PRs and work items on the target platform (GitHub Issues/PRs or Azure DevOps Work Items). The same label names are used regardless of platform — on ADO they are stored as work item tags.

| Label | Used On | Purpose |
|-------|---------|---------|
| `executive-request` | Issues | PM escalation requiring human decision |
| `resource-request` | Issues | Request for additional engineers |
| `blocker` | Issues | Agent is blocked and needs help |
| `agent-question` | Issues | Inter-agent question |
| `agent-stuck` | Issues | Agent has been stuck beyond timeout |
| `resolved` | Issues | Issue has been resolved |
| `in-progress` | PRs | Engineer is actively implementing |
| `ready-for-review` | PRs | Implementation complete, awaiting review |
| `approved` | PRs | PR has been approved |
| `tested` | PRs | Test Engineer has generated a test plan |
| `tests` | PRs | PR contains test artifacts |
| `complexity-high` | PRs | High-complexity task |
| `complexity-medium` | PRs | Medium-complexity task |
| `complexity-low` | PRs | Low-complexity task |

---

## Program Manager Agent

**Class:** `ProgramManagerAgent` · **File:** `AgentSquad.Agents/ProgramManagerAgent.cs`

### Role and Responsibilities

The PM is the leadership agent responsible for team orchestration, resource management, and project health. It does not write code but instead coordinates the entire team, triages problems, and ensures alignment with project goals.

### Model Tier: Premium

Requires the strongest reasoning model for nuanced judgment — evaluating PR alignment with requirements, triaging complex blockers, and making resource allocation decisions.

### Main Loop Behavior

Each cycle executes these steps in order:

1. **Check Executive Responses** — Polls GitHub issues labeled `executive-request` for human approvals/denials. Processes responses and takes appropriate action.

2. **Monitor Team Status** — Reads `TeamMembers.md` to verify each agent's reported status. Detects stale agents (status hasn't changed beyond expected intervals).

3. **Handle Resource Requests** — Processes issues labeled `resource-request`. Approves requests within configured limits or escalates to executive if limits would be exceeded.

4. **Handle Blockers** — Triages issues labeled `blocker` using AI analysis. Provides actionable guidance to the blocked agent. Escalates to executive if the blocker cannot be resolved.

5. **Review Pull Requests** — Evaluates open PRs for alignment with the engineering plan and project requirements. Posts review comments with AI-generated analysis.

6. **Update Project Tracking** — Updates `TeamMembers.md` with current agent statuses, assignments, and activity.

### Messages

| Direction | Message Type | Behavior |
|-----------|-------------|----------|
| **Receives** | `ResourceRequestMessage` | Evaluates and approves/denies; escalates to executive if over limits |
| **Receives** | `StatusUpdateMessage` | Updates internal `_trackedAgents` dictionary for monitoring |
| **Receives** | `HelpRequestMessage` | Logs help request; creates GitHub issue if blocker |
| **Sends** | `StatusUpdateMessage` | Broadcasts own status changes |

### GitHub Artifacts

- **Issues created:** Executive requests, blocker escalations, guidance comments
- **Issue comments:** Triage analysis, approval/denial responses
- **PR comments:** Requirements alignment reviews
- **Files updated:** `TeamMembers.md`

### Dependencies

- Depends on all other agents reporting status via the message bus
- Depends on `TeamMembers.md` being readable in the repository
- Requires human intervention for `executive-request` issues

### Error Handling

- Individual cycle steps are wrapped in try-catch; failures in one step don't block others
- Stale agent detection triggers status warnings in logs
- Failed AI calls (blocker triage, PR review) are logged and skipped

---

## Researcher Agent

**Class:** `ResearcherAgent` · **File:** `AgentSquad.Agents/ResearcherAgent.cs`

### Role and Responsibilities

Conducts deep technical research on assigned topics using multi-turn AI conversations. Produces structured research findings that feed into the Architect's design process.

### Model Tier: Standard

Research requires solid analytical capability but doesn't need the premium tier's creative reasoning. Standard models provide good quality at reasonable cost.

### Main Loop Behavior

The Researcher operates on a queue-based model:

1. **Wait for directives** — Blocks on `_researchQueue` until a `TaskAssignmentMessage` arrives
2. **Conduct research** — Executes a 3-turn AI conversation per topic:
   - **Turn 1:** Break topic into 3–5 key sub-questions
   - **Turn 2:** Provide detailed analysis per sub-question (findings, tools, trade-offs, recommendations)
   - **Turn 3:** Synthesize into structured output (Summary, Key Findings, Recommended Tools, Considerations)
3. **Publish findings** — Appends formatted results to `Research.md`
4. **Notify completion** — Broadcasts `StatusUpdateMessage` with `"ResearchComplete"`

### Messages

| Direction | Message Type | Behavior |
|-----------|-------------|----------|
| **Receives** | `TaskAssignmentMessage` | Enqueues as `ResearchDirective` |
| **Sends** | `StatusUpdateMessage` | Broadcasts `"ResearchComplete"` to all agents |

### GitHub Artifacts

- **Files created/updated:** `Research.md` — Appended with sections per research topic

### Dependencies

- Triggered by `TaskAssignmentMessage` (typically from the workflow bootstrap)
- The Architect waits for research completion before beginning design

### Error Handling

- Failed AI conversations are logged; the research directive is skipped
- Research.md is appended atomically; partial results don't corrupt the document

---

## Architect Agent

**Class:** `ArchitectAgent` · **File:** `AgentSquad.Agents/ArchitectAgent.cs`

### Role and Responsibilities

Designs the system architecture based on research findings. Produces the `Architecture.md` document and continuously reviews PRs for architectural alignment after the initial design phase.

### Model Tier: Premium

Architecture design requires the highest reasoning capability — making trade-offs between competing approaches, considering security/scalability, and producing a coherent system design.

### Main Loop Behavior

**Phase 1 — Architecture Design (one-time):**

1. Waits for `TaskAssignmentMessage` containing architecture directive
2. Executes a 5-turn AI conversation:
   - **Turn 1:** Identify key architectural decisions with options and trade-offs
   - **Turn 2:** Design system components (name, responsibility, interfaces, dependencies)
   - **Turn 3:** Define data model, API contracts, infrastructure requirements
   - **Turn 4:** Address security, scaling, and risk mitigation
   - **Turn 5:** Compile everything into structured `Architecture.md`
3. Saves `Architecture.md` to the repository
4. Creates a GitHub issue asking the Software Engineer to review the architecture
5. Broadcasts `StatusUpdateMessage` with `"ArchitectureComplete"`

**Phase 2 — Continuous PR Review:**

After architecture is complete, the Architect enters a polling loop:

1. Gets all open PRs with `ready-for-review` label
2. Skips PRs already reviewed
3. For each unreviewed PR, evaluates architectural alignment via AI
4. Posts review comments on the PR

### Messages

| Direction | Message Type | Behavior |
|-----------|-------------|----------|
| **Receives** | `TaskAssignmentMessage` | Triggers architecture design process |
| **Sends** | `StatusUpdateMessage` | Broadcasts `"ArchitectureComplete"` |

### GitHub Artifacts

- **Files created:** `Architecture.md` — Complete system design document
- **Issues created:** Architecture review request for Software Engineer
- **PR comments:** Architectural alignment reviews

### Dependencies

- Waits for research to complete (Research.md available)
- Software Engineer depends on `Architecture.md` for engineering planning
- All engineer agents reference `Architecture.md` for implementation guidance

### Error Handling

- Architecture design failure is a critical error (logged and retried)
- PR review failures are logged and skipped; the PR is retried next cycle

---

## Software Engineer Agent

**Class:** `SoftwareEngineerAgent` · **File:** `AgentSquad.Agents/SoftwareEngineerAgent.cs`

### Role and Responsibilities

The technical lead responsible for breaking architecture into tasks, assigning work to the engineering team, implementing high-complexity tasks, and reviewing all engineer PRs.

### Model Tier: Premium

Requires premium reasoning for engineering planning (task decomposition, dependency analysis), code review judgment, and high-complexity implementation.

### Main Loop Behavior

**Phase 1 — Engineering Planning:**

1. Polls GitHub issues for the "Architecture document is ready" signal (or reads Architecture.md directly)
2. Reads `Architecture.md` and `Research.md`
3. Executes a multi-turn AI conversation to decompose architecture into tasks:
   - Output format: `TASK|ID|Name|Description|Complexity|Dependencies|Effort`
   - Builds a task backlog with dependency graph
4. Saves `EngineeringPlan.md`
5. Broadcasts `StatusUpdateMessage` with `"EngineeringPlanReady"`

**Phase 2 — Development Management (continuous loop):**

1. **Assign tasks** — Reads `TeamMembers.md` for available engineers; matches task complexity to role (SE → assigned by priority); creates branches and PRs; sends `TaskAssignmentMessage`

2. **Work on own tasks** — Claims High-complexity tasks with satisfied dependencies; generates implementation via AI; creates PR; marks ready for review

3. **Review engineer PRs** — Gets PRs with `ready-for-review` label (excluding own); evaluates quality via AI; submits approval or requests changes; marks tasks as "Complete" if approved

4. **Evaluate resource needs** — Counts unassigned parallelizable tasks vs. available engineers; sends `ResourceRequestMessage` to PM if more are needed

5. **Update engineering plan** — Rebuilds `EngineeringPlan.md` with current task statuses

### Messages

| Direction | Message Type | Behavior |
|-----------|-------------|----------|
| **Receives** | `StatusUpdateMessage` | Clears engineer's assignment when task complete |
| **Receives** | `TaskAssignmentMessage` | Adds externally-assigned tasks to backlog |
| **Sends** | `TaskAssignmentMessage` | Assigns tasks to engineers (with PR URL) |
| **Sends** | `ResourceRequestMessage` | Requests additional engineers from PM |
| **Sends** | `StatusUpdateMessage` | Broadcasts plan readiness and own task completions |

### GitHub Artifacts

- **Files created/updated:** `EngineeringPlan.md` — Task breakdown with status tracking
- **PRs created:** Task PRs for each engineer assignment; own PRs for high-complexity work
- **PR reviews:** Approval or change-request reviews on engineer PRs
- **Branches:** `agent/{agent-slug}/{task-slug}` for each task

### Dependencies

- Depends on `Architecture.md` being complete (waits for Architect signal)
- Depends on `TeamMembers.md` for available engineer discovery
- Engineers depend on task assignments from the Software Engineer

### Error Handling

- Task assignment failures are logged; the task remains in backlog for retry
- PR creation failures are logged; the task stays unassigned
- Review failures are logged; the PR is retried next cycle
- Dependencies that cannot be met are flagged in the engineering plan

### Task Dependencies

The Software Engineer maintains a task backlog with explicit dependencies:

```
Task A (no deps) → Task B (depends on A) → Task C (depends on A, B)
```

`AreDependenciesMet(task)` checks that all dependency tasks have `"Complete"` status before allowing assignment. Tasks are processed in dependency order.

---

## Software Engineer Agent

**Class:** `SoftwareEngineerAgent` · **File:** `AgentSquad.Agents/SoftwareEngineerAgent.cs`

### Role and Responsibilities

Executes medium-complexity engineering tasks. Produces implementation PRs with a 3-turn AI loop (plan → implement → self-review). Receives guidance from the Software Engineer via issue comments.

### Model Tier: Standard

Medium-complexity tasks benefit from a capable but cost-efficient model. The 3-turn self-review approach compensates for any quality gap vs. premium.

### Main Loop Behavior

1. **Poll for assigned PRs** — Gets PRs assigned to this agent via `PullRequestWorkflow.GetAgentTasksAsync()`
2. **For each open PR not yet worked on:**
   - Execute `WorkOnTaskAsync(pr)`:
     - **Turn 1:** Analyze requirements; outline implementation plan (components, interfaces, dependencies)
     - **Turn 2:** Produce complete implementation (all code files, class structures, tests)
     - **Turn 3:** Self-review (check for missing error handling, architecture alignment, edge cases, bugs); produce corrected implementation if needed
   - Post implementation summary as PR comment
   - Mark PR `ready-for-review`
   - Send `StatusUpdateMessage` with `"TaskComplete"`
3. **Check for issues** — Poll for guidance or feedback issues (e.g., `REQUEST_CHANGES` from Software Engineer); acknowledge and resolve

### Messages

| Direction | Message Type | Behavior |
|-----------|-------------|----------|
| **Receives** | `TaskAssignmentMessage` | Logs task assignment (PR polling does the actual pickup) |
| **Sends** | `StatusUpdateMessage` | Broadcasts `"TaskComplete"` when PR is ready for review |

### GitHub Artifacts

- **PR comments:** Implementation summary with approach description
- **PRs updated:** Marks `ready-for-review`, removes `in-progress`
- **Issues resolved:** Acknowledges and closes feedback issues

### Dependencies

- Depends on Software Engineer for task assignments (PRs)
- References `Architecture.md` and `EngineeringPlan.md` for context
- Software Engineer reviews the implementation

### Error Handling

- Implementation failure: creates a `blocker` issue via `IssueWorkflow.ReportBlockerAsync()`
- Status transitions to `Blocked` on failure
- Feedback from Software Engineer (REQUEST_CHANGES) triggers re-implementation

---

## Software Engineer Agent

**Class:** `SoftwareEngineerAgent` · **File:** `AgentSquad.Agents/SoftwareEngineerAgent.cs`

### Role and Responsibilities

Executes low-complexity engineering tasks with additional guardrails: complexity detection, self-validation with retries, and escalation when a task exceeds capability. Uses a smaller context window and simpler prompts than the Software Engineer.

### Model Tier: Budget

Low-complexity tasks are well-suited to budget models. The self-validation loop and escalation mechanism compensate for lower model capability.

### Main Loop Behavior

1. **Poll for assigned PRs** — Same as Software Engineer
2. **For each open PR not yet worked on:**
   - Execute `WorkOnTaskAsync(pr)`:
     - **Step 1:** Break task into small implementation steps; check for `COMPLEXITY_WARNING` keyword
       - If detected → escalate via `EscalateComplexityAsync()` (create issue, send `HelpRequestMessage`, go Blocked)
     - **Step 2:** Implement step-by-step (simpler approach than Software Engineer)
     - **Step 3:** Self-validate with retry loop (up to 2 retries):
       - Separate validation context with validation-specific prompt
       - Model responds with `VALIDATION: PASS` or `VALIDATION: FAIL`
       - If FAIL → ask model to fix issues, re-validate
       - If still failing after retries → proceed with "best effort" warning
     - **Step 4:** Post implementation as PR comment with validation status (✅ or ⚠️)
     - **Step 5:** Mark PR `ready-for-review`
   - Send `StatusUpdateMessage` with self-validation pass/fail indicator
3. **Check for issues** — Same as Software Engineer

### Messages

| Direction | Message Type | Behavior |
|-----------|-------------|----------|
| **Receives** | `TaskAssignmentMessage` | Logs task assignment |
| **Sends** | `StatusUpdateMessage` | Broadcasts `"TaskComplete"` with validation status |
| **Sends** | `HelpRequestMessage` | Complexity escalation (`IsBlocker = true`) to Software Engineer |

### GitHub Artifacts

- **PR comments:** Implementation summary with ✅ (validation passed) or ⚠️ (best effort warning)
- **PRs updated:** Marks `ready-for-review`
- **Issues created:** Complexity escalation: `"Task #{PR} exceeds Software Engineer capability"`
- **Issues resolved:** Acknowledges feedback issues

### Key Differences from Software Engineer

| Aspect | Software Engineer | Software Engineer |
|--------|----------------|-----------------|
| Context window | Full architecture doc | Truncated to 4,000 chars |
| Complexity handling | Implements directly | Detects and escalates |
| Self-review | Single pass | Validation with up to 2 retries |
| Failure mode | Reports blocker | Escalates to Software Engineer |
| Implementation style | 3-turn conversation | Step-by-step decomposition |
| Max retries | N/A | `MaxSelfReviewRetries = 2` |

### Dependencies

- Depends on Software Engineer for task assignments
- Escalates to Software Engineer when complexity exceeds capability
- Software Engineer reviews implementation

### Error Handling

- Complexity escalation creates an issue and sends `HelpRequestMessage`
- Self-validation failures after max retries proceed with "best effort" warning
- Implementation failures report blockers same as Software Engineer

---

## Test Engineer Agent

**Class:** `TestEngineerAgent` · **File:** `AgentSquad.Agents/TestEngineerAgent.cs`

### Role and Responsibilities

Monitors completed PRs across the team, generates comprehensive test plans, and creates test PRs with coverage documentation. Operates independently — no task assignment needed.

### Model Tier: Standard

Test plan generation requires understanding code structure and identifying edge cases, which benefits from a capable model. Standard tier provides the right balance.

### Main Loop Behavior

Runs at 2× the standard polling interval (slower scan rate):

1. **Scan for untested PRs** — Gets all open PRs; filters to those that:
   - Don't have the `tested` label
   - Have the `ready-for-review` label
   - Were not created by the Test Engineer (avoids circular testing)
   - Haven't been previously processed
2. **For each untested PR:**
   - Generate test plan via AI (single prompt with structured template):
     - **Test Strategy** — Overall testing approach
     - **Test Cases** — Numbered list with description, assertions, and edge cases
     - **File Locations** — Where test files should be created
     - **Dependencies/Mocks** — Required test infrastructure
   - Create a test branch: `agent/test-engineer/{task-slug}`
   - Create test plan file: `docs/test-plans/pr-{number}-test-plan.md`
   - Create test PR with labels `["tests", "in-progress"]`
   - Add comment to source PR with test plan summary
   - Add `tested` label to source PR
   - Broadcast `StatusUpdateMessage`

### Messages

| Direction | Message Type | Behavior |
|-----------|-------------|----------|
| **Sends** | `StatusUpdateMessage` | Broadcasts after test PR creation |

The Test Engineer does not subscribe to any message types — it operates entirely through GitHub PR polling.

### GitHub Artifacts

- **PRs created:** Test PRs titled `"{TestEngineer}: Tests for PR #{number} - {title}"`
- **Files created:** `docs/test-plans/pr-{number}-test-plan.md`
- **PR comments:** Test plan summary on source PR
- **Labels added:** `tested` on source PR; `tests` + `in-progress` on test PR
- **Branches created:** `agent/test-engineer/{task-slug}`

### Dependencies

- Depends on other agents producing PRs with `ready-for-review` label
- Independent of the message bus for triggering (poll-based)

### Error Handling

- Failed test plan generation is logged; the PR is retried next cycle
- Tracks previously processed PRs to avoid duplicate test plans
- Skips own PRs to prevent infinite test-plan generation loops

---

## Agent Interaction Summary

```
                          ┌──────────────────┐
                          │  Program Manager │
                          │  (Orchestrator)  │
                          └────────┬─────────┘
                    ┌──────────────┼──────────────┐
                    │              │              │
              ResourceReq    StatusUpdate    BlockerTriage
                    │              │              │
         ┌──────────┴──┐   ┌──────┴─────┐  ┌────┴────────┐
         │  Principal   │   │ Researcher │  │  Architect  │
         │  Engineer    │   │            │  │             │
         └──────┬───────┘   └────────────┘  └─────────────┘
                │
        TaskAssignment
         ┌──────┼──────┐
         │      │      │
    ┌────┴──┐ ┌─┴───┐ ┌┴──────┐
    │Software Engineer │ │Software Engineer│ │ Test  │
    │Eng.(n)│ │Eng(n)│ │ Eng.  │
    └───────┘ └──────┘ └───────┘

Legend:
  ──── Message bus communication
  ResourceReq = ResourceRequestMessage
  TaskAssignment = TaskAssignmentMessage
  StatusUpdate = StatusUpdateMessage (broadcast to all)
  BlockerTriage = HelpRequestMessage → PM creates issue
```

### Communication Flow by Project Phase

| Phase | Key Interactions |
|-------|-----------------|
| **Research** | PM assigns topic → Researcher produces Research.md → broadcasts ResearchComplete |
| **Architecture** | Architect reads Research.md → produces Architecture.md → creates review issue → broadcasts ArchitectureComplete |
| **Planning** | Principal reads Architecture.md + Research.md → creates EngineeringPlan.md → assigns tasks via PRs + TaskAssignment messages |
| **Development** | Engineers implement tasks → mark PRs ready-for-review → Principal reviews → Architect reviews architecture alignment |
| **Testing** | Test Engineer scans ready-for-review PRs → generates test plans → creates test PRs → labels source PRs as tested |
| **Review** | PM reviews PR alignment with requirements → Principal approves/requests changes → PRs merged |

### Shared State Documents

| Document | Writer(s) | Reader(s) |
|----------|-----------|-----------|
| `TeamMembers.md` | PM | PM, Software Engineer (for engineer discovery) |
| `Research.md` | Researcher | Architect, Software Engineer |
| `Architecture.md` | Architect | Software Engineer, Software Engineer, Software Engineer, Test Engineer |
| `EngineeringPlan.md` | Software Engineer | PM (for progress tracking), Engineers (for context) |
