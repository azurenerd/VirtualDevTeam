# AgentSquad Requirements & Workflow Scenarios

> **Purpose:** This document captures all requirements for the AgentSquad multi-agent AI developer team system. Each requirement includes expected workflow scenarios that can be used by testing agents, developers, or new Copilot sessions to validate the system operates correctly and to create/fix bugs when it doesn't.
>
> **Maintainer:** Generated from session history and checkpoint analysis. Update when new requirements are added.
>
> **Owner:** Ben Humphrey (@azurenerd)

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Agent Roles & Responsibilities](#2-agent-roles--responsibilities)
3. [Workflow & Phase Requirements](#3-workflow--phase-requirements)
4. [PM Agent Requirements](#4-pm-agent-requirements)
5. [Researcher Agent Requirements](#5-researcher-agent-requirements)
6. [Architect Agent Requirements](#6-architect-agent-requirements)
7. [Principal Engineer Agent Requirements](#7-principal-engineer-agent-requirements)
8. [Senior & Junior Engineer Requirements](#8-senior--junior-engineer-requirements)
9. [Test Engineer Requirements](#9-test-engineer-requirements)
10. [Communication & Message Bus Requirements](#10-communication--message-bus-requirements)
11. [GitHub Integration Requirements](#11-github-integration-requirements)
12. [PR Review & Merge Requirements](#12-pr-review--merge-requirements)
13. [Rework & Feedback Loop Requirements](#13-rework--feedback-loop-requirements)
14. [Clarification & Escalation Requirements](#14-clarification--escalation-requirements)
15. [Configuration & Tech Stack Requirements](#15-configuration--tech-stack-requirements)
16. [Idempotency & Restart Recovery Requirements](#16-idempotency--restart-recovery-requirements)
17. [Dashboard & Monitoring Requirements](#17-dashboard--monitoring-requirements)
18. [AI Provider Requirements](#18-ai-provider-requirements)
19. [Code Quality & Architecture Requirements](#19-code-quality--architecture-requirements)
20. [End-to-End Workflow Scenarios](#20-end-to-end-workflow-scenarios)
21. [PE Integration & Branch Sync Requirements](#21-pe-integration--branch-sync-requirements)

---

## 1. System Overview

AgentSquad is a multi-agent AI system where 7 specialized agent roles collaborate through GitHub PRs/Issues and an in-process message bus to build software projects autonomously. A human Executive stakeholder (@azurenerd) provides high-level direction and resolves escalations.

### Core Principles

- **REQ-SYS-001**: Agents communicate through two complementary layers: an in-process message bus (real-time, <1ms, no durability) and GitHub API (durable artifacts, human oversight).
- **REQ-SYS-002**: The message bus uses `System.Threading.Channels` with bounded capacity (1000 messages). Messages route by `ToAgentId` — set to `"*"` for broadcast, or a specific agent `Identity.Id` for targeted delivery.
- **REQ-SYS-003**: All message bus subscriptions and routing MUST use agent `Identity.Id` (e.g., `seniorengineer-{guid}`), never `DisplayName` (e.g., `Senior Engineer 1`). DisplayName is only for GitHub PR/Issue titles.
- **REQ-SYS-004**: The system uses .NET 8, C# 12, with nullable reference types, file-scoped namespaces, and `record` types for messages/DTOs.
- **REQ-SYS-005**: Workflow phases progress linearly: Initialization → Research → Architecture → EngineeringPlanning → ParallelDevelopment → Testing → Review → Completion. No backward transitions.

**Scenario: System Startup**
1. Runner starts, registers DI services
2. `AgentSquadWorker` spawns core agents in phase order: PM → Researcher → Architect → PE
3. PM performs one-time kickoff: reads project description, seeds Researcher
4. PM restores previously-spawned engineers from TeamMembers.md
5. Each agent initializes, subscribes to relevant message types, enters its main loop

---

## 2. Agent Roles & Responsibilities

### REQ-ROLE-001: Seven Specialized Roles

| Role | Model Tier | Primary Responsibility |
|------|-----------|----------------------|
| Program Manager (PM) | premium | Project oversight, PMSpec creation, Issue creation, clarification handling, PR review |
| Researcher | standard | Research phase, produces Research.md |
| Architect | premium | Architecture design, produces Architecture.md |
| Principal Engineer (PE) | premium | Engineering plan, task assignment, high-complexity implementation, PR review |
| Senior Engineer | standard | Medium-complexity task implementation |
| Junior Engineer | budget/local | Low-complexity task implementation |
| Test Engineer | standard | Test planning and execution |

### REQ-ROLE-002: Engineer Hierarchy

- **REQ-ROLE-002a**: Senior and Junior engineers share identical workflow logic. They differ only in AI model tier, role display name, and minor behavioral overrides (Senior does self-review, Junior truncates context for budget models).
- **REQ-ROLE-002b**: All three engineer types (Senior, Junior, PE) extend `EngineerAgentBase` which contains shared logic for issue-driven work, rework handling, clarification loops, and message subscriptions.
- **REQ-ROLE-002c**: The PE has additional orchestration capabilities (planning, assignment, review, resource evaluation) on top of the base engineer functionality.

**Scenario: Engineer Creates PR from Issue**
1. PE assigns GitHub Issue #42 to "Senior Engineer 1" by updating issue title and sending `IssueAssignmentMessage`
2. Senior Engineer receives message → reads Issue #42 details from GitHub
3. AI analyzes Issue against PMSpec + Architecture → produces understanding, acceptance criteria, planned approach
4. If AI has questions → enters clarification loop (see §14)
5. Engineer creates branch `agent/senior-engineer-1/issue-42-implement-auth`
6. Engineer creates PR with title "Senior Engineer 1: Implement auth" and body containing "Closes #42" + understanding + criteria + plan
7. AI produces implementation using correct tech stack → code files parsed and committed
8. PR marked ready-for-review → `ReviewRequestMessage` broadcast

---

## 3. Workflow & Phase Requirements

### REQ-WF-001: Document Production Pipeline

Documents are produced in strict order, each building on the previous:

```
Project.Description → Research.md → PMSpec.md → Architecture.md → EngineeringPlan.md → Code PRs
```

- **REQ-WF-001a**: Research.md is produced by the Researcher from the project description and configurable research prompt.
- **REQ-WF-001b**: PMSpec.md is produced by the PM from Research.md + project description. Contains: Executive Summary, Business Goals, User Stories & Acceptance Criteria, Scope, Non-Functional Requirements, Success Metrics, Constraints.
- **REQ-WF-001c**: Architecture.md is produced by the Architect from PMSpec.md + Research.md.
- **REQ-WF-001d**: EngineeringPlan.md is produced by the PE from Architecture.md + PMSpec.md + Enhancement Issues.

### REQ-WF-002: Phase Gating

Each phase has gate conditions that must be met before advancing:

- Research → Architecture: `research.doc.ready` + `research.complete` signals
- Architecture → EngineeringPlanning: `architecture.doc.ready` + `architecture.complete` signals
- EngineeringPlanning → ParallelDevelopment: `engineering.plan.ready` + `principal.ready` signals

### REQ-WF-003: Agent Ordering

- **REQ-WF-003a**: Researcher starts work immediately after PM's kickoff TaskAssignment.
- **REQ-WF-003b**: PM creates PMSpec.md after receiving `ResearchComplete` status update (not before).
- **REQ-WF-003c**: Architect starts after receiving `PMSpecReady` status update from PM. Architect does NOT listen to TaskAssignment broadcasts.
- **REQ-WF-003d**: PE starts after receiving `PlanningCompleteMessage` from PM AND Architecture.md is ready.
- **REQ-WF-003e**: Engineers start only after PE assigns them Issues via `IssueAssignmentMessage`.

**Scenario: Full Document Pipeline**
1. PM sends TaskAssignment to Researcher with project description
2. Researcher produces Research.md (3 AI turns) → commits via document PR → auto-merges → sends `ResearchComplete`
3. PM receives ResearchComplete → creates PMSpec.md (2 AI turns) → commits via document PR → auto-merges → sends `PMSpecReady`
4. PM extracts User Stories from PMSpec → creates Enhancement Issues in GitHub → sends `PlanningCompleteMessage` to PE
5. Architect receives PMSpecReady → reads PMSpec + Research → creates Architecture.md (5 AI turns) → commits → sends `ArchitectureComplete`
6. PE receives PlanningCompleteMessage + detects Architecture.md → reads Enhancement Issues → creates EngineeringPlan.md → enters development loop

---

## 4. PM Agent Requirements

### REQ-PM-001: Project Kickoff

- **REQ-PM-001a**: On startup, PM reads project description from config and creates a GitHub Issue for the Researcher.
- **REQ-PM-001b**: PM sends `TaskAssignmentMessage` to Researcher via message bus.
- **REQ-PM-001c**: If Research.md already has meaningful content, PM skips research kickoff entirely.
- **REQ-PM-001d**: PM restores previously-spawned engineers from TeamMembers.md on startup.

### REQ-PM-002: PMSpec Creation

- **REQ-PM-002a**: After `ResearchComplete`, PM creates PMSpec.md using multi-turn AI conversation (2 turns: analyze requirements → draft specification).
- **REQ-PM-002b**: PMSpec is committed via document PR (PR-first pattern: create PR → do AI work → commit → auto-merge).
- **REQ-PM-002c**: After PMSpec merge, PM broadcasts `PMSpecReady` status update.

### REQ-PM-003: User Story Issue Creation

- **REQ-PM-003a**: After PMSpec is finalized, PM uses AI to extract User Stories and creates a GitHub Issue for each one.
- **REQ-PM-003b**: Each Issue is labeled `enhancement` and contains: User Story description (As a [role], I want...), detailed description, Acceptance Criteria checklist.
- **REQ-PM-003c**: Each Issue must have enough detail for an engineer to implement without needing the full PMSpec.
- **REQ-PM-003d**: Only after ALL Issues are created, PM sends `PlanningCompleteMessage` to PE.
- **REQ-PM-003e**: Documentation-related Issues (Architecture.md, PMSpec.md, etc.) should be labeled `documentation`.
- **REQ-PM-003f**: Idempotent: if Enhancement Issues already exist, skip creation and re-send PlanningCompleteMessage.

**Scenario: PM Creates User Story Issues**
1. PMSpec.md is finalized and merged
2. PM sends AI prompt with full PMSpec content → AI extracts 8 User Stories
3. For each story: PM checks if Issue with similar title exists → if not, creates Issue with title, body (User Story + Acceptance Criteria), label "enhancement"
4. After all 8 Issues created, PM publishes `PlanningCompleteMessage { IssueCount = 8 }`
5. PM sets `_userStoryIssuesCreated = true` to prevent re-creation

### REQ-PM-004: Clarification Handling

- **REQ-PM-004a**: PM subscribes to `ClarificationRequestMessage` from engineers.
- **REQ-PM-004b**: PM reads the Issue, uses AI to formulate a response based on PMSpec context.
- **REQ-PM-004c**: PM posts the response as a comment on the GitHub Issue.
- **REQ-PM-004d**: PM sends `ClarificationResponseMessage` back to the requesting engineer (using `request.FromAgentId`).
- **REQ-PM-004e**: If PM's AI response indicates uncertainty (contains "UNSURE"), PM escalates to Executive stakeholder.

**Scenario: PM Answers Engineer Question**
1. Junior Engineer sends `ClarificationRequestMessage { IssueNumber=42, Question="Should auth support OAuth or just JWT?" }`
2. PM dequeues request → reads Issue #42 from GitHub → reads PMSpec
3. PM sends AI prompt with Issue + PMSpec + question → AI responds: "Based on the PMSpec, the system should support JWT only for the MVP phase"
4. PM posts comment on Issue #42: "**Program Manager** responding to clarification: Based on the PMSpec..."
5. PM sends `ClarificationResponseMessage { IssueNumber=42, Response="..." }` to the Junior Engineer's agent Id

### REQ-PM-005: PR Review (Business Alignment)

- **REQ-PM-005a**: PM subscribes to `ReviewRequestMessage` and queues PR numbers for review.
- **REQ-PM-005b**: PM reviews PRs against PMSpec business requirements — but ONLY against the PR's own scope (not the entire PMSpec).
- **REQ-PM-005c**: Review must evaluate: business goal alignment, user story coverage for THIS task, acceptance criteria fulfillment.
- **REQ-PM-005d**: PM posts approval or changes-requested comment on the PR.
- **REQ-PM-005e**: If changes requested, PM sends `ChangesRequestedMessage` (broadcast) so the author engineer can rework.

### REQ-PM-006: Resource Management

- **REQ-PM-006a**: PM handles `ResourceRequestMessage` from PE — spawns new engineers via `AgentSpawnManager`.
- **REQ-PM-006b**: New engineers are tracked in TeamMembers.md for persistence across restarts.
- **REQ-PM-006c**: If max additional engineers limit is reached, PM escalates to Executive.

### REQ-PM-007: Executive Escalation

- **REQ-PM-007a**: When PM cannot answer a clarification, it creates an `executive-request` Issue assigned to the Executive GitHub username (configurable, default "azurenerd").
- **REQ-PM-007b**: PM monitors executive-request Issues for responses and relays answers back to the original engineer's Issue.

---

## 5. Researcher Agent Requirements

### REQ-RES-001: Research Production

- **REQ-RES-001a**: Researcher receives TaskAssignment from PM with project description.
- **REQ-RES-001b**: Uses 3-turn AI conversation to produce comprehensive Research.md.
- **REQ-RES-001c**: Research prompt is configurable in appsettings.json (empty string uses rich 7-section default).
- **REQ-RES-001d**: Research.md committed via document PR (PR-first: create PR → AI work → commit → auto-merge).
- **REQ-RES-001e**: After completion, broadcasts `ResearchComplete` status update.
- **REQ-RES-001f**: Idempotent: if Research.md already has meaningful content, skip re-research.

**Scenario: Research Phase**
1. PM creates GitHub Issue "Research: [ProjectName]" → sends TaskAssignment to Researcher
2. Researcher opens document PR for Research.md
3. Turn 1: Analyze project → Turn 2: Research patterns/technologies → Turn 3: Compile structured document
4. Commits Research.md → auto-merges PR → broadcasts ResearchComplete
5. On restart: if Research.md has content, Researcher skips work entirely

---

## 6. Architect Agent Requirements

### REQ-ARCH-001: Architecture Design

- **REQ-ARCH-001a**: Architect starts work ONLY after receiving `PMSpecReady` status update (not TaskAssignment broadcast).
- **REQ-ARCH-001b**: Reads PMSpec.md and Research.md for full context.
- **REQ-ARCH-001c**: Uses 5-turn AI conversation to produce Architecture.md.
- **REQ-ARCH-001d**: Architecture.md committed via document PR → auto-merge.
- **REQ-ARCH-001e**: After completion, broadcasts `ArchitectureComplete` status update.
- **REQ-ARCH-001f**: Idempotent: if Architecture.md already has meaningful content, skip.
- **REQ-ARCH-001g**: Incorporates the configured TechStack into architecture decisions.

### REQ-ARCH-002: PR Review (Architecture Alignment)

- **REQ-ARCH-002a**: Architect subscribes to `ReviewRequestMessage`.
- **REQ-ARCH-002b**: Reviews code PRs for architecture pattern compliance — scoped to the PR's own task, not the entire architecture.

**Scenario: Architecture Phase**
1. Architect receives PMSpecReady → reads PMSpec.md + Research.md
2. Opens document PR → 5-turn AI conversation → produces Architecture.md
3. Commits → auto-merges → broadcasts ArchitectureComplete
4. Later: receives ReviewRequest for PR #34 → reviews code against architecture patterns → posts comment

---

## 7. Principal Engineer Agent Requirements

### REQ-PE-001: Two-Phase Loop

- **REQ-PE-001a**: Phase 1: Wait for Architecture.md + PlanningCompleteMessage + Enhancement Issues → create EngineeringPlan.md.
- **REQ-PE-001b**: Phase 2: Continuous development loop with priorities: rework → assignment → own tasks → review → resource evaluation → plan update.
- **REQ-PE-001c**: On restart, if EngineeringPlan.md has real content, restore task backlog and skip to Phase 2.

### REQ-PE-002: Engineering Plan Creation

- **REQ-PE-002a**: PE reads PMSpec.md, Architecture.md, and ALL Enhancement-labeled GitHub Issues.
- **REQ-PE-002b**: AI maps each Issue to engineering tasks with: ID, Issue number, name, description, complexity (High/Medium/Low), dependencies.
- **REQ-PE-002c**: EngineeringPlan.md includes a task table with columns: ID, Task, Complexity, Assigned To, Issue #, PR #, Status, Dependencies.
- **REQ-PE-002d**: Task complexity mapping: High → PE, Medium → Senior Engineers, Low → Junior Engineers.

**Scenario: PE Creates Engineering Plan**
1. PE receives PlanningCompleteMessage + Architecture.md is ready
2. PE fetches all Enhancement Issues (e.g., 8 Issues)
3. AI analyzes Issues + PMSpec + Architecture → produces TASK|ID|IssueNum|Name|Desc|Complexity|Deps lines
4. PE parses into task backlog → builds EngineeringPlan.md markdown table → commits
5. Enters Phase 2 development loop

### REQ-PE-003: Task Assignment

- **REQ-PE-003a**: PE checks registered engineers via `AgentRegistry` (not TeamMembers.md).
- **REQ-PE-003b**: For each free engineer: find next unassigned task matching their complexity tier.
- **REQ-PE-003c**: Assignment process: update GitHub Issue title to `{EngineerName}: {TaskName}` → send `IssueAssignmentMessage` to engineer's `Identity.Id`.
- **REQ-PE-003d**: Track assignments in `_agentAssignments` dictionary keyed by agent `Identity.Id` (not DisplayName).
- **REQ-PE-003e**: When an assignment's Issue is closed (completed), clear the assignment so the engineer can receive new work.

**Scenario: PE Assigns Task to Engineer**
1. PE loops through registered engineers → finds Senior Engineer 1 has no active assignment
2. PE finds next Medium-complexity Pending task with met dependencies: T2 "Implement user auth" (Issue #43)
3. PE updates Issue #43 title to "Senior Engineer 1: Implement user auth"
4. PE sends `IssueAssignmentMessage { ToAgentId=seniorengineer-abc123, IssueNumber=43, Complexity="Medium" }`
5. PE records `_agentAssignments["seniorengineer-abc123"] = 43` and updates task status to "Assigned"

### REQ-PE-004: Own Task Implementation

- **REQ-PE-004a**: PE works on High-complexity tasks itself.
- **REQ-PE-004b**: PE assigns the Issue to itself (updates title with own DisplayName).
- **REQ-PE-004c**: PE creates PR with detailed AI-generated description including: summary, acceptance criteria, implementation notes, testing approach.
- **REQ-PE-004d**: PR body includes `Closes #{IssueNumber}` to auto-close the Issue on merge.
- **REQ-PE-004e**: PE commits implementation and marks PR ready-for-review.

### REQ-PE-005: PR Review (Technical Quality)

- **REQ-PE-005a**: PE subscribes to `ReviewRequestMessage` and queues PRs for review.
- **REQ-PE-005b**: PE skips reviewing its own PRs.
- **REQ-PE-005c**: PE reviews code PRs against architecture and engineering plan — scoped to the PR's task, NOT the full project.
- **REQ-PE-005d**: Review evaluates: architecture patterns, implementation completeness for THIS task, code quality, error handling, test coverage.
- **REQ-PE-005e**: If changes requested, PE sends `ChangesRequestedMessage` with feedback details.

### REQ-PE-006: Resource Evaluation

- **REQ-PE-006a**: PE evaluates if more engineers are needed based on parallelizable pending tasks vs. available engineers.
- **REQ-PE-006b**: PE sends `ResourceRequestMessage` when parallelizable tasks significantly exceed available engineers.
- **REQ-PE-006c**: Requested role matches task complexity: Low tasks → Junior, Medium tasks → Senior.

---

## 8. Senior & Junior Engineer Requirements

### REQ-ENG-001: Issue-Driven Work

- **REQ-ENG-001a**: Engineers receive work ONLY via `IssueAssignmentMessage` (not TaskAssignment).
- **REQ-ENG-001b**: On receiving assignment: read the full GitHub Issue, read PMSpec and Architecture for context.
- **REQ-ENG-001c**: AI analyzes the Issue and produces: understanding summary, acceptance criteria, high-level task plan, and any questions.
- **REQ-ENG-001d**: If AI has questions (no "NO_QUESTIONS" in output), enter clarification loop (§14).
- **REQ-ENG-001e**: Engineer creates its OWN PR (PE no longer creates PRs for other engineers).

### REQ-ENG-002: PR Creation by Engineers

- **REQ-ENG-002a**: PR branch: `agent/{engineer-name}/{issue-N-slug}`.
- **REQ-ENG-002b**: PR title: `{EngineerDisplayName}: {IssueTitle}`.
- **REQ-ENG-002c**: PR body must include: `Closes #{IssueNumber}`, Understanding section, Acceptance Criteria section, Planned Approach section.
- **REQ-ENG-002d**: Engineer may need to break work into multiple PRs if there are dependencies.

### REQ-ENG-003: Implementation

- **REQ-ENG-003a**: AI produces implementation using the configured TechStack (default: C# .NET 8 with Blazor Server).
- **REQ-ENG-003b**: AI output must use `FILE: path/to/file.ext` format for each file.
- **REQ-ENG-003c**: Code files are parsed by `CodeFileParser` and committed individually to the PR branch.
- **REQ-ENG-003d**: If code file parsing fails, raw output is committed as a fallback (but this indicates a problem).
- **REQ-ENG-003e**: After commit, engineer marks PR ready-for-review and broadcasts `ReviewRequestMessage`.

### REQ-ENG-004: Senior-Specific

- **REQ-ENG-004a**: Senior Engineer does an extra self-review AI turn before committing (reviews own implementation for quality).

### REQ-ENG-005: Junior-Specific

- **REQ-ENG-005a**: Junior Engineer truncates PMSpec and Architecture context for budget models (4000 chars max).
- **REQ-ENG-005b**: Junior can escalate complexity to PE if a task seems too complex.

**Scenario: Senior Engineer Implements Feature**
1. Receives `IssueAssignmentMessage { IssueNumber=43, IssueTitle="Implement user auth" }`
2. Reads Issue #43 from GitHub → reads PMSpec (full) + Architecture (full)
3. AI analyzes → produces plan with NO_QUESTIONS → skips clarification
4. Creates branch `agent/senior-engineer-1/issue-43-implement-user-auth`
5. Creates PR "Senior Engineer 1: Implement user auth" with body: "Closes #43\n\n## Understanding\n...\n## Acceptance Criteria\n...\n## Planned Approach\n..."
6. AI produces implementation in C# .NET 8 → outputs 5 files using FILE: format
7. CodeFileParser extracts files → committed to PR branch
8. Senior does self-review AI turn → may produce improved version → committed
9. PR marked ready-for-review → `ReviewRequestMessage` broadcast
10. Waits for review (keeps CurrentPrNumber set so rework messages can reach it)

---

## 9. Test Engineer Requirements

### REQ-TEST-001: Test Generation from Merged PRs

- **REQ-TEST-001a**: Test Engineer monitors merged PRs (via `GetMergedPullRequestsAsync`) every 3× poll interval and generates real, runnable test code for any PR that contains testable code files.
- **REQ-TEST-001b**: Testable code files are identified by extension: `.cs`, `.ts`, `.tsx`, `.js`, `.jsx`, `.py`, `.java`, `.go`, `.rs`, `.razor`, `.blazor`, `.vue`, `.svelte`, `.rb`, `.php`, `.swift`, `.kt`. PRs with only non-code files (markdown, images, config) are skipped.
- **REQ-TEST-001c**: Test Engineer skips PRs it created (self-authored test PRs) to avoid circular testing.
- **REQ-TEST-001d**: Test Engineer uses standard model tier for all AI work.

### REQ-TEST-002: Business Context for Test Generation

- **REQ-TEST-002a**: Before generating tests, the Test Engineer MUST gather full business context: linked issue (user story + acceptance criteria from PR body "Closes #N"), PMSpec.md, and Architecture.md.
- **REQ-TEST-002b**: The AI prompt includes: (1) linked issue acceptance criteria, (2) PM Specification, (3) Architecture document patterns, (4) actual source code files from the merged PR, and (5) PR title and description.
- **REQ-TEST-002c**: Tests MUST validate both acceptance criteria (business behavior) and technical implementation (code correctness). Prioritize business behavior tests over structural code coverage.
- **REQ-TEST-002d**: The Test Engineer uses `ProjectFileManager` to read PMSpec.md and Architecture.md, and `IGitHubService.GetIssueAsync` to read linked issue details.

### REQ-TEST-003: Test PR Workflow

- **REQ-TEST-003a**: Test PRs are created on branches named `agent/testengineer/{sourcepr-number}-tests`.
- **REQ-TEST-003b**: After creating a test PR, the Test Engineer marks it ready-for-review and sends a `ReviewRequestMessage` to request PE review.
- **REQ-TEST-003c**: The PE reviews test PRs before they are merged, using the same review loop as code PRs.
- **REQ-TEST-003d**: After successful test generation, the Test Engineer applies a `tested` label to the source PR. This label persists across restarts and is the primary dedup mechanism.

### REQ-TEST-004: Test Engineer Rework Loop

- **REQ-TEST-004a**: Test Engineer subscribes to `ChangesRequestedMessage` and enqueues rework items when feedback targets its test PR.
- **REQ-TEST-004b**: Rework follows the same pattern as engineer agents: read feedback → AI fixes → commit → re-mark ready-for-review → re-request review.
- **REQ-TEST-004c**: Maximum 3 rework attempts per test PR before force-completing.

### REQ-TEST-005: Test Engineer Restart Recovery

- **REQ-TEST-005a**: On restart, Test Engineer scans for open test PRs it authored (matching branch pattern).
- **REQ-TEST-005b**: If an open test PR has unaddressed CHANGES_REQUESTED feedback (from GitHub comments), the feedback is queued for rework.
- **REQ-TEST-005c**: If an open test PR has no reviews, it re-requests review.
- **REQ-TEST-005d**: `_testedPRs` HashSet is populated from the `tested` label on source PRs (persisted on GitHub, not in-memory only).
- **REQ-TEST-005e**: If source files from a merged PR no longer exist on main (e.g., files were deleted), the PR is marked as tested and skipped. It MUST NOT retry every cycle.

---

## 10. Communication & Message Bus Requirements

### REQ-MSG-001: Message Types

| Message Type | From | To | Purpose |
|-------------|------|-----|---------|
| `TaskAssignmentMessage` | PM | Researcher/Agents | Initial task assignment (legacy, used for research kickoff) |
| `StatusUpdateMessage` | Any | Broadcast (*) | Status changes, phase signals (ResearchComplete, PMSpecReady, etc.) |
| `PlanningCompleteMessage` | PM | PE (broadcast) | All User Story Issues created, PE can start planning |
| `IssueAssignmentMessage` | PE | Specific Engineer (by Id) | Assign a GitHub Issue to an engineer |
| `ClarificationRequestMessage` | Engineer | PM (broadcast) | Engineer has questions about an Issue |
| `ClarificationResponseMessage` | PM | Specific Engineer (by Id) | PM answers engineer's question |
| `ReviewRequestMessage` | Engineer | Broadcast (*) | PR ready for review |
| `ChangesRequestedMessage` | PM/PE | Broadcast (*) | Reviewer requests changes on PR |
| `ResourceRequestMessage` | PE | PM (broadcast) | Request additional engineer |
| `HelpRequestMessage` | Any | PM | Request help/escalation |

### REQ-MSG-002: Routing Rules

- **REQ-MSG-002a**: Targeted messages (non-broadcast) MUST use `Identity.Id`, never `DisplayName`.
- **REQ-MSG-002b**: Broadcast messages use `ToAgentId = "*"`.
- **REQ-MSG-002c**: `ClarificationResponseMessage` targets the original requester via `request.FromAgentId`.
- **REQ-MSG-002d**: `IssueAssignmentMessage` targets the specific engineer via their `Identity.Id` from the `AgentRegistry`.
- **REQ-MSG-002e**: `ChangesRequestedMessage` is broadcast; the author engineer filters by matching `CurrentPrNumber`.

### REQ-MSG-003: Message Delivery

- **REQ-MSG-003a**: Each agent has a bounded channel (capacity 1000) as its mailbox.
- **REQ-MSG-003b**: Messages are dispatched to handlers by type, including base type hierarchy.
- **REQ-MSG-003c**: Handler errors are logged but don't crash the dispatch loop.

---

## 11. GitHub Integration Requirements

### REQ-GH-001: PR Conventions

- **REQ-GH-001a**: PR titles: `{AgentDisplayName}: {TaskTitle}`.
- **REQ-GH-001b**: PR branches: `agent/{agent-name-slug}/{task-slug}`.
- **REQ-GH-001c**: Code PRs include a `.agentsquad/{task}.task` marker file so the branch differs from main before any code is committed.
- **REQ-GH-001d**: PRs link to Issues via `Closes #N` in the PR body for automatic closure on merge.

### REQ-GH-002: Issue Conventions

- **REQ-GH-002a**: Issue titles: `{AgentDisplayName}: {Title}` for assigned work.
- **REQ-GH-002b**: Enhancement Issues (from PMSpec) are labeled `enhancement`.
- **REQ-GH-002c**: Documentation Issues are labeled `documentation`.
- **REQ-GH-002d**: Executive escalation Issues are labeled `executive-request`.
- **REQ-GH-002e**: Blocker Issues are labeled `blocker` + `agent-stuck`.
- **REQ-GH-002f**: Resource request Issues are labeled `resource-request` + `executive-request`.

### REQ-GH-003: Document PRs

- **REQ-GH-003a**: Document PRs (Research.md, PMSpec.md, Architecture.md, EngineeringPlan.md) use PR-first pattern: create PR before AI work, commit after.
- **REQ-GH-003b**: Document PRs are auto-merged by the authoring agent (no review needed).
- **REQ-GH-003c**: Stale content from reused branches is cleaned before new commits.

### REQ-GH-004: Label Management

- **REQ-GH-004a**: PRs start with `in-progress` label during work.
- **REQ-GH-004b**: When ready for review, `in-progress` is swapped for `ready-for-review`.
- **REQ-GH-004c**: When approved, `approved` label is added.
- **REQ-GH-004d**: Complexity labels (`high-complexity`, `medium-complexity`, `low-complexity`) are added at PR creation.

---

## 12. PR Review & Merge Requirements

### REQ-REV-001: Dual-Agent Approval

- **REQ-REV-001a**: Code PRs require approval from BOTH PM ("ProgramManager") and PE ("PrincipalEngineer") before merging.
- **REQ-REV-001b**: Reviewers post comments with `**[AgentName] APPROVED**` or `**[AgentName] CHANGES REQUESTED**` markers.
- **REQ-REV-001c**: The last approver (whoever sees both have approved) triggers the merge.
- **REQ-REV-001d**: All merges use squash-and-merge (`PullRequestMergeMethod.Squash`).
- **REQ-REV-001e**: Head branch is deleted after merge.

### REQ-REV-002: Review Scope

- **REQ-REV-002a**: Reviewers evaluate PRs ONLY against their own stated description and acceptance criteria.
- **REQ-REV-002b**: PRs are NOT expected to cover the entire PMSpec, Architecture, or project scope.
- **REQ-REV-002c**: It is expected that PRs will be smaller chunks of the entire solution.
- **REQ-REV-002d**: Reviewers should NOT request changes because a PR doesn't cover something outside its stated scope.

### REQ-REV-002.5: Code-Aware Reviews (All Reviewers)

- **REQ-REV-002.5a**: ALL reviewers MUST read the actual code files committed in the PR (via `GetPRCodeContextAsync`), not just the PR title and description. Reviews based only on PR body text are insufficient.
- **REQ-REV-002.5b**: ALL reviewers MUST read the linked issue (user story + acceptance criteria) parsed from the PR body ("Closes #N") via `ParseLinkedIssueNumber` + `GetIssueAsync`.
- **REQ-REV-002.5c**: Each reviewer reads the context documents appropriate to their expertise:
  - **Architect**: Architecture.md + PMSpec.md + linked issue + code files
  - **PM (ProgramManager)**: PMSpec.md + EngineeringPlan.md + linked issue + code files
  - **PE (PrincipalEngineer)**: Architecture.md + PMSpec.md + EngineeringPlan.md + linked issue + code files
- **REQ-REV-002.5d**: The AI review prompt MUST explicitly instruct the model to evaluate the actual code, not just the PR description.
- **REQ-REV-002.5e**: Code files are read from the PR's head branch and truncated per-file at 8,000 characters to stay within token budgets. Non-code files (images, binary) are excluded.
- **REQ-REV-002.5f**: `PullRequestWorkflow.GetPRCodeContextAsync(prNumber, headBranch)` is the shared helper for building code context. It reads changed files, filters to code extensions, and formats them for AI prompts.

### REQ-REV-003: Review Triggering

- **REQ-REV-003a**: Reviews are triggered ONLY by `ReviewRequestMessage` via message bus (not by polling labels).
- **REQ-REV-003b**: PM and PE subscribe to `ReviewRequestMessage` and queue PR numbers.
- **REQ-REV-003c**: `NeedsReviewFromAsync` detects if a rework `ReviewRequest` was posted after the reviewer's last comment (requires re-review).
- **REQ-REV-003d**: Agents should NOT review PRs until engineering agents are done (content beyond metadata is committed).

### REQ-REV-004: Preventing Review Spam

- **REQ-REV-004a**: `_reviewedPrNumbers` HashSet prevents re-reviewing in the same loop.
- **REQ-REV-004b**: `HandleReviewRequestAsync` removes from `_reviewedPrNumbers` when rework is submitted (so re-review happens).
- **REQ-REV-004c**: `HasAgentReviewedAsync` returns true for ANY review (approved OR changes-requested) — prevents duplicate reviews.
- **REQ-REV-004d**: Architect MUST call `NeedsReviewFromAsync` before reviewing to prevent duplicate reviews across restarts. The in-memory `_reviewedPrNumbers` is lost on restart; `NeedsReviewFromAsync` checks GitHub comments as source of truth.

### REQ-REV-005: PE-Authored PR Reviewer Substitution

- **REQ-REV-005a**: When the PE authors a PR, it cannot review its own work. The required reviewers are dynamically substituted: PM + Architect (instead of the default PM + PE).
- **REQ-REV-005b**: `GetRequiredReviewers(prAuthorRole)` returns `["ProgramManager", "Architect"]` when the author is PrincipalEngineer.
- **REQ-REV-005c**: The Architect agent subscribes to `ReviewRequestMessage` and reviews PRs alongside the PM when the PE is the author.

**Scenario: Dual Review and Merge**
1. Engineer marks PR #35 ready-for-review → broadcasts `ReviewRequestMessage`
2. PM receives → queues PR #35 → next review loop: reads PR, AI evaluates against PMSpec scope → APPROVE → posts comment, checks if PE also approved → PE hasn't → waits
3. PE receives → queues PR #35 → reads PR, AI evaluates against architecture → APPROVE → posts comment, checks if PM approved → PM has → triggers squash merge → deletes branch
4. Issue #43 auto-closes because PR body contained "Closes #43"

---

## 13. Rework & Feedback Loop Requirements

### REQ-REWORK-001: Changes Requested Flow

- **REQ-REWORK-001a**: When a reviewer requests changes, they post a comment on the PR and send `ChangesRequestedMessage` (broadcast).
- **REQ-REWORK-001b**: The author engineer's `HandleChangesRequestedAsync` matches the message by comparing `message.PrNumber` against `CurrentPrNumber`.
- **REQ-REWORK-001c**: Matched messages are enqueued as `ReworkItem` in `ReworkQueue`.
- **REQ-REWORK-001d**: Engineer MUST keep `CurrentPrNumber` and `AssignedPullRequest` set after initial commit so rework messages can match. These are cleared only when starting a new issue assignment or when the PR is merged/closed.

### REQ-REWORK-002: Rework Implementation

- **REQ-REWORK-002a**: Engineer dequeues `ReworkItem` → reads PR + feedback + Architecture + PMSpec context.
- **REQ-REWORK-002b**: AI produces fixes addressing ALL feedback points.
- **REQ-REWORK-002c**: Fixes are committed to the same PR branch.
- **REQ-REWORK-002d**: PR is re-marked ready-for-review → new `ReviewRequestMessage` broadcast.
- **REQ-REWORK-002e**: This creates a loop: review → changes requested → rework → re-review → until approved.

### REQ-REWORK-003: PR State Tracking

- **REQ-REWORK-003a**: Engineers check if their tracked PR has been merged/closed each loop iteration. If so, clear `CurrentPrNumber` and `AssignedPullRequest`.
- **REQ-REWORK-003b**: Recovery after restart: if an open PR is found with `ready-for-review` label, re-track it (set `CurrentPrNumber`) so rework feedback can still reach the engineer. Do NOT re-implement it.
- **REQ-REWORK-003c**: Recovery after restart: if an open PR is found with `in-progress` label (no ready-for-review), treat it as needing implementation via `WorkOnExistingPrAsync`.

**Scenario: Rework Loop**
1. PM reviews PR #35 → AI finds missing error handling → posts "CHANGES REQUESTED" comment → sends `ChangesRequestedMessage { PrNumber=35, Feedback="Missing null checks in auth controller..." }`
2. Senior Engineer receives broadcast → `CurrentPrNumber == 35` → matches → enqueues `ReworkItem`
3. Next loop: dequeues rework → AI reads original PR + feedback + PMSpec + Architecture → produces fixed files
4. Commits fixes to PR #35 branch → re-marks ready-for-review → sends new `ReviewRequestMessage`
5. PM receives ReviewRequest → `_reviewedPrNumbers.Remove(35)` → re-reviews → this time approves
6. PE reviews → approves → merge triggered

---

## 14. Clarification & Escalation Requirements

### REQ-CLAR-001: Engineer → PM Clarification Loop

- **REQ-CLAR-001a**: When AI analysis of an Issue produces questions (not "NO_QUESTIONS"), engineer enters clarification loop.
- **REQ-CLAR-001b**: Engineer posts questions as a comment on the GitHub Issue mentioning the PM.
- **REQ-CLAR-001c**: Engineer sends `ClarificationRequestMessage` to PM via bus.
- **REQ-CLAR-001d**: Engineer status changes to `Blocked` while waiting.
- **REQ-CLAR-001e**: Engineer polls `ClarificationResponses` queue every 5 seconds, timeout after ~5 minutes.
- **REQ-CLAR-001f**: Maximum round-trips is configurable (`MaxClarificationRoundTrips`, default 5). After max rounds, engineer proceeds with best understanding.
- **REQ-CLAR-001g**: If no response received within timeout, engineer proceeds anyway (logs warning).
- **REQ-CLAR-001h**: Mid-work clarification: if engineer encounters unclear requirements during implementation, it can pause and ask questions on the Issue.

### REQ-CLAR-002: PM → Executive Escalation

- **REQ-CLAR-002a**: If PM's AI indicates uncertainty ("UNSURE") when answering a clarification, PM creates an `executive-request` Issue.
- **REQ-CLAR-002b**: Issue is assigned to the Executive GitHub username (configurable `ExecutiveGitHubUsername`, default "azurenerd").
- **REQ-CLAR-002c**: PM monitors executive-request Issues for responses.
- **REQ-CLAR-002d**: When Executive responds, PM relays the clarification back to the engineer's Issue and sends `ClarificationResponseMessage`.

**Scenario: Clarification with Executive Escalation**
1. Junior Engineer reads Issue #44 → AI produces questions: "Should the report export support PDF, Excel, or both?"
2. Junior posts comment on Issue #44: "**Junior Engineer 1** has questions: Should the report export support PDF, Excel, or both?"
3. Junior sends `ClarificationRequestMessage { IssueNumber=44, Question="..." }` → status = Blocked
4. PM dequeues request → AI analyzes but responds with "UNSURE — the PMSpec mentions 'export functionality' but doesn't specify format"
5. PM creates Issue "Executive Request: Clarification needed for Issue #44 — Report Export Format" with label `executive-request`, body references Issue #44
6. PM monitors Issue → Executive (@azurenerd) comments: "Both PDF and Excel. PDF for printing, Excel for data analysis."
7. PM reads response → posts on Issue #44: "**Program Manager** clarification: The Executive has confirmed both PDF and Excel export are required..."
8. PM sends `ClarificationResponseMessage { IssueNumber=44, Response="..." }` to Junior's agent Id
9. Junior receives response → AI re-evaluates → NO_QUESTIONS → proceeds with implementation

---

## 15. Configuration & Tech Stack Requirements

### REQ-CFG-001: Tech Stack Configuration

- **REQ-CFG-001a**: `TechStack` property in `ProjectConfig` (default: "C# .NET 8 with Blazor Server").
- **REQ-CFG-001b**: ALL agents must incorporate TechStack into their AI prompts.
- **REQ-CFG-001c**: Generated code MUST use the configured tech stack (not markdown, not other languages).

### REQ-CFG-002: Executive Configuration

- **REQ-CFG-002a**: `ExecutiveGitHubUsername` in `ProjectConfig` (default: "azurenerd").
- **REQ-CFG-002b**: `MaxClarificationRoundTrips` in `LimitsConfig` (default: 5).

### REQ-CFG-003: General Configuration

- **REQ-CFG-003a**: `appsettings.json` is gitignored. `appsettings.template.json` is committed with placeholders.
- **REQ-CFG-003b**: All config under the `AgentSquad` section, bound via `IOptions<AgentSquadConfig>`.
- **REQ-CFG-003c**: Model tiers: premium, standard, budget, local — each maps to a provider/model.
- **REQ-CFG-003d**: Per-agent model tier assignment is configurable.
- **REQ-CFG-003e**: `GitHubPollIntervalSeconds` controls how often agents poll (default 30).
- **REQ-CFG-003f**: `MaxAdditionalEngineers` caps dynamic engineer spawning (default 3).

---

## 16. Idempotency & Restart Recovery Requirements

### REQ-IDEM-001: Document Idempotency

- **REQ-IDEM-001a**: Before creating any document (Research.md, PMSpec.md, Architecture.md), check if it already has meaningful content. If so, skip creation.
- **REQ-IDEM-001b**: Even when skipping creation, still send the appropriate downstream signals (ResearchComplete, PMSpecReady, etc.) so other agents can proceed.

### REQ-IDEM-002: Issue Idempotency

- **REQ-IDEM-002a**: Before creating any GitHub Issue, check if one with a similar title already exists. If so, skip.
- **REQ-IDEM-002b**: Enhancement Issues: if any Enhancement Issues exist, skip the entire creation process and re-send PlanningCompleteMessage.
- **REQ-IDEM-002c**: Research Issues: if Research.md has content, do NOT create a new research Issue.

### REQ-IDEM-003: PR Idempotency

- **REQ-IDEM-003a**: Before creating a PR, check if one with the same title prefix already exists. If so, use the existing PR.
- **REQ-IDEM-003b**: Handle `ApiValidationException` on duplicate PR creation gracefully (retry search).

### REQ-IDEM-004: Restart Recovery

- **REQ-IDEM-004a**: PM restores previously-spawned engineers from TeamMembers.md on startup.
- **REQ-IDEM-004b**: PE restores task backlog from existing EngineeringPlan.md and skips to development loop.
- **REQ-IDEM-004c**: Engineers recover open PRs: `in-progress` PRs get re-implemented; `ready-for-review` PRs are tracked for rework but not re-implemented.
- **REQ-IDEM-004d**: When PE restarts, `_agentAssignments` is empty — PE must re-check which engineers are free and re-assign unfinished tasks.
- **REQ-IDEM-004e**: PE recovery for own PRs MUST check GitHub comments for unaddressed feedback (CHANGES_REQUESTED) using `GetPendingChangesRequestedAsync`, not just labels. If feedback exists, populate the ReworkQueue directly. If all reviewers approved, auto-merge. Only re-broadcast ReviewRequestMessage if no reviews exist at all.
- **REQ-IDEM-004f**: The in-process message bus (`InProcessMessageBus`) is volatile — ALL messages are lost on restart. Recovery logic MUST use GitHub API (comments, labels, PR state) as the source of truth, never depend on bus message replay.
- **REQ-IDEM-004g**: PE reconciles task statuses against merged PRs on startup. Tasks whose PRs are already merged are marked Complete in the backlog (uses `GetMergedPullRequestsAsync`).
- **REQ-IDEM-004h**: Test Engineer recovery: scans for open test PRs, checks GitHub comments for unaddressed feedback, re-requests review for PRs with no reviews. Uses `tested` label on source PRs as persistent dedup marker.

**Scenario: System Restart Recovery**
1. System crashes while Senior Engineer 1 has PR #35 (ready-for-review) and Junior Engineer 1 has PR #36 (in-progress)
2. On restart: PM reads TeamMembers.md → restores Senior Engineer 1 and Junior Engineer 1
3. PE reads EngineeringPlan.md → restores task backlog → enters development loop
4. Senior Engineer 1 starts → CurrentPrNumber is null → finds PR #35 with "ready-for-review" → re-tracks it (CurrentPrNumber = 35) but does NOT re-implement
5. Junior Engineer 1 starts → CurrentPrNumber is null → finds PR #36 with "in-progress" → calls `WorkOnExistingPrAsync` → re-implements
6. PE loop: finds Senior Engineer 1 not in `_agentAssignments` → checks if their Issue is still open → Issue #43 is open → re-assigns to them

---

## 17. Dashboard & Monitoring Requirements

### REQ-DASH-001: Blazor Server Dashboard

- **REQ-DASH-001a**: Dashboard embedded in Runner process (Web SDK, not separate app).
- **REQ-DASH-001b**: Real-time updates via SignalR (no page refresh needed).
- **REQ-DASH-001c**: Dark Grafana-style theme.
- **REQ-DASH-001d**: Pages: Home (agent cards), AgentDetail, GitHubFeed, TeamViz (animated avatars).

### REQ-DASH-002: Agent Cards

- **REQ-DASH-002a**: Show agent status with StatusReason as live task description (40-char truncation, tooltip for full text).
- **REQ-DASH-002b**: Per-agent model selector dropdown with edit icon toggle.
- **REQ-DASH-002c**: Error tracking badge showing error count; modal popup with details, timestamps, stack traces, reset button.
- **REQ-DASH-002d**: Timer display that resets only when status enum changes (not on reason text changes).

### REQ-DASH-003: Model Override

- **REQ-DASH-003a**: Dashboard allows runtime model override per agent via dropdown.
- **REQ-DASH-003b**: Available models listed in `ModelRegistry.AvailableCopilotModels`.

---

## 18. AI Provider Requirements

### REQ-AI-001: Copilot CLI Provider

- **REQ-AI-001a**: When `CopilotCli.Enabled` is true (default), all tiers route through `copilot` CLI binary.
- **REQ-AI-001b**: Implemented as `IChatCompletionService` — agents require zero code changes.
- **REQ-AI-001c**: Process-per-request model: each call spawns a fresh `copilot` process.
- **REQ-AI-001d**: Prompts piped via stdin to avoid shell escaping issues.
- **REQ-AI-001e**: `SemaphoreSlim` limits concurrency (configurable, default 4).
- **REQ-AI-001f**: `CliInteractiveWatchdog` auto-responds to y/n prompts, selection menus, "press enter" prompts.
- **REQ-AI-001g**: Fail-fast on credential prompts or auth failures.
- **REQ-AI-001h**: `CliOutputParser` strips ANSI codes, CLI chrome (banners, separators), resolves carriage-return overwrites.
- **REQ-AI-001i**: Model IDs use dots: `claude-opus-4.6`, `claude-sonnet-4.6`, `gpt-5.2` (not dashes).

### REQ-AI-002: Fallback

- **REQ-AI-002a**: If `copilot` binary not found at startup, `ModelRegistry` falls back to API-key provider for each tier.
- **REQ-AI-002b**: Fallback can be triggered at runtime via `ModelRegistry.TriggerFallback()`.

### REQ-AI-003: Model Strategy

- **REQ-AI-003a**: Generating from scratch always beats a draft→fix pipeline in cost, speed, and quality.
- **REQ-AI-003b**: Prefer single high-quality generation passes over iterative refinement with cheaper models.

---

## 19. Code Quality & Architecture Requirements

### REQ-CODE-001: C# Conventions

- File-scoped namespaces throughout.
- `record` types for messages, DTOs, and immutable data.
- `ArgumentNullException.ThrowIfNull()` for guard clauses.
- Async methods suffixed with `Async`, accepting `CancellationToken ct = default`.
- `ILogger<T>` with structured logging (named parameters, not string interpolation).
- `IDisposable` with `_disposed` flag to prevent use-after-dispose.

### REQ-CODE-002: Thread Safety

- `AgentRegistry` uses `ConcurrentDictionary` for lock-free reads.
- `WorkflowStateMachine` uses `lock` for state transitions.
- `AgentBase.Status` guarded by `_statusLock`.
- `ConcurrentQueue<T>` for inter-thread message queuing (ReworkQueue, AssignmentQueue, etc.).

### REQ-CODE-003: Testing

- xUnit with `[Fact]` attributes. Naming: `MethodName_ExpectedBehavior()`.
- Moq for mocking external dependencies.
- Integration tests build full DI container with real services, mock only external APIs.

---

## 20. End-to-End Workflow Scenarios

### Scenario A: Happy Path — Full Project Lifecycle

```
1. PM starts → reads project description → creates Research Issue → sends TaskAssignment to Researcher
2. Researcher creates Research.md (3 turns) → document PR → auto-merge → broadcasts ResearchComplete
3. PM receives ResearchComplete → creates PMSpec.md (2 turns) → document PR → auto-merge → broadcasts PMSpecReady
4. PM extracts 6 User Stories → creates 6 Enhancement Issues → sends PlanningCompleteMessage
5. Architect receives PMSpecReady → creates Architecture.md (5 turns) → document PR → auto-merge
6. PE receives PlanningComplete + Architecture.md → reads 6 Enhancement Issues → creates EngineeringPlan.md
7. PE assigns T1 (Low) to Junior Engineer, T2 (Medium) to Senior Engineer, starts T3 (High) itself
8. Junior reads Issue → creates PR → implements → marks ready-for-review
9. Senior reads Issue → creates PR → implements → self-reviews → marks ready-for-review
10. PE implements T3 → marks ready-for-review
11. PM reviews Junior's PR → approves; PE reviews → approves → squash merge → branch deleted
12. Senior's PR: PM approves, PE requests changes → Senior reworks → re-review → both approve → merge
13. PE's PR: PM approves → PE can't self-review → PM merge triggers (only PM needed for PE PRs? — or PE uses separate reviewer)
14. T1 complete → T4 depends on T1 → now assignable → PE assigns T4 to Junior
15. All tasks complete → signals engineering complete → Testing phase
```

### Scenario B: Clarification Loop with Multiple Rounds

```
1. PE assigns Issue #44 "Export reports" to Junior Engineer
2. Junior reads Issue → AI has 2 questions → posts on Issue → sends ClarificationRequest
3. Junior status = Blocked → polls ClarificationResponses
4. PM dequeues → AI answers question 1 clearly, unsure about question 2
5. PM posts answer for Q1 on Issue → creates Executive Request for Q2
6. PM sends partial ClarificationResponse → Junior receives → AI still has Q2 → round 2
7. Executive responds on executive-request Issue → PM relays to Issue #44
8. PM sends ClarificationResponse for Q2 → Junior receives → NO_QUESTIONS → proceeds
9. Junior creates PR → implements → review cycle
```

### Scenario C: Rework Feedback Loop (3 iterations)

```
1. Senior creates PR #35 → implements → marks ready-for-review
2. PM reviews → APPROVE; PE reviews → CHANGES REQUESTED ("missing input validation")
3. Senior receives ChangesRequested → enqueues ReworkItem → AI fixes → commits → re-marks ready
4. PM: _reviewedPrNumbers.Remove(35) → re-reviews → APPROVE (validation now present)
5. PE: _reviewedPrNumbers.Remove(35) → re-reviews → CHANGES REQUESTED ("validation messages not i18n")
6. Senior reworks again → commits → re-marks ready
7. Both reviewers re-review → both APPROVE → squash merge → branch deleted → Issue auto-closed
```

### Scenario D: System Restart Mid-Work

```
1. State before crash: PE has plan with 5 tasks, T1 assigned to Junior (PR #36 in-progress), T2 assigned to Senior (PR #35 ready-for-review), T3 PE working (PR #37 in-progress)
2. System restarts → PM reads TeamMembers.md → spawns Junior + Senior
3. PE reads EngineeringPlan.md → restores 5 tasks → enters dev loop
4. Junior starts → finds PR #36 (in-progress, no ready-for-review label) → calls WorkOnExistingPrAsync → re-implements
5. Senior starts → finds PR #35 (ready-for-review) → re-tracks it (CurrentPrNumber=35) → waits for rework/new assignment
6. PE starts → _agentAssignments empty → finds Junior free → Issue for T1 still open → re-assigns
7. PE finds Senior free → Issue for T2 still open → re-assigns
8. PE finds T3 (own task) needs work → re-creates PR or finds existing → continues
```

### Scenario E: Resource Scaling Under Load

```
1. PE creates plan: 8 tasks (2 High, 3 Medium, 3 Low)
2. Initial team: 1 Senior, 1 Junior
3. PE assigns T1(Low) to Junior, T2(Medium) to Senior, starts T3(High) itself
4. PE evaluates: 5 parallelizable tasks remaining, 0 free engineers → sends ResourceRequest for Junior
5. PM approves → spawns Junior Engineer 2 → updates TeamMembers.md
6. PE detects new Junior in registry → assigns T4(Low) to Junior 2
7. Still 3 parallelizable tasks, 0 free → PE sends ResourceRequest for Senior
8. PM approves → spawns Senior Engineer 2
9. PE assigns T5(Medium) to Senior 2
10. T1 completes → Junior 1 is free → PE assigns T6(Low) to Junior 1
11. All tasks eventually assigned and completed through the review cycle
```

### Scenario F: Test Engineer Generates Tests with Business Context

```
1. Senior Engineer's PR #35 (Issue #43 "Export reports") is reviewed, approved, and merged
2. Test Engineer scans merged PRs → finds PR #35 with 4 testable .ts files → not in _testedPRs, no "tested" label
3. Test Engineer parses "Closes #43" from PR body → fetches Issue #43 (acceptance criteria: "supports PDF and Excel export")
4. Test Engineer reads PMSpec.md (business requirements) and Architecture.md (tech patterns)
5. AI generates tests targeting: (a) acceptance criteria from Issue #43, (b) code correctness of the 4 source files, (c) edge cases
6. Creates test PR "TestEngineer: Tests for PR #35 - Export reports" on branch agent/testengineer/35-tests
7. Commits test files → marks ready-for-review → sends ReviewRequestMessage
8. PE reviews test PR → APPROVE → merge
9. Test Engineer applies "tested" label to source PR #35
10. On next restart: _testedPRs populated from "tested" label → PR #35 skipped
```

### Scenario G: Test Engineer Rework After PE Review

```
1. Test Engineer creates test PR #40 for merged PR #35
2. PE reviews test PR #40 → CHANGES REQUESTED ("Missing test for error handling in export service")
3. Test Engineer receives ChangesRequestedMessage → matches _currentTestPrNumber → enqueues rework
4. AI reads feedback + original source + existing test code → generates additional error handling tests
5. Commits fixes → re-marks ready-for-review → sends ReviewRequestMessage
6. PE re-reviews → APPROVE → merge → "tested" label applied to source PR #35
```

---

## 21. PE Integration & Branch Sync Requirements

### REQ-INTEG-001: PE Integration PR (Final Glue Phase)

- **REQ-INTEG-001a**: When the PE detects ALL engineering plan tasks are "Done" (PRs merged), it creates a final integration PR that verifies the combined result works as a whole.
- **REQ-INTEG-001b**: The integration PR is created from a new branch off latest main.
- **REQ-INTEG-001c**: AI reviews the full codebase against PMSpec + Architecture + all merged PRs and generates integration fixes: missing wiring, broken imports, config/route registration, cross-module references.
- **REQ-INTEG-001d**: The integration PR goes through the normal review cycle (PM + Architect review and approve).
- **REQ-INTEG-001e**: On merge of the integration PR, signal `testing.integration.complete` and advance toward Completion phase.

### REQ-INTEG-002: Branch Sync (Pull Latest Main)

- **REQ-INTEG-002a**: Before resuming work on an existing PR after restart, agents MUST sync their branch with the latest main using GitHub's Update Branch API (`PUT /pulls/{number}/update-branch`).
- **REQ-INTEG-002b**: Before marking a PR ready-for-review, the branch should be synced to ensure no merge conflicts.
- **REQ-INTEG-002c**: `UpdatePullRequestBranchAsync` is added to `IGitHubService` / `GitHubService`.
- **REQ-INTEG-002d**: `EngineerAgentBase` calls branch sync at key points: before `WorkOnExistingPrAsync` and before `MarkReadyForReviewAsync`.

**Scenario: Integration PR After All Tasks Complete**
```
1. PE plan has 6 tasks (T1-T6). All assigned, implemented, reviewed, and merged.
2. PE detects all tasks Complete → creates integration branch from latest main
3. AI reads full codebase + PMSpec + Architecture → finds: missing route registration for T3's controller, T5 imports a module from T2 using wrong path
4. AI generates fixes → commits to integration branch → creates PR "PrincipalEngineer: Integration — Final Assembly"
5. PM reviews integration PR → APPROVE (all user stories covered)
6. Architect reviews → APPROVE (no architectural violations)
7. Squash merge → signal testing.integration.complete → Completion phase
```

---

## Appendix: Known Bugs Fixed

These bugs were discovered during scenario analysis and fixed. Listed here as regression test targets:

| Bug | Severity | What Happened | Root Cause | Fix |
|-----|----------|--------------|------------|-----|
| Message routing by DisplayName | CRITICAL | IssueAssignmentMessage never delivered to engineers | ToAgentId used DisplayName, engineers subscribe by Identity.Id | Route by Identity.Id via EngineerInfo.AgentId |
| Assignment tracking key mismatch | CRITICAL | Engineers never freed for new work | _agentAssignments keyed by DisplayName, StatusUpdate uses agent Id | Key by Identity.Id |
| Task completion not tracked | MODERATE | PE tasks never marked Complete in backlog | Matched CurrentTask (issue title) against task.Id | Match by Name with Id fallback |
| CurrentPrNumber cleared too early | MODERATE | Rework feedback never matched to engineer | Cleared immediately after commit, before review | Keep until PR merged/closed |
| Review spam from repeated polling | MODERATE | Duplicate review comments created | HasAgentApprovedAsync returned false for CHANGES_REQUESTED | Added HasAgentReviewedAsync |
| Tech stack ignored in prompts | MODERATE | Generated code was markdown instead of C# | TechStack not incorporated into agent prompts | Added TechStack to all prompts |
| Architect duplicate reviews on restart | MODERATE | Same PR reviewed twice after restart | `_reviewedPrNumbers` in-memory only, lost on restart | Added `NeedsReviewFromAsync` check before reviewing |
| PE stuck after CHANGES_REQUESTED + restart | CRITICAL | PE waits forever for review that already happened | `ChangesRequestedMessage` lost on restart, recovery only checked labels not comments | `RecoverReadyForReviewPRsAsync` now reads GitHub comments via `GetPendingChangesRequestedAsync` |
| TestEngineer re-scans stale PRs every cycle | MODERATE | Tries to read files from old merged PRs, fails, never marks tested | `_testedPRs` in-memory only, never applies "tested" label, doesn't skip files-missing PRs | Apply "tested" label after generation, mark stale PRs as tested |
| TestEngineer tests have no business context | MODERATE | Tests only validate code structure, not acceptance criteria | AI prompt only included source code + PR description, no issue/PMSpec/Architecture context | Added full context gathering: linked issue + PMSpec + Architecture |
| Reviewers don't read actual code | CRITICAL | Reviews based on PR description only, not the committed code | AI prompts only included PR title/body, not file contents from the branch | All reviewers now read actual code files via `GetPRCodeContextAsync` |
| PM review missing linked issue | MODERATE | PM couldn't validate acceptance criteria | No issue lookup in PM review prompt | Added `ParseLinkedIssueNumber` + `GetIssueAsync` to PM review |
| Architect review missing PMSpec | MODERATE | Architect couldn't validate business alignment | Only Architecture.md included in prompt | Added PMSpec.md + linked issue to Architect review |
| PE task reconciliation crash | MODERATE | Used non-existent method | Called `GetClosedPullRequestsAsync` which didn't exist | Changed to `GetMergedPullRequestsAsync` |
