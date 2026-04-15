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
22. [Local Workspace & Build System Requirements](#22-local-workspace--build-system-requirements)
23. [Multi-Tier Test Execution Requirements](#23-multi-tier-test-execution-requirements)
24. [AI Conversation Mode Requirements](#24-ai-conversation-mode-requirements)
25. [GitHub API Rate Limit Handling](#25-github-api-rate-limit-handling)
26. [Human Gate Enforcement Requirements](#26-human-gate-enforcement-requirements)
27. [MCP Server Infrastructure Requirements](#27-mcp-server-infrastructure-requirements)
28. [SME Agent System Requirements](#28-sme-agent-system-requirements)
29. [Knowledge Pipeline Requirements](#29-knowledge-pipeline-requirements)
30. [Prompt Externalization Requirements](#30-prompt-externalization-requirements)
31. [Appendix: Known Bugs Fixed](#appendix-known-bugs-fixed)

---

## 1. System Overview

AgentSquad is a multi-agent AI system where specialized agent roles collaborate through GitHub PRs/Issues and an in-process message bus to build software projects autonomously. The system includes 7 core agent roles, user-defined custom agents, and dynamically-spawned SME (Subject Matter Expert) agents that provide specialist expertise on demand. Agents can be enhanced with per-role customization (custom system prompts, MCP tool servers, and external knowledge links). A human Executive stakeholder (@azurenerd) provides high-level direction and resolves escalations.

### Core Principles

- **REQ-SYS-001**: Agents communicate through two complementary layers: an in-process message bus (real-time, <1ms, no durability) and GitHub API (durable artifacts, human oversight).
- **REQ-SYS-002**: The message bus uses `System.Threading.Channels` with bounded capacity (1000 messages). Messages route by `ToAgentId` — set to `"*"` for broadcast, or a specific agent `Identity.Id` for targeted delivery.
- **REQ-SYS-003**: All message bus subscriptions and routing MUST use agent `Identity.Id` (e.g., `seniorengineer-{guid}`), never `DisplayName` (e.g., `Senior Engineer 1`). DisplayName is only for GitHub PR/Issue titles.
- **REQ-SYS-004**: The system uses .NET 8, C# 12, with nullable reference types, file-scoped namespaces, and `record` types for messages/DTOs.
- **REQ-SYS-005**: Workflow phases progress linearly: Initialization → Research → Architecture → EngineeringPlanning → ParallelDevelopment → Testing → Review → Finalization. No backward transitions. Note: the final phase was renamed from "Completion" to "Finalization" — only final review/validation items belong there. Closed engineering tasks remain in the Development phase with closed visual indicators.

**Scenario: System Startup**
1. Runner starts, registers DI services
2. `AgentSquadWorker` spawns core agents in phase order: PM → Researcher → Architect → PE
3. PM performs one-time kickoff: reads project description, seeds Researcher
4. PM restores previously-spawned engineers from TeamMembers.md
5. `AgentSquadWorker` spawns enabled custom agents from configuration
6. Each agent initializes, subscribes to relevant message types, enters its main loop

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
- **REQ-ROLE-002d**: Multiple PE agents can run simultaneously via the engineer pool. The lowest-rank online PE is the "leader" (handles orchestration-only tasks); additional PEs are "workers" who pick up tasks and review PRs but do not plan, assign, or evaluate resources. See §7 for details.

### REQ-ROLE-003: Custom Agents

- **REQ-ROLE-003a**: Users can define custom agent roles via the dashboard Configuration page or appsettings.json. Custom agents have: Name, ModelTier, Enabled flag, RoleDescription, McpServers, and KnowledgeLinks.
- **REQ-ROLE-003b**: `CustomAgentConfig` extends `AgentConfig` with `Name` and `Enabled` properties. Custom agents are listed under `AgentSquad:CustomAgents` in configuration.
- **REQ-ROLE-003c**: `CustomAgent` class extends `AgentBase` with a persona-driven implementation. Custom agents receive work via the message bus or GitHub issues, using their RoleDescription as the AI system prompt.
- **REQ-ROLE-003d**: `AgentFactory` can create custom agents. `AgentSquadWorker` spawns all enabled custom agents at startup after core agents are initialized.
- **REQ-ROLE-003e**: `AgentRole.Custom` enum value is used for custom agents. `AgentIdentity.CustomAgentName` tracks the user-defined name for display and routing.

### REQ-ROLE-004: SME (Subject Matter Expert) Agents

- **REQ-ROLE-004a**: SME agents are dynamically created at runtime when specialist expertise is needed. They are spawned by the PM (team composition) or PE (reactive spawning) and are subject to human gate approval.
- **REQ-ROLE-004b**: `SmeAgent` extends `CustomAgent` with SME-specific behavior: workflow modes (OnDemand, Continuous, OneShot), structured result reporting via `SmeResultMessage`, and graceful MCP degradation.
- **REQ-ROLE-004c**: SME agents are defined by `SMEAgentDefinition` records with: DefinitionId, RoleName, SystemPrompt, McpServers, KnowledgeLinks, Capabilities, ModelTier, MaxInstances, WorkflowMode, SubscribeTo, and CreatedByAgentId.
- **REQ-ROLE-004d**: Only PM and PE can spawn SME agents. SME agents cannot recursively spawn other SME agents.

**Scenario: Custom Agent Startup**
1. User configures a "SecurityAuditor" custom agent via dashboard with RoleDescription, standard tier, and an MCP server
2. On startup, `AgentSquadWorker` reads custom agent configs → finds SecurityAuditor with Enabled=true
3. `AgentFactory.CreateCustomAgent("SecurityAuditor", ...)` creates agent with Custom role
4. SecurityAuditor initializes, subscribes to message bus, enters its main loop using its RoleDescription as AI persona

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
Project.Description → Research.md → PMSpec.md → Architecture.md → Engineering Task Issues → Code PRs
```

- **REQ-WF-001a**: Research.md is produced by the Researcher from the project description and configurable research prompt.
- **REQ-WF-001b**: PMSpec.md is produced by the PM from Research.md + project description. Contains: Executive Summary, Business Goals, User Stories & Acceptance Criteria, Scope, Non-Functional Requirements, Success Metrics, Constraints.
- **REQ-WF-001c**: Architecture.md is produced by the Architect from PMSpec.md + Research.md.
- **REQ-WF-001d**: Engineering Task Issues (labeled `engineering-task`) are created by the PE from Architecture.md + PMSpec.md + Enhancement Issues. Each task is a GitHub Issue linking back to its parent Enhancement Issue. There is NO EngineeringPlan.md file — GitHub Issues are the single source of truth for task tracking.

### REQ-WF-002: Phase Gating

Each phase has gate conditions that must be met before advancing:

- Research → Architecture: `research.doc.ready` + `research.complete` signals
- Architecture → EngineeringPlanning: `architecture.doc.ready` + `architecture.complete` signals
- EngineeringPlanning → ParallelDevelopment: `engineering.plan.ready` + `principal.ready` signals (PE has created engineering-task issues)

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
6. PE receives PlanningCompleteMessage + detects Architecture.md → reads Enhancement Issues → creates engineering-task Issues in GitHub (each linked to parent Enhancement Issue) → enters development loop

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

### REQ-PM-005: PR Review — Phase 3 Final Business Review

- **REQ-PM-005a**: PM is the **final reviewer** in the sequential pipeline (Phase 3). PM only reviews PRs that have the `tests-added` label (indicating TE has completed Phase 2). PM does NOT review PRs without this label.
- **REQ-PM-005b**: PM proactively scans each cycle for open PRs with `tests-added` but without `pm-approved` — this is a polling gate, not triggered by `ReviewRequestMessage`.
- **REQ-PM-005c**: PM reviews PRs against PMSpec business requirements — but ONLY against the PR's own scope (not the entire PMSpec).
- **REQ-PM-005d**: Review must evaluate: business goal alignment, user story coverage for THIS task, acceptance criteria fulfillment, AND visual evidence from TE screenshots/videos posted as PR comments.
- **REQ-PM-005e**: PM gathers TE visual evidence by scanning PR comments for image URLs and video links. This evidence is included in the AI review prompt so the model can validate design and business outcomes visually.
- **REQ-PM-005f**: On **APPROVED**: PM posts `[ProgramManager] APPROVED` comment and adds the `pm-approved` label. PM does NOT call `ApproveAndMaybeMergeAsync` — merge is handled by the PE merge gate. PM sends a `StatusUpdateMessage` to notify PE that the PR is ready for merge.
- **REQ-PM-005g**: On **CHANGES REQUESTED**: PM sends `ChangesRequestedMessage` (broadcast) so the author engineer can rework. Max `MaxPmReworkCycles` retries (default 3).
- **REQ-PM-005h**: After completing all pending PR reviews, PM resets its status to Idle ("Monitoring team progress") so the dashboard accurately reflects current activity.

### REQ-PM-006: Resource Management

- **REQ-PM-006a**: PM handles `ResourceRequestMessage` from PE — spawns new engineers via `AgentSpawnManager`.
- **REQ-PM-006b**: New engineers are tracked in TeamMembers.md for persistence across restarts.
- **REQ-PM-006c**: If max additional engineers limit is reached, PM escalates to Executive.

### REQ-PM-007: Executive Escalation

- **REQ-PM-007a**: When PM cannot answer a clarification, it creates an `executive-request` Issue assigned to the Executive GitHub username (configurable, default "azurenerd").
- **REQ-PM-007b**: PM monitors executive-request Issues for responses and relays answers back to the original engineer's Issue.

### REQ-PM-008: Enhancement Issue Completion Review

- **REQ-PM-008a**: PM periodically reviews open Enhancement Issues (`ReviewEnhancementIssueCompletionAsync`). When all sub-issues (engineering tasks) for an enhancement are closed, PM does a final AI-powered acceptance review against the original acceptance criteria.
- **REQ-PM-008b**: If the AI review returns APPROVED, PM posts a summary comment and closes the enhancement issue. If NEEDS_MORE_WORK, PM posts the gap analysis as a comment but keeps the issue open.
- **REQ-PM-008c**: **Orphaned Enhancement Detection:** PM MUST detect enhancement issues with zero sub-issues after the engineering phase has started. If an enhancement has been open with no linked engineering tasks for more than one full engineering loop cycle, PM should flag it with a comment noting the gap and notify the PE (rather than silently skipping it forever).
- **REQ-PM-008d**: PM tracks reviewed enhancements in `_reviewedEnhancementIssues` to prevent re-reviewing the same issue every loop.

**Scenario: PM Closes Completed Enhancement**
1. PM created Enhancement Issue #53 "User Authentication" with 3 engineering-task sub-issues (#60, #61, #62)
2. All 3 sub-issues closed (PRs merged) → PM detects all closed
3. AI acceptance review: compares acceptance criteria vs. completed tasks → APPROVED
4. PM posts: "✅ PM Final Review — APPROVED. All 3 tasks delivered." → closes #53

**Scenario: Orphaned Enhancement Detected**
1. PM created Enhancement Issue #55 "Data Export" — PE's engineering plan missed it
2. PE created tasks for all other enhancements but none referencing #55
3. PM's enhancement review: #55 has 0 sub-issues → skipped (old behavior was silent)
4. After engineering phase started: PM detects #55 has been orphaned → flags with comment: "⚠️ This enhancement has no linked engineering tasks. Notifying PE for resolution."
5. PE receives notification → creates additional engineering tasks or comments with justification

### REQ-PM-009: Team Composition Pipeline

- **REQ-PM-009a**: After PMSpec creation, PM initiates the Team Composition Pipeline. PM gathers project description + Research.md + PMSpec.md as input context.
- **REQ-PM-009b**: `AgentTeamComposer.BuildTeamCompositionPromptAsync()` builds an AI prompt that includes the project context, available agent catalog, MCP server definitions, and SME templates.
- **REQ-PM-009c**: AI proposes the optimal team composition as structured JSON. `ParseProposal()` converts the AI response into a `TeamCompositionProposal` record.
- **REQ-PM-009d**: The proposal is subject to the `AgentTeamComposition` human gate — the director reviews and approves/rejects the proposed team composition before any agents are spawned.
- **REQ-PM-009e**: On approval, the PM spawns new SME agents from the proposal (using existing templates or new definitions) and saves the approved composition to `TeamComposition.md`.
- **REQ-PM-009f**: After team composition is complete, PM signals `TeamCompositionComplete` via the message bus.
- **REQ-PM-009g**: PM sends `TeamCompositionProposalMessage` (broadcast) when the proposal is ready for review, and processes `TeamCompositionApprovalMessage` when the gate decision is made.

**Scenario: PM Team Composition Pipeline**
1. PM creates PMSpec.md → gathers project description + Research.md + PMSpec.md
2. `AgentTeamComposer.BuildTeamCompositionPromptAsync()` builds prompt with agent catalog + 3 MCP servers + 2 SME templates
3. AI proposes: 2 Senior Engineers, 1 Junior Engineer, 1 SME "DatabaseExpert" (from template), 1 SME "UIAccessibilitySpecialist" (new definition)
4. PM sends `TeamCompositionProposalMessage` → human gate `AgentTeamComposition` activated
5. Director reviews proposal → approves with modification (removes Junior Engineer)
6. PM spawns: 2 SEs, DatabaseExpert SME (from template), UIAccessibilitySpecialist SME (new definition)
7. PM saves `TeamComposition.md` → signals `TeamCompositionComplete`

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

### REQ-ARCH-002: PR Review — Phase 1 Gatekeeper (Architecture Alignment)

- **REQ-ARCH-002a**: Architect subscribes to `ReviewRequestMessage` and is the **first reviewer** in the sequential pipeline (Phase 1).
- **REQ-ARCH-002b**: Reviews code PRs for architecture pattern compliance — scoped to the PR's own task, not the entire architecture.
- **REQ-ARCH-002c**: Architect review reads Architecture.md + PMSpec.md + linked issue + actual code files from the PR branch.
- **REQ-ARCH-002d**: On **APPROVED**: Architect adds the `architect-approved` label to the PR. Architect does NOT call `ApproveAndMaybeMergeAsync` and does NOT trigger merge — it is decoupled from merge logic entirely.
- **REQ-ARCH-002e**: On **CHANGES REQUESTED**: Architect sends `ChangesRequestedMessage` for the author engineer to rework. Max `MaxArchitectReworkCycles` retries (default 3).
- **REQ-ARCH-002f**: Architect skips PRs that already have the `architect-approved` label to prevent duplicate reviews.
- **REQ-ARCH-002g**: Architect also reviews PE-authored PRs (part of the reviewer substitution when PE is the author — see REQ-REV-005).
- **REQ-ARCH-002h**: Architect uses `NeedsReviewFromAsync` to prevent duplicate reviews across restarts.

**Scenario: Architecture Phase**
1. Architect receives PMSpecReady → reads PMSpec.md + Research.md
2. Opens document PR → 5-turn AI conversation → produces Architecture.md
3. Commits → auto-merges → broadcasts ArchitectureComplete
4. Later: receives ReviewRequest for Senior's PR #35 → reads actual code files from branch + Architecture.md + PMSpec.md + linked issue → evaluates → APPROVED → adds `architect-approved` label (Phase 1 complete, TE Phase 2 can begin)

---

## 7. Principal Engineer Agent Requirements

### REQ-PE-001: Two-Phase Loop

- **REQ-PE-001a**: Phase 1: Wait for Architecture.md + PlanningCompleteMessage + Enhancement Issues → create engineering-task Issues in GitHub. **(Leader only)**
- **REQ-PE-001b**: Phase 2: Continuous development loop with priorities: rework → assignment → own tasks → review → resource evaluation.
- **REQ-PE-001c**: On restart, if engineering-task Issues already exist, restore task backlog from them and skip to Phase 2.
- **REQ-PE-001d**: PE can begin working on tasks (Phase 2) even if no other engineers have been spawned yet. It assigns High-complexity tasks to itself and starts immediately.
- **REQ-PE-001e**: Non-leader PEs entering Phase 1 sync task state from existing GitHub engineering-task issues rather than creating the plan. They wait for the leader to create tasks before entering Phase 2.

### REQ-PE-002: Engineering Task Issue Creation

- **REQ-PE-002a**: PE reads PMSpec.md, Architecture.md, and ALL Enhancement-labeled GitHub Issues.
- **REQ-PE-002b**: AI maps each Enhancement Issue to engineering tasks with: ID, parent Issue number, name, description, complexity (High/Medium/Low), dependencies.
- **REQ-PE-002c**: Each engineering task is created as a GitHub Issue labeled `engineering-task`. The issue body contains structured metadata: parent issue reference, complexity, dependencies (as issue numbers), and detailed description.
- **REQ-PE-002d**: Task complexity mapping: High → PE, Medium → Senior Engineers, Low → Junior Engineers. When the pool is PE-only (SE=0, JE=0), all complexity levels are handled by PE agents.
- **REQ-PE-002e**: Engineering-task issues link back to their parent Enhancement issue via `Parent: #N` in the body. This replaces the old EngineeringPlan.md file — there is no markdown plan file.
- **REQ-PE-002f**: Dependencies between tasks are tracked as issue numbers in the body (e.g., `Dependencies: #106, #107`). A task is assignable only when all dependency issues are closed.
- **REQ-PE-002g**: Task status is tracked by issue state: open = pending/in-progress, closed = done. The `in-progress` label indicates active work.
- **REQ-PE-002h**: Only the leader PE creates engineering-task issues. This is idempotent — if issues already exist (e.g., from a prior run), the leader loads them instead of recreating.
- **REQ-PE-002i**: **Foundation-First Planning:** The first engineering task (T1) MUST be a project foundation/scaffolding task with NO dependencies. All other tasks MAY depend on T1. If the AI-generated T1 is not a foundation task, the PE searches the task list for keywords (`foundation`, `scaffold`, `setup`, `structure`, `skeleton`, `template`, `infrastructure`, `project setup`) and promotes the matching task to position 0. T1's dependencies are always cleared.
- **REQ-PE-002j**: The PE prompt includes guidance to create tasks suitable for parallel work with minimal overlap and merge conflict potential. Tasks should target different modules, directories, or components.

**Scenario: Foundation-First Task Ordering**
1. AI generates engineering plan: T1="Implement auth endpoints", T2="Create project scaffold", T3="Build user CRUD"
2. PE detects T1 is NOT a foundation task (no scaffold/setup keywords)
3. PE finds T2 contains "scaffold" → promotes T2 to position 0, shifts T1 to position 1
4. T2 (now first) has dependencies cleared → will be implemented first
5. Result: scaffold creates the project structure; T1 and T3 build on top of it in parallel

**Scenario: PE Creates Engineering Tasks as Issues**
1. PE receives PlanningCompleteMessage + Architecture.md is ready
2. PE fetches all Enhancement Issues (e.g., 4 Issues)
3. AI analyzes Issues + PMSpec + Architecture → produces TASK|ID|IssueNum|Name|Desc|Complexity|Deps lines
4. PE parses tasks → creates GitHub Issues labeled `engineering-task` for each task, with structured body containing parent issue, complexity, dependencies
5. Dependencies are resolved to issue numbers (e.g., T1 depends on nothing, T3 depends on T2's issue #107)
6. Enters Phase 2 development loop — picks first assignable task

### REQ-PE-003: Task Assignment

- **REQ-PE-003a**: PE checks registered engineers via `AgentRegistry` (not TeamMembers.md). **(Leader only)**
- **REQ-PE-003b**: For each free engineer (including non-leader PEs, SEs, and JEs): find next unassigned task matching their complexity tier.
- **REQ-PE-003c**: Assignment process: update GitHub Issue title to `{EngineerName}: {TaskName}` → send `IssueAssignmentMessage` to engineer's `Identity.Id`.
- **REQ-PE-003d**: Track assignments in `_agentAssignments` dictionary keyed by agent `Identity.Id` (not DisplayName).
- **REQ-PE-003e**: When an assignment's Issue is closed (completed), clear the assignment so the engineer can receive new work.
- **REQ-PE-003f**: Non-leader PEs are treated as assignable workers. The leader assigns tasks to them using the same `IssueAssignmentMessage` mechanism, with PE-appropriate complexity preferences (High → Medium → Low).

**Scenario: PE Assigns Task to Engineer**
1. PE loops through registered engineers → finds Senior Engineer 1 has no active assignment
2. PE finds next Medium-complexity Pending task with met dependencies: T2 "Implement user auth" (Issue #43)
3. PE updates Issue #43 title to "Senior Engineer 1: Implement user auth"
4. PE sends `IssueAssignmentMessage { ToAgentId=seniorengineer-abc123, IssueNumber=43, Complexity="Medium" }`
5. PE records `_agentAssignments["seniorengineer-abc123"] = 43` and updates task status to "Assigned"

### REQ-PE-004: Own Task Implementation

- **REQ-PE-004a**: PE works on High-complexity tasks itself. Non-leader PEs work on any complexity.
- **REQ-PE-004b**: PE assigns the Issue to itself (updates title with own DisplayName).
- **REQ-PE-004c**: PE creates PR with detailed AI-generated description including: summary, acceptance criteria, implementation notes, testing approach.
- **REQ-PE-004d**: PR body includes `Closes #{IssueNumber}` to auto-close the Issue on merge.
- **REQ-PE-004e**: PE commits implementation and marks PR ready-for-review.
- **REQ-PE-004f**: Non-leader PEs skip the "defer to spawning engineers" guard — they always seek work immediately. Only the leader PE defers non-High tasks during the spawn cooldown window.

### REQ-PE-005: PR Review (Technical Quality)

- **REQ-PE-005a**: PE subscribes to `ReviewRequestMessage` and queues PRs for review. **(All PEs)**
- **REQ-PE-005b**: PE skips reviewing its own PRs. PR title matching uses `{DisplayName}:` (with colon delimiter) to prevent "PrincipalEngineer" from matching "PrincipalEngineer 1:" PRs.
- **REQ-PE-005c**: PE reviews code PRs against architecture and engineering-task issue context — scoped to the PR's task, NOT the full project.
- **REQ-PE-005d**: Review evaluates: architecture patterns, implementation completeness for THIS task, code quality, error handling, test coverage.
- **REQ-PE-005e**: If changes requested, PE sends `ChangesRequestedMessage` with feedback details.
- **REQ-PE-005f**: Cross-PE review dedup: before reviewing a PR, check GitHub comments for any `[PrincipalEngineer` prefix. If any PE has already reviewed and no new rework commits exist, skip the review. This prevents multiple PEs from reviewing the same PR redundantly.

### REQ-PE-006: Resource Evaluation

- **REQ-PE-006a**: PE evaluates if more workers are needed based on parallelizable pending tasks vs. available workers. **(Leader only)**
- **REQ-PE-006b**: PE sends `ResourceRequestMessage` when parallelizable tasks significantly exceed available workers.
- **REQ-PE-006c**: Resource requests prefer PE pool first (PEs produce more robust code), falling back to SE pool then JE pool when PE pool is exhausted. The old complexity-based role selection (Low→Junior, Medium→Senior) is replaced by pool-priority ordering.
- **REQ-PE-006d**: Pool capacity is checked against `EngineerPoolConfig` — the PE estimates remaining capacity by comparing current agent count per role against configured pool limits.

### REQ-PE-007: Multi-PE Leader Election

- **REQ-PE-007a**: Each PE has a `Rank` (integer, 0-based). The core PE spawned at startup has rank 0. Additional PEs from the pool have rank 1, 2, etc.
- **REQ-PE-007b**: The leader is the lowest-rank online PE (status: Working, Idle, Online, or Initializing) as determined by querying `AgentRegistry.GetAgentsByRole(PrincipalEngineer)`.
- **REQ-PE-007c**: Leader election is evaluated each loop iteration — it is dynamic. If the leader goes offline, the next-lowest-rank PE becomes leader automatically.
- **REQ-PE-007d**: Leader-only responsibilities: create engineering plan (Phase 1), create integration PR, check all tasks complete, evaluate resource needs, assign tasks to other engineers, recover orphaned assignments.
- **REQ-PE-007e**: Any PE can: pick up and implement tasks, review PRs, handle rework on own PRs, recover own in-progress PRs, process own rework feedback.
- **REQ-PE-007f**: If no PEs are online in the registry (edge case during initialization), the current PE considers itself the leader.

### REQ-PE-008: Engineer Pool Configuration

- **REQ-PE-008a**: `EngineerPoolConfig` defines per-role pool limits: `PrincipalEngineerPool` (default 2), `SeniorEngineerPool` (default 0), `JuniorEngineerPool` (default 0).
- **REQ-PE-008b**: Pool sizes define how many *additional* agents of each role can be dynamically spawned beyond core agents. The core PE (rank 0) is always spawned — the PE pool defines how many extra PEs can be added.
- **REQ-PE-008c**: `MaxAdditionalEngineers` is a computed property (sum of all pool sizes) for backward compatibility.
- **REQ-PE-008d**: Default configuration (PE=2, SE=0, JE=0) means only additional PE agents are spawned — no Senior or Junior engineers. This is the recommended configuration because PE-tier prompts produce more robust code.
- **REQ-PE-008e**: Alternative configuration example: PE=0, SE=2, JE=1 allows up to 2 Senior Engineers and 1 Junior Engineer to be spawned (classic behavior), with no additional PEs.
- **REQ-PE-008f**: The `AgentSpawnManager` enforces per-role limits independently. It tracks `_spawnedPEs`, `_spawnedSEs`, `_spawnedJEs` separately and checks the correct pool for each spawn request.

### REQ-PE-009: Enhancement Coverage Validation

- **REQ-PE-009a**: After the PE finalizes the engineering plan (all engineering-task issues created), it MUST validate that every open PM Enhancement issue has at least one linked engineering-task.
- **REQ-PE-009b**: For each enhancement with zero engineering tasks: PE asks AI whether the enhancement was intentionally covered by other tasks (e.g., "JSON data layer is fully addressed by tasks T3 and T7") or was genuinely missed.
- **REQ-PE-009c**: If the enhancement was missed: PE creates additional engineering tasks and links them as sub-issues of the enhancement.
- **REQ-PE-009d**: If the enhancement was intentionally not given dedicated tasks (covered by other tasks): PE adds a comment on the enhancement issue explaining the justification (e.g., "This user story is fully addressed by engineering tasks T3 (#458) and T7 (#462) which implement the data models, service layer, and template file specified in the acceptance criteria.").
- **REQ-PE-009e**: This validation prevents orphaned enhancement issues that can never be closed by the PM's `ReviewEnhancementIssueCompletionAsync` (which requires at least one sub-issue to evaluate completion).

**Scenario: PE Catches Missing Enhancement Coverage**
1. PM creates 10 Enhancement Issues (#447-#456)
2. PE creates engineering plan: 9 tasks covering #447-#452, #454, #456 — but #453 and #455 missed
3. PE validation pass: iterates all 10 enhancements → finds #453 has 0 linked tasks, #455 has 0
4. AI analysis for #453: "This user story's requirements are fully addressed by T2 (data models) and T5 (service layer)" → PE posts justification comment on #453
5. AI analysis for #455: "This user story was missed — needs a dedicated task for report theming" → PE creates new task T10 linked to #455
6. Result: all enhancements have either engineering tasks or documented justification

### REQ-PE-010: Reactive SME Spawning

- **REQ-PE-010a**: During the development phase, the PE can reactively spawn SME agents when encountering tasks that require specialist expertise beyond the current team's capabilities.
- **REQ-PE-010b**: `RequestSmeIfNeededAsync(taskDescription, additionalContext, ct)` uses AI assessment to determine whether an SME is needed for a given task. The AI evaluates the task description and context against the current team's capabilities.
- **REQ-PE-010c**: If the AI determines an SME is needed: capability keywords are extracted from the task, and the system checks for existing matching templates via `SmeDefinitionGenerator.FindMatchingTemplateAsync`.
- **REQ-PE-010d**: If no matching template exists, the AI generates a new SME definition via `BuildDefinitionGenerationPrompt` → `ParseDefinition`, creating a tailored specialist agent definition.
- **REQ-PE-010e**: The SME agent is spawned via `AgentSpawnManager.SpawnSmeAgentAsync(definition, assignToIssue?, ct)`, which enforces `MaxInstances` per definition and `MaxTotalSmeAgents` globally. Spawning is subject to the `SmeAgentSpawn` human gate.
- **REQ-PE-010f**: Only the PM and PE can trigger SME agent spawning. SME agents cannot recursively spawn other SME agents.
- **REQ-PE-010g**: The PE sends `SpawnSmeAgentMessage` to request spawning, which is processed by the `AgentSpawnManager`.

**Scenario: PE Reactively Spawns SME Agent**
1. PE encounters engineering task T7 "Implement GraphQL federation gateway" — requires specialist knowledge
2. PE calls `RequestSmeIfNeededAsync("Implement GraphQL federation gateway", taskContext, ct)`
3. AI assessment: "YES — this task requires GraphQL federation expertise not present in the current team"
4. PE extracts capabilities: ["graphql", "federation", "gateway", "api-gateway"]
5. `FindMatchingTemplateAsync` → no matching template found
6. AI generates new definition: RoleName="GraphQLFederationExpert", SystemPrompt="You are a GraphQL federation specialist...", ModelTier=standard
7. PE calls `SpawnSmeAgentAsync(definition, issueNumber: 107, ct)` → human gate `SmeAgentSpawn` activated
8. Director approves → SME agent "GraphQLFederationExpert" spawned → works on task → reports findings via `SmeResultMessage`
9. PE incorporates SME findings into the implementation

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

### REQ-TEST-001: Test Generation — Phase 2 of Sequential Pipeline

- **REQ-TEST-001a**: Test Engineer is the **Phase 2 reviewer** in the sequential PR pipeline. TE scans for open PRs with the `architect-approved` label (indicating Phase 1 is complete) and generates real, runnable test code for any PR that contains testable code files.
- **REQ-TEST-001b**: Testable code files are identified by extension: `.cs`, `.ts`, `.tsx`, `.js`, `.jsx`, `.py`, `.java`, `.go`, `.rs`, `.razor`, `.blazor`, `.vue`, `.svelte`, `.rb`, `.php`, `.swift`, `.kt`. PRs with only non-code files (markdown, images, config) are skipped.
- **REQ-TEST-001c**: Test Engineer skips PRs it created (self-authored test PRs) to avoid circular testing.
- **REQ-TEST-001d**: Test Engineer uses standard model tier for all AI work.

### REQ-TEST-002: Business Context for Test Generation

- **REQ-TEST-002a**: Before generating tests, the Test Engineer MUST gather full business context: linked issue (user story + acceptance criteria from PR body "Closes #N"), PMSpec.md, and Architecture.md.
- **REQ-TEST-002b**: The AI prompt includes: (1) linked issue acceptance criteria, (2) PM Specification, (3) Architecture document patterns, (4) actual source code files from the PR, and (5) PR title and description.
- **REQ-TEST-002c**: Tests MUST validate both acceptance criteria (business behavior) and technical implementation (code correctness). Prioritize business behavior tests over structural code coverage.
- **REQ-TEST-002d**: The Test Engineer uses `ProjectFileManager` to read PMSpec.md and Architecture.md, and `IGitHubService.GetIssueAsync` to read linked issue details.

### REQ-TEST-003: Same-PR Test Workflow

- **REQ-TEST-003a**: TE adds tests to the **same PR branch** as the author engineer — it does NOT create a separate test PR. This ensures the code and its tests are reviewed and merged together as a single unit.
- **REQ-TEST-003b**: After adding tests, the TE runs the full test suite (unit, integration, UI) locally in the workspace. All tests must pass before proceeding.
- **REQ-TEST-003c**: After tests pass, the TE adds the `tests-added` label to the PR. This label is the gate for Phase 3 (PM Final Review).
- **REQ-TEST-003d**: The `tested` label on source PRs persists across restarts and is the primary dedup mechanism.

### REQ-TEST-004: Visual Evidence (Screenshots & Videos)

- **REQ-TEST-004a**: After tests pass, the TE MUST post screenshots and/or videos as comments on the PR showing what the application actually looks like.
- **REQ-TEST-004b**: Screenshots capture the rendered UI for any frontend changes (Blazor pages, components, forms). Videos capture interactive flows (navigation, form submission, animations).
- **REQ-TEST-004c**: Visual evidence is used by the PM in Phase 3 to validate that the implementation matches the design and business outcome expectations from the PMSpec.
- **REQ-TEST-004d**: If the PR has no frontend changes (backend-only), screenshots/videos are optional. The TE should still post test execution output (pass/fail summary, coverage metrics) as a PR comment.

### REQ-TEST-005: Test Engineer Rework Loop

- **REQ-TEST-005a**: Test Engineer subscribes to `ChangesRequestedMessage` and enqueues rework items when feedback targets its test PR.
- **REQ-TEST-005b**: Rework follows the same pattern as engineer agents: read feedback → AI fixes → commit → re-mark ready-for-review → re-request review.
- **REQ-TEST-005c**: Maximum `MaxTestReworkCycles` (default 3) rework attempts per PR before force-completing.

### REQ-TEST-006: Test Engineer Restart Recovery

- **REQ-TEST-006a**: On restart, Test Engineer scans for open PRs with `architect-approved` label that need testing.
- **REQ-TEST-006b**: If a PR has unaddressed CHANGES_REQUESTED feedback (from GitHub comments), the feedback is queued for rework.
- **REQ-TEST-006c**: `_testedPRs` HashSet is populated from the `tested` label on source PRs (persisted on GitHub, not in-memory only).
- **REQ-TEST-006d**: If source files from a PR no longer exist on main (e.g., files were deleted), the PR is marked as tested and skipped. It MUST NOT retry every cycle.

### REQ-TEST-007: Multi-Tier Test Strategy

- **REQ-TEST-007a**: Before generating tests, the `TestStrategyAnalyzer` determines which test tiers are needed using code-based heuristics (no AI calls). Three tiers exist: Unit (always for code changes), Integration (service/API layer), UI/Playwright (frontend components).
- **REQ-TEST-007b**: **File extension rules:** `.razor`, `.cshtml`, `.tsx`, `.jsx`, `.vue`, `.svelte`, `.html` trigger UI tests. File name patterns containing `Controller`, `Service`, `Repository`, `Handler`, `Gateway`, `Middleware`, `Hub`, `Client`, `Startup`, `Program` trigger integration tests.
- **REQ-TEST-007c**: **Keyword detection:** PR body and linked issue body are searched for UI keywords (`page`, `form`, `button`, `navigation`, `modal`, `click`, etc.) and integration keywords (`api`, `endpoint`, `database`, `http`, `authentication`, etc.).
- **REQ-TEST-007d**: **Acceptance criteria extraction:** `ExtractAcceptanceCriteria` parses checklist items (`- [ ]`, `* [ ]`) and numbered items from the issue body's acceptance criteria section. UI keywords in criteria add UI test scenarios.
- **REQ-TEST-007e**: Each tier gets a separate AI prompt with tier-specific guidance: unit tests use mocking patterns, integration tests use `WebApplicationFactory`, UI tests use Playwright Page Object Model.
- **REQ-TEST-007f**: Test files use `[Trait("Category", "Unit")]` / `[Trait("Category", "Integration")]` / `[Trait("Category", "UI")]` for xUnit tier filtering.
- **REQ-TEST-007g**: `TestStrategy` record includes `RequiredTiers` (yields active `TestTier` enum values), `Rationale` (human-readable explanation), and `UITestScenarios` (extracted from acceptance criteria).

### REQ-TEST-008: Tiered Test Execution Pipeline

- **REQ-TEST-008a**: Tests execute in priority order: Unit (fast feedback) → Integration → UI/Playwright. Each tier runs only if all prior tiers passed.
- **REQ-TEST-008b**: Each tier uses its own command from `WorkspaceConfig`: `UnitTestCommand`, `IntegrationTestCommand`, `UITestCommand`. Null values fall back to the generic `TestCommand`.
- **REQ-TEST-008c**: Each tier has independent timeout settings: `UnitTestTimeoutSeconds` (60), `IntegrationTestTimeoutSeconds` (180), `UITestTimeoutSeconds` (300).
- **REQ-TEST-008d**: Each tier gets its own AI fix-retry loop (up to `MaxTestRetries`). If unit tests fail, do NOT attempt integration/UI tests.
- **REQ-TEST-008e**: Results are collected as `AggregateTestResult` with per-tier breakdown. PR body uses `FormatAsMarkdown()` for visual reporting (✅/❌ per tier, pass/fail counts, failure details).

### REQ-TEST-009: Playwright UI Test Support

- **REQ-TEST-009a**: When `WorkspaceConfig.EnableUITests` is true and UI tests are needed, the `PlaywrightRunner` manages Playwright infrastructure.
- **REQ-TEST-009b**: Chromium browsers are auto-installed idempotently — checks for `chromium*` directory in shared cache at `{RootPath}/.playwright-browsers/`. Install uses `pwsh playwright.ps1 install chromium` (.NET) or `npx playwright install chromium` (Node).
- **REQ-TEST-009c**: All UI tests run **headless only** (`CreateNoWindow = true`, `HEADED=0` env var, `PlaywrightHeadless = true` config). Tests MUST NOT take over the user's screen.
- **REQ-TEST-009d**: App-under-test lifecycle: start application process → poll HTTP 200 readiness (up to `AppStartupTimeoutSeconds`) → run tests → kill process tree on completion.
- **REQ-TEST-009e**: When UI tests are needed and no Playwright test project exists in the workspace, auto-scaffold one via `PlaywrightRunner.GeneratePlaywrightTestScaffold()` (creates `.csproj` with Playwright + xUnit packages, `PlaywrightFixture` base class with browser lifecycle management).
- **REQ-TEST-009f**: `AppBaseUrl` is configurable (default `http://localhost:5000`) for the app-under-test address.

### REQ-TEST-010: Port Isolation for UI Tests

- **REQ-TEST-010a**: Each TE workspace gets a unique port via `DeriveUniquePort(workspacePath)` — hashes workspace path to a port in range 5100–5899 to prevent conflicts when multiple agents test simultaneously.
- **REQ-TEST-010b**: `ASPNETCORE_URLS` environment variable is set to `http://localhost:{uniquePort}` on the app-under-test process.
- **REQ-TEST-010c**: `PatchHardcodedPortBindings()` MUST scan all `Program.cs` files in the workspace before starting the app. AI-generated code frequently contains `app.Urls.Clear()` and `app.Urls.Add("http://localhost:XXXX")` which are **programmatic overrides** that defeat ALL external configuration (env vars, CLI args, appsettings).
- **REQ-TEST-010d**: Patching strategy: **comment out** the `app.Urls.Add()` and `app.Urls.Clear()` lines entirely (do NOT replace with env-var-reading code — `dotnet run` may skip recompilation). Backup originals to `.playwright-bak` files. Restore after tests complete via `RestoreOriginalPortBindings()`.
- **REQ-TEST-010e**: After patching, delete both `bin/` and `obj/` directories in the patched project to force full recompilation. Without this, `dotnet run` may use cached build output that still contains hardcoded ports.
- **REQ-TEST-010f**: The `AppStartCommand` from config (e.g., `dotnet run --project ... --urls http://localhost:5100`) has its port rewritten via `RewritePort()` to match the derived unique port.

**Scenario: UI Test Port Isolation**
1. TE starts UI tests for PR #35 in workspace `/agents/senior-engineer-1/ws`
2. `DeriveUniquePort` hashes workspace path → port 5490
3. `PatchHardcodedPortBindings` scans `Program.cs` → finds `app.Urls.Add("http://localhost:5050")` → comments it out
4. Deletes `bin/` and `obj/` → forces full recompilation
5. Starts app with `ASPNETCORE_URLS=http://localhost:5490` → app listens on 5490 (not hardcoded 5050)
6. Playwright tests navigate to `http://localhost:5490` → tests pass
7. `RestoreOriginalPortBindings` restores `.playwright-bak` files → source code reverted

### REQ-TEST-011: PR Number in Test Engineer Status Messages

- **REQ-TEST-011a**: When the Test Engineer is running tests for a specific PR, status messages MUST include the PR number (e.g., `"Running unit tests for PR #35..."` not just `"Running unit tests..."`).
- **REQ-TEST-011b**: The `prNumber` parameter is passed through the `RunTestTierWithRetryAsync` method to all status updates during test execution.

**Scenario: TE Status Messages with PR Number**
1. TE picks up PR #35 → status: `"Analyzing PR #35 test strategy..."`
2. TE runs unit tests → status: `"Running unit tests for PR #35..."`
3. TE runs integration tests → status: `"Running integration tests for PR #35..."`
4. TE runs UI tests → status: `"Running UI tests for PR #35..."`
5. Dashboard agent card shows PR number in the live status line

**Scenario: Phase 2 — TE Tests on Same PR**
1. Architect approves PR #35 → adds `architect-approved` label (Phase 1 complete)
2. TE scans open PRs → finds PR #35 with `architect-approved` and 4 testable files
3. TE parses "Closes #43" → fetches Issue #43 (acceptance criteria) + PMSpec.md + Architecture.md
4. AI generates unit + integration + UI tests → TE commits test files to PR #35's branch
5. TE runs tests locally: ✅ Unit (12 passed) → ✅ Integration (5 passed) → ✅ UI (3 passed)
6. TE captures screenshots of rendered UI pages → posts as PR comments
7. TE posts test summary comment: "✅ Unit: 12 passed | ✅ Integration: 5 passed | ✅ UI: 3 passed"
8. TE adds `tests-added` label → Phase 3 (PM) can begin

**Scenario: Multi-Tier Test Generation**
1. PR #45 contains `AuthController.cs` + `Login.razor`
2. TE picks up PR #45 → `TestStrategyAnalyzer.Analyze()` returns: `NeedsUnitTests=true` (code file), `NeedsIntegrationTests=true` (Controller pattern), `NeedsUITests=true` (.razor extension)
3. AI generates unit tests → integration tests → UI/Playwright tests with tier-specific prompts
4. TE scaffolds Playwright project (first time), writes all test files to PR #45's branch
5. Build succeeds → run unit tests (2s, 12 passed) → run integration tests (8s, 5 passed) → run UI tests headless (15s, 3 passed)
6. Posts screenshots + test summary → adds `tests-added` label

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
- **REQ-GH-002g**: Engineering Task Issues (created by PE) are labeled `engineering-task`. They also carry complexity labels (`complexity:high`, `complexity:medium`, `complexity:low`) and status labels (`in-progress`) as appropriate.

### REQ-GH-003: Document PRs

- **REQ-GH-003a**: Document PRs (Research.md, PMSpec.md, Architecture.md) use PR-first pattern: create PR before AI work, commit after.
- **REQ-GH-003b**: Document PRs are auto-merged by the authoring agent (no review needed).
- **REQ-GH-003c**: Stale content from reused branches is cleaned before new commits.

### REQ-GH-004: Label Management

- **REQ-GH-004a**: PRs start with `in-progress` label during work.
- **REQ-GH-004b**: When ready for review, `in-progress` is swapped for `ready-for-review`.
- **REQ-GH-004c**: When approved, `approved` label is added.
- **REQ-GH-004d**: Complexity labels (`high-complexity`, `medium-complexity`, `low-complexity`) are added at PR creation.

---

## 12. PR Review & Merge Requirements

### REQ-REV-001: Sequential Three-Phase PR Review Pipeline

Code PRs go through a **sequential three-phase** review pipeline. Each phase has a gate condition (a label) that must be present before the next phase begins. This ensures Architect validates architecture before tests are written, and PM validates business outcomes only after tests and visual evidence are available.

- **REQ-REV-001a**: **Phase 1 — Architect Review:** Architect is the first reviewer for all engineer-authored PRs. On approval, Architect adds the `architect-approved` label to the PR. Architect does NOT trigger merge. On changes requested, Architect sends `ChangesRequestedMessage` for the author to rework (max `MaxArchitectReworkCycles` retries, default 3).
- **REQ-REV-001b**: **Phase 2 — Test Engineer:** TE scans for open PRs with the `architect-approved` label (not `approved`). TE adds tests to the **same PR branch** as the author engineer (not a separate test PR). After tests pass and visual evidence (screenshots/videos) is posted, TE adds the `tests-added` label.
- **REQ-REV-001c**: **Phase 3 — PM Final Review:** PM only reviews PRs that have the `tests-added` label. PM proactively scans each cycle for PRs with `tests-added` but without `pm-approved`. PM validates: business alignment with PMSpec, acceptance criteria, AND visual evidence from TE screenshots/videos. On approval, PM posts `[ProgramManager] APPROVED` comment and adds `pm-approved` label. On changes requested, PM sends `ChangesRequestedMessage` (max `MaxPmReworkCycles` retries, default 3).
- **REQ-REV-001d**: **Merge Gate:** PE's `MergeTestedPRsAsync` merges PRs ONLY when both `pm-approved` AND `tests-added` labels are present. All merges use squash-and-merge (`PullRequestMergeMethod.Squash`). Head branch is deleted after merge.
- **REQ-REV-001e**: The three review labels (`architect-approved`, `tests-added`, `pm-approved`) are defined as constants in `PullRequestWorkflow.Labels`.

### REQ-REV-002: Review Scope

- **REQ-REV-002a**: Reviewers evaluate PRs ONLY against their own stated description and acceptance criteria.
- **REQ-REV-002b**: PRs are NOT expected to cover the entire PMSpec, Architecture, or project scope.
- **REQ-REV-002c**: It is expected that PRs will be smaller chunks of the entire solution.
- **REQ-REV-002d**: Reviewers should NOT request changes because a PR doesn't cover something outside its stated scope.

### REQ-REV-002.5: Code-Aware Reviews (All Reviewers)

- **REQ-REV-002.5a**: ALL reviewers MUST read the actual code files committed in the PR (via `GetPRCodeContextAsync`), not just the PR title and description. Reviews based only on PR body text are insufficient.
- **REQ-REV-002.5b**: ALL reviewers MUST read the linked issue (user story + acceptance criteria) parsed from the PR body ("Closes #N") via `ParseLinkedIssueNumber` + `GetIssueAsync`.
- **REQ-REV-002.5c**: Each reviewer reads the context documents appropriate to their expertise:
  - **Architect** (Phase 1): Architecture.md + PMSpec.md + linked issue + code files
  - **PM** (Phase 3): PMSpec.md + linked issue + code files + TE visual evidence (screenshots/videos from PR comments)
  - **PE** (code review): Architecture.md + PMSpec.md + linked issue + code files
- **REQ-REV-002.5d**: The AI review prompt MUST explicitly instruct the model to evaluate the actual code, not just the PR description.
- **REQ-REV-002.5e**: Code files are read from the PR's head branch and truncated per-file at 8,000 characters to stay within token budgets. Non-code files (images, binary) are excluded.
- **REQ-REV-002.5f**: `PullRequestWorkflow.GetPRCodeContextAsync(prNumber, headBranch)` is the shared helper for building code context. It reads changed files, filters to code extensions, and formats them for AI prompts.

### REQ-REV-003: Review Triggering

- **REQ-REV-003a**: **Architect** (Phase 1): subscribes to `ReviewRequestMessage` via message bus. This is the initial trigger from the engineer marking ready-for-review.
- **REQ-REV-003b**: **TE** (Phase 2): proactively polls for PRs with `architect-approved` label each scan cycle. Does not wait for a bus message — label presence is the gate.
- **REQ-REV-003c**: **PM** (Phase 3): proactively polls for PRs with `tests-added` label but without `pm-approved` each review cycle. Does not wait for a bus message — label presence is the gate.
- **REQ-REV-003d**: `NeedsReviewFromAsync` detects if a rework `ReviewRequest` was posted after the reviewer's last comment (requires re-review).
- **REQ-REV-003e**: Agents should NOT review PRs until engineering agents are done (content beyond metadata is committed).

### REQ-REV-004: Preventing Review Spam

- **REQ-REV-004a**: `_reviewedPrNumbers` HashSet prevents re-reviewing in the same loop.
- **REQ-REV-004b**: `HandleReviewRequestAsync` removes from `_reviewedPrNumbers` when rework is submitted (so re-review happens).
- **REQ-REV-004c**: `HasAgentReviewedAsync` returns true for ANY review (approved OR changes-requested) — prevents duplicate reviews.
- **REQ-REV-004d**: Architect MUST call `NeedsReviewFromAsync` before reviewing to prevent duplicate reviews across restarts. The in-memory `_reviewedPrNumbers` is lost on restart; `NeedsReviewFromAsync` checks GitHub comments as source of truth.
- **REQ-REV-004e**: Architect skips PRs that already have the `architect-approved` label (prevents re-reviewing already-approved PRs).

### REQ-REV-005: PE-Authored PR Reviewer Substitution

- **REQ-REV-005a**: When the PE authors a PR, it cannot review its own work. The required reviewers are dynamically substituted: PM + Architect (instead of the default PM + PE).
- **REQ-REV-005b**: `GetRequiredReviewers(prAuthorRole)` returns `["ProgramManager", "Architect"]` when the author is PrincipalEngineer.
- **REQ-REV-005c**: The Architect agent subscribes to `ReviewRequestMessage` and reviews PRs alongside the PM when the PE is the author.

### REQ-REV-006: PM Visual Validation

- **REQ-REV-006a**: During Phase 3 review, PM gathers TE visual evidence (screenshots and videos) from PR comments by scanning for image URLs and video links.
- **REQ-REV-006b**: PM's AI prompt includes this visual evidence alongside the PMSpec, linked issue, and code files to validate whether the PR meets design and business outcome expectations.
- **REQ-REV-006c**: After approval, PM sends a `StatusUpdateMessage` to notify the PE that the PR is ready for merge.

### REQ-REV-007: Vision-Based Screenshot Review

- **REQ-REV-007a**: All reviewers (PM, Architect, PE) that evaluate screenshots MUST download actual image bytes from PR comment URLs and include them as `ImageContent` items in the AI prompt — not just pass URLs as text.
- **REQ-REV-007b**: `PullRequestWorkflow.GetPRScreenshotImagesAsync` downloads images with: max 5 images per PR, max 2MB per image, 15-second timeout per download.
- **REQ-REV-007c**: Images are embedded as base64 data URIs in the CopilotCli prompt via `AppendMessageContent` helper.
- **REQ-REV-007d**: If image download fails, reviewers fall back to URL-only text context (`GetPRScreenshotContextAsync`) — degraded but not broken.
- **REQ-REV-007e**: AI review prompts MUST explicitly instruct: "examine the screenshots for error pages, blank screens, JSON parse errors, broken layouts, missing CSS, or any visual indication the application is not working correctly."

**Scenario: Vision-Based Screenshot Catches Broken UI**
1. TE posts screenshot of application on PR #35 — screenshot shows a white page with "Error: Failed to load data.json"
2. PM downloads actual image bytes (420KB PNG) → embeds as base64 ImageContent in ChatHistory
3. PM's AI prompt includes: "Examine screenshots for error pages, broken UI..."
4. AI sees the actual error page → responds: "CHANGES REQUESTED — Screenshot shows application error: 'Failed to load data.json'. The data layer is broken."
5. Engineer receives ChangesRequestedMessage with specific visual feedback → fixes data loading → TE re-screenshots → PM re-reviews → APPROVED

**Scenario: Sequential Three-Phase PR Review and Merge**
1. Engineer marks PR #35 ready-for-review → broadcasts `ReviewRequestMessage`
2. **Phase 1**: Architect receives → reads actual code files + Architecture.md + PMSpec.md + linked issue → AI evaluates architecture alignment → APPROVED → posts comment, adds `architect-approved` label
3. **Phase 2**: TE scans open PRs → finds PR #35 with `architect-approved` → adds tests to the same PR branch → runs unit/integration/UI tests → posts screenshots/videos as PR comments → adds `tests-added` label
4. **Phase 3**: PM scans PRs → finds PR #35 with `tests-added` but no `pm-approved` → reads code + linked issue + PMSpec + TE screenshots/videos → AI validates business alignment + visual evidence → APPROVED → posts comment, adds `pm-approved` label → notifies PE
5. PE's `MergeTestedPRsAsync` → sees `pm-approved` + `tests-added` → squash merge → delete branch → Issue auto-closes

**Scenario: Architect Requests Changes (Phase 1 Rework)**
1. Engineer marks PR #35 ready-for-review
2. Architect reviews → CHANGES REQUESTED ("component doesn't follow Architecture §4.2 pattern") → sends `ChangesRequestedMessage`
3. Engineer reworks → commits fixes → re-marks ready-for-review → sends new `ReviewRequestMessage`
4. Architect re-reviews → APPROVED → adds `architect-approved` label → TE proceeds to Phase 2
5. Max `MaxArchitectReworkCycles` (default 3) rework attempts before force-approval

**Scenario: PM Requests Changes (Phase 3 Rework)**
1. TE completes Phase 2 → adds `tests-added` label with passing tests + screenshots
2. PM reviews → CHANGES REQUESTED ("acceptance criteria #3 not met per screenshot") → sends `ChangesRequestedMessage`
3. Engineer reworks → commits fixes → TE re-tests → updates screenshots → PM re-reviews
4. Max `MaxPmReworkCycles` (default 3) rework attempts before force-approval

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

### REQ-REWORK-003: Multi-Phase Rework Cycles

Each phase of the sequential pipeline has its **own independent retry limit**:

- **REQ-REWORK-003a**: **Architect rework** (Phase 1): `MaxArchitectReworkCycles` (default 3). After max retries, force-approval is triggered.
- **REQ-REWORK-003b**: **TE test rework** (Phase 2): `MaxTestReworkCycles` (default 3). After max retries, TE force-completes and marks coverage as met.
- **REQ-REWORK-003c**: **PM rework** (Phase 3): `MaxPmReworkCycles` (default 3). After max retries, force-approval is triggered.
- **REQ-REWORK-003d**: All three retry limits are independently configurable in `LimitsConfig` so each review phase can be tuned separately.
- **REQ-REWORK-003e**: Rework cycles reset when the PR moves to the next phase (e.g., Architect rework count doesn't carry over to PM rework count).

### REQ-REWORK-004: PR State Tracking

- **REQ-REWORK-004a**: Engineers check if their tracked PR has been merged/closed each loop iteration. If so, clear `CurrentPrNumber` and `AssignedPullRequest`.
- **REQ-REWORK-004b**: Recovery after restart: if an open PR is found with `ready-for-review` label, re-track it (set `CurrentPrNumber`) so rework feedback can still reach the engineer. Do NOT re-implement it.
- **REQ-REWORK-004c**: Recovery after restart: if an open PR is found with `in-progress` label (no ready-for-review), treat it as needing implementation via `WorkOnExistingPrAsync`.

**Scenario: Multi-Phase Rework Loop**
1. Engineer creates PR #35 → marks ready-for-review → ReviewRequestMessage broadcast
2. **Phase 1 rework**: Architect reviews → CHANGES REQUESTED ("missing interface from Architecture §4.2") → Engineer reworks → Architect re-reviews → APPROVED → adds `architect-approved`
3. **Phase 2**: TE picks up PR #35 → adds tests → tests pass → posts screenshots → adds `tests-added`
4. **Phase 3 rework**: PM reviews → CHANGES REQUESTED ("acceptance criteria #3 not met per screenshot") → Engineer reworks → TE re-runs tests → PM re-reviews → APPROVED → adds `pm-approved`
5. PE merges PR #35 (both `pm-approved` + `tests-added` present)

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

### REQ-CFG-002.5: Rework Cycle Configuration

- **REQ-CFG-002.5a**: `MaxArchitectReworkCycles` in `LimitsConfig` (default: 3) — maximum rework attempts during Phase 1 (Architect review) before force-approval.
- **REQ-CFG-002.5b**: `MaxPmReworkCycles` in `LimitsConfig` (default: 3) — maximum rework attempts during Phase 3 (PM final review) before force-approval.
- **REQ-CFG-002.5c**: `MaxTestReworkCycles` in `LimitsConfig` (default: 3) — maximum rework attempts during Phase 2 (TE testing) before force-completion.
- **REQ-CFG-002.5d**: All three limits are independently configurable so each review phase can be tuned separately without affecting the others.

### REQ-CFG-003: General Configuration

- **REQ-CFG-003a**: `appsettings.json` is gitignored. `appsettings.template.json` is committed with placeholders.
- **REQ-CFG-003b**: All config under the `AgentSquad` section, bound via `IOptions<AgentSquadConfig>`.
- **REQ-CFG-003c**: Model tiers: premium, standard, budget, local — each maps to a provider/model.
- **REQ-CFG-003d**: Per-agent model tier assignment is configurable.
- **REQ-CFG-003e**: `GitHubPollIntervalSeconds` controls how often agents poll (default 30).
- **REQ-CFG-003f**: `MaxAdditionalEngineers` caps dynamic engineer spawning (default 3).

### REQ-CFG-004: Local Workspace Configuration

- **REQ-CFG-004a**: `WorkspaceConfig` section under `AgentSquad:Workspace`. Controls local build/test infrastructure for all agents.
- **REQ-CFG-004b**: `RootPath` (default: `C:\Agents`) — root directory for agent local repos. Auto-created at startup if it doesn't exist.
- **REQ-CFG-004c**: `BuildCommand` (default: `dotnet build`) / `TestCommand` (default: `dotnet test`) / `RestoreCommand` (default: `dotnet restore`) — per-project build/test commands configurable via config.
- **REQ-CFG-004d**: Per-tier test commands: `UnitTestCommand`, `IntegrationTestCommand`, `UITestCommand` with individual timeout settings (`UnitTestTimeoutSeconds: 60`, `IntegrationTestTimeoutSeconds: 180`, `UITestTimeoutSeconds: 300`).
- **REQ-CFG-004e**: `MaxTestRetries` (default: 3) — per-tier AI fix-retry attempts before failing.
- **REQ-CFG-004f**: Playwright settings: `PlaywrightBrowsersCachePath` (default: `{RootPath}/.playwright-browsers`), `PlaywrightHeadless` (default: true), `EnableUITests` (default: true), `AppBaseUrl` (default: `http://localhost:5000`), `AppStartCommand`, `AppStartupTimeoutSeconds`.

### REQ-CFG-005: Engineer Pool Configuration

- **REQ-CFG-005a**: `EngineerPoolConfig` section defines initial pool composition: `SeniorEngineerCount` (default: 2), `JuniorEngineerCount` (default: 1), `PrincipalEngineerCount` (default: 1).
- **REQ-CFG-005b**: Multi-PE mode: when `PrincipalEngineerCount > 1`, the first PE is the leader (creates engineering plan), additional PEs are workers (skip to Phase 2).
- **REQ-CFG-005c**: Leader/worker role assignment is based on spawn order. Non-leader PEs sync task state from existing GitHub engineering-task issues.

### REQ-CFG-006: AI Conversation Mode Configuration

- **REQ-CFG-006a**: `FastMode` (default: false) — when true, collapses multi-turn AI conversations to a single prompt. Reduces latency and cost at the expense of nuance.
- **REQ-CFG-006b**: `SinglePassMode` (default: false) — independent of `FastMode`. When true, flattens multi-turn conversation sequences (e.g., Researcher 3-turn, Architect 5-turn) into a single prompt per agent. Can be combined with any model tier.
- **REQ-CFG-006c**: Agents that support multi-turn conversations: Researcher (3 turns), Architect (5 turns), PM, Principal Engineer (2 turns). Each checks `SinglePassMode` to decide whether to collapse turns.
- **REQ-CFG-006d**: `SinglePassMode` decouples from `FastMode` so users can use premium models (high quality) with single-pass execution (lower cost/latency) — two independent axes.

### REQ-CFG-007: Per-Agent Role Customization

- **REQ-CFG-007a**: Each agent can be configured with a `RoleDescription` — custom text that overrides or augments the agent's default system prompt. The RoleDescription is injected as a `[ROLE CUSTOMIZATION]` section at the start of system prompts via `AgentBase.CreateChatHistory()`.
- **REQ-CFG-007b**: Each agent can be configured with a list of `McpServers` — MCP server names assigned to that agent. These are passed as `--mcp-server` flags to the copilot CLI process for each AI call.
- **REQ-CFG-007c**: Each agent can be configured with `KnowledgeLinks` — URLs to external documentation pages. These are fetched, content-extracted (HTML/Markdown-aware), optionally AI-summarized, and injected as a `[ROLE KNOWLEDGE]` section into system prompts.
- **REQ-CFG-007d**: `AgentConfig` model has `RoleDescription`, `McpServers`, and `KnowledgeLinks` properties. These are configured per-agent under `AgentSquad:Agents:{Role}` in appsettings.json.
- **REQ-CFG-007e**: `RoleContextProvider` service manages the full pipeline: fetching knowledge link content, extraction, summarization, caching, and budget enforcement.

### REQ-CFG-008: MCP Server Configuration

- **REQ-CFG-008a**: Global MCP server definitions are configured under `AgentSquad:McpServers` in appsettings.json. Each server is defined by `McpServerDefinition` record: Name, Description, Command, Args, Env, Transport (Stdio/Http/Sse), RequiredRuntimes, ProvidedCapabilities.
- **REQ-CFG-008b**: `McpServerRegistry` provides lookup, enumeration, and capability-based search of registered MCP servers.
- **REQ-CFG-008c**: `McpServerAvailabilityChecker` validates that required runtimes and commands are installed on the host machine before allowing agents to use an MCP server.
- **REQ-CFG-008d**: `McpServerSecurityPolicy` blocks dangerous servers (names containing shell, exec, terminal, cmd, powershell, bash), validates HTTPS-only knowledge links, rejects private network URLs, and validates definition fields.
- **REQ-CFG-008e**: `CopilotCliMcpConfigManager` (registered as `IHostedService`) synchronizes MCP server definitions to the copilot CLI's `mcp.json` configuration file at startup.

### REQ-CFG-009: SME Agent Configuration

- **REQ-CFG-009a**: SME system configuration lives under `AgentSquad:SmeAgents` section, bound to `SmeAgentsConfig`. Key properties: `Enabled` (default true), `MaxTotalSmeAgents` (default 5), `AllowAgentCreatedDefinitions` (default true), `PersistDefinitions` (default true), `DefinitionsPath` (default `sme-definitions.json`).
- **REQ-CFG-009b**: SME templates are configured under `SmeAgentsConfig.Templates` as a list of `SMEAgentDefinition` records. Templates provide pre-built specialist roles that can be instantly spawned without AI generation.
- **REQ-CFG-009c**: Custom SME definitions (created at runtime by PM or PE) are persisted to `sme-definitions.json` when `PersistDefinitions` is true. `SMEAgentDefinitionService` provides CRUD operations with JSON file persistence.
- **REQ-CFG-009d**: `SMEAgentDefinitionService` supports template lookup by name and capability-based search across both templates and custom definitions.

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
- **REQ-IDEM-004b**: PE restores task backlog from existing engineering-task GitHub Issues (`GetIssuesByLabelAsync("engineering-task")`) and skips to development loop. There is no EngineeringPlan.md to parse.
- **REQ-IDEM-004c**: Engineers recover open PRs: `in-progress` PRs get re-implemented; `ready-for-review` PRs are tracked for rework but not re-implemented.
- **REQ-IDEM-004d**: When PE restarts, `_agentAssignments` is empty — PE must re-check which engineers are free and re-assign unfinished tasks.
- **REQ-IDEM-004e**: PE recovery for own PRs MUST check GitHub comments for unaddressed feedback (CHANGES_REQUESTED) using `GetPendingChangesRequestedAsync`, not just labels. If feedback exists, populate the ReworkQueue directly. If all reviewers approved, auto-merge. Only re-broadcast ReviewRequestMessage if no reviews exist at all.
- **REQ-IDEM-004f**: The in-process message bus (`InProcessMessageBus`) is volatile — ALL messages are lost on restart. Recovery logic MUST use GitHub API (comments, labels, PR state) as the source of truth, never depend on bus message replay.
- **REQ-IDEM-004g**: PE reconciles task statuses against merged PRs on startup. Tasks whose PRs are already merged are marked Done (issue closed) using `GetMergedPullRequestsAsync`.
- **REQ-IDEM-004h**: Test Engineer recovery: scans for open PRs with `architect-approved` label, checks GitHub comments for unaddressed feedback, re-requests review for PRs with no reviews. Uses `tested` label on source PRs as persistent dedup marker.
- **REQ-IDEM-004i**: CLI session IDs (`_prSessionIds`) MUST be persisted to the SQLite database (`cli_sessions` table) so that after a runner restart, agents resume the same Copilot CLI session for each PR. This preserves the AI's full conversation history (implementation context, prior rework feedback) across restarts, enabling higher-quality rework responses. Both `EngineerAgentBase` and `TestEngineerAgent` restore their session mappings from the database during `OnInitializeAsync`.

### REQ-IDEM-005: Runner Startup vs Fresh Reset (Task File Preservation)

- **REQ-IDEM-005a**: **Runner startup MUST NOT delete task files** (`.agentsquad/*.task` marker files, `TeamMembers.md`, local workspace directories). These must persist so the runner can resume work after an accidental exit, computer restart, or crash. The runner startup path is the "resume" path.
- **REQ-IDEM-005b**: **Fresh reset (via dashboard or CLI script) MUST delete everything** needed for a clean start: all GitHub PRs (closed), all GitHub Issues (closed with appropriate labels), all non-default branches deleted, all repo files removed (except preserved files like `OriginalDesignConcept.html` and `.gitignore`), the SQLite state database, and local workspace directories.
- **REQ-IDEM-005c**: The fresh reset script (`scripts/fresh-reset.ps1`) and dashboard cleanup endpoint perform the same complete cleanup. Both MUST verify the cleanup was successful (0 open issues, 0 open PRs, 0 agent branches) before reporting completion. If verification fails, retry until clean.
- **REQ-IDEM-005d**: The fresh reset MUST close all GitHub issues by adding appropriate labels (e.g., `stale-cleanup`) so they don't appear as open work in future runs.
- **REQ-IDEM-005e**: The fresh reset MUST close all open PRs before deleting branches to avoid GitHub API errors on branch deletion.
- **REQ-IDEM-005f**: After a fresh reset, the runner starts from the Initialization phase with no prior state — identical to a first-ever run.

**Scenario: System Restart Recovery**
1. System crashes while Senior Engineer 1 has PR #35 (ready-for-review) and Junior Engineer 1 has PR #36 (in-progress)
2. On restart: PM reads TeamMembers.md → restores Senior Engineer 1 and Junior Engineer 1
3. PE loads engineering-task issues from GitHub → restores task backlog → enters development loop
4. Senior Engineer 1 starts → restores CLI session mapping from DB → CurrentPrNumber is null → finds PR #35 with "ready-for-review" → re-tracks it (CurrentPrNumber = 35) but does NOT re-implement → rework uses restored CLI session so AI has full prior context
5. Junior Engineer 1 starts → restores CLI session mapping from DB → CurrentPrNumber is null → finds PR #36 with "in-progress" → calls `WorkOnExistingPrAsync` → re-implements using restored CLI session
6. PE loop: finds Senior Engineer 1 not in `_agentAssignments` → checks if their Issue is still open → Issue #43 is open → re-assigns to them

---

## 17. Dashboard & Monitoring Requirements

### REQ-DASH-001: Blazor Server Dashboard

- **REQ-DASH-001a**: Dashboard can run in two modes: (1) embedded in the Runner process (in-process, default), or (2) as a standalone process via `AgentSquad.Dashboard.Host` on port 5051, separate from the Runner on port 5050. Standalone mode allows UI changes/restarts without killing running agents.
- **REQ-DASH-001b**: Real-time updates via SignalR (no page refresh needed). In standalone mode, data is fetched via REST API instead of in-process services.
- **REQ-DASH-001c**: Dark Grafana-style theme.
- **REQ-DASH-001d**: Pages: Home (agent cards), AgentDetail, GitHubFeed, TeamViz (animated avatars), PullRequests, Issues.

### REQ-DASH-002: Agent Cards

- **REQ-DASH-002a**: Show agent status with StatusReason as live task description (40-char truncation, tooltip for full text).
- **REQ-DASH-002b**: Per-agent model selector dropdown with edit icon toggle.
- **REQ-DASH-002c**: Error tracking badge showing error count; modal popup with details, timestamps, stack traces, reset button.
- **REQ-DASH-002d**: Timer display that resets only when status enum changes (not on reason text changes).
- **REQ-DASH-002e**: Agent detail card popup MUST show a link to the currently assigned PR (if any). The PR number is extracted via regex from the agent's `StatusReason` text (e.g., "PR #35") with fallback to `AssignedPullRequest` property. Link format: `https://github.com/{owner}/{repo}/pull/{prNumber}`.
- **REQ-DASH-002f**: Agent status messages MUST NOT show "⏳ Awaiting human approval..." when the corresponding gate is in auto mode. Guard all pre-gate `UpdateStatus` calls with `_gateCheck.RequiresHuman(gateId)` to prevent false status flash on the dashboard.

### REQ-DASH-003: Model Override

- **REQ-DASH-003a**: Dashboard allows runtime model override per agent via dropdown.
- **REQ-DASH-003b**: Available models listed in `ModelRegistry.AvailableCopilotModels`.

### REQ-DASH-004: Pull Requests Tab

- **REQ-DASH-004a**: Dedicated Pull Requests page accessible from the nav menu (🔀 icon).
- **REQ-DASH-004b**: State filter tabs: Open, Closed, All — with count badges.
- **REQ-DASH-004c**: GitHub-inspired PR cards showing: title, PR number, state indicator (🟢 open / 🟣 merged / 🔴 closed), author, branch info, labels, creation date (relative time-ago formatting).
- **REQ-DASH-004d**: PR data fetched via `DashboardDataService.GetPullRequestsAsync()` with 30-second cache TTL.
- **REQ-DASH-004e**: Cards link to GitHub PR URL (opens in new tab).

### REQ-DASH-005: Issues Tab

- **REQ-DASH-005a**: Dedicated Issues page accessible from the nav menu (📋 icon).
- **REQ-DASH-005b**: State filter tabs: Open, Closed, All — with count badges. Active tab highlighted.
- **REQ-DASH-005c**: **Label filter dropdown:** Dynamically populated from unique labels across all issues. Multi-select support. Labels color-coded by category: engineering-task (blue), executive-request (gold), complexity levels (gray), status labels (green/yellow/red).
- **REQ-DASH-005d**: **Assignee filter dropdown:** Dynamically populated from issue assignees.
- **REQ-DASH-005e**: **Sort options:** Newest, Oldest, Most commented, Recently updated. All sorting done client-side from cached data.
- **REQ-DASH-005f**: Issue cards show: title, issue number, open/closed state icon (🟢/🟣), labels as colored badges, comment count (💬), assignees, author, creation/close date with relative time-ago formatting.
- **REQ-DASH-005g**: Dropdown state management: toggling one filter dropdown closes any other open dropdown.
- **REQ-DASH-005h**: Issue data fetched via `DashboardDataService.GetIssuesAsync()` with 30-second cache TTL. Issues are filtered to exclude pull requests (GitHub API returns PRs as issues).

### REQ-DASH-006: Dashboard Port Configuration

- **REQ-DASH-006a**: Dashboard port configurable via `AgentSquad:Dashboard:Port` (default: 5050). Value wired to Kestrel at startup via `builder.WebHost.UseUrls()`.
- **REQ-DASH-006b**: Dashboard is non-essential — port conflict or binding failure MUST NOT crash the agent runner.

### REQ-DASH-007: Engineering Plan Visualization Tab

- **REQ-DASH-007a**: Dedicated Engineering Plan page (`/engineering-plan`) accessible from the nav menu (📊 icon). Visualizes engineering tasks as an interactive dependency graph using Cytoscape.js with the dagre hierarchical layout.
- **REQ-DASH-007b**: Task nodes are color-coded by status: completed (green), in-progress (yellow/amber), pending (gray), blocked (red). Node labels show task ID, name, and complexity.
- **REQ-DASH-007c**: Dependency edges shown as directed arrows between task nodes. Edge validation: skip edges where source/target node IDs don't exist (prevents layout crashes from stale cross-run data).
- **REQ-DASH-007d**: Side detail panel: clicking a task node opens a slide-out panel showing full task details (description, status, assignee, linked issue, parent enhancement, complexity, dependencies).
- **REQ-DASH-007e**: Progress summary bar at top: shows completion percentage, task counts by status, and current workflow phase.
- **REQ-DASH-007f**: `EngineeringPlanDataService` provides task data sourced from `AgentStateStore` (SQLite), registered as a scoped service in DI.
- **REQ-DASH-007g**: Layout fallback: if dagre layout fails (e.g., orphaned edges, plugin not registered), fall back to grid layout rather than showing an empty canvas. Console logging for debugging.
- **REQ-DASH-007h**: DOM readiness: graph initialization must wait for the container element to be rendered (150ms delay after `OnAfterRenderAsync`) and retry on failure rather than setting `_graphInitialized` prematurely.

**Scenario: Engineering Plan Graph Usage**
1. User navigates to `/engineering-plan` → sees dependency graph with 9 task nodes
2. T1 (foundation) at top with no incoming edges → T2-T5 depend on T1 (arrows from T1)
3. T1 is green (completed), T2-T4 yellow (in-progress), T5-T9 gray (pending)
4. User clicks T3 node → detail panel slides open showing: description, assigned to "Senior Engineer 1", Issue #460, complexity Medium
5. T7 shows red (blocked) with incoming dependency edge from T5 which is still pending

**Scenario: Dashboard Issues Tab Usage**
1. User navigates to `/issues` → sees all open issues with count badges
2. Filters by label "engineering-task" → sees only engineering work items
3. Sorts by "Most commented" → sees the most-discussed issues first
4. Clicks "Closed" tab → sees completed work with closed dates
5. Filters by assignee "senior-engineer-1" → sees only that agent's assigned issues

### REQ-DASH-008: Dashboard Process Separation (Standalone Mode)

- **REQ-DASH-008a**: `AgentSquad.Dashboard.Host` is a standalone Blazor Server project that runs on port 5051, separate from the Runner on port 5050. UI changes/restarts do not affect running agents.
- **REQ-DASH-008b**: `IDashboardDataService` interface decouples Razor pages from the data source implementation. Two implementations exist: `DashboardDataService` (in-process, used when Runner hosts dashboard) and `HttpDashboardDataService` (HTTP client, used in standalone Dashboard.Host).
- **REQ-DASH-008c**: Runner exposes a REST API at `/api/dashboard/*` (~30 endpoints) that the standalone dashboard consumes via `HttpDashboardDataService`.
- **REQ-DASH-008d**: `DashboardMode(IsStandalone: bool)` record controls NavMenu visibility and behavior differences between embedded and standalone modes.
- **REQ-DASH-008e**: `HttpDashboardDataService` MUST use `IHttpClientFactory` with named client pattern (not `AddHttpClient<T>` with separate singleton registration, which causes DI conflicts).
- **REQ-DASH-008f**: Standalone dashboard requires stub service registrations for services it doesn't host: `NullGitHubService`, `GateNotificationService`, `AgentStateStore`, `BuildTestMetrics`.

**Scenario: Dashboard Separation — UI Iteration Without Agent Disruption**
```
1. Runner starts on port 5050 with 7 agents actively working (PE implementing T3, Senior on T4)
2. Developer needs to tweak the Timeline page layout
3. Developer stops Dashboard.Host (port 5051) → edits Razor page → rebuilds Dashboard.Host → restarts
4. Runner and all agents continue uninterrupted — no state loss, no agent restarts
5. Dashboard.Host reconnects to Runner's REST API → resumes displaying live agent status
```

### REQ-DASH-009: Project Timeline Tab

- **REQ-DASH-009a**: Dedicated Timeline page (`/timeline`) accessible from the nav menu. Visualizes the project as a hierarchical timeline with expandable/collapsible groups.
- **REQ-DASH-009b**: **PM/Engineering toggle**: Timeline supports two views switched via toggle buttons. PM view shows enhancement issues as top-level groups with engineering-task children. Engineering view shows engineering-task issues as top-level groups with linked PRs as children.
- **REQ-DASH-009c**: **Node type indicators**: PRs and Issues are visually distinguished with colored badges — "PR #X" in purple and "Issue #X" in green — in both node labels and detail popups.
- **REQ-DASH-009d**: **Background refresh**: After initial load, auto-refresh happens silently without showing the "Syncing work items" overlay. The overlay only displays on first load or when the user clicks the manual refresh button.
- **REQ-DASH-009e**: **Detail panel race condition safety**: When 30s auto-refresh rebuilds `_phases`/`_groupLookup`, the `_selectedGroup` reference must be re-fetched from the new `_groupLookup` after `BuildTimeline`. Use pattern matching for null safety to prevent `NullReferenceException` crashes.
- **REQ-DASH-009f**: **Finalization phase mapping**: Only final review/validation items appear in the Finalization phase. Closed engineering tasks remain in the Development phase with closed visual indicators (strikethrough, muted colors).

### REQ-DASH-010: Force Refresh

- **REQ-DASH-010a**: Overview and Timeline pages include a Force Refresh button (🔄 icon) that invalidates all caches (including GitHub API list caches) and reloads data from source.
- **REQ-DASH-010b**: Force refresh calls `InvalidateListCaches()` on the GitHub service before fetching fresh data.

**Scenario: Timeline PM/Engineering Toggle**
```
1. User navigates to /timeline → sees PM view by default (enhancement issues as top-level)
2. Enhancement "User Authentication" shows 3 child engineering-tasks: "Implement JWT auth", "Add OAuth flow", "Create login UI"
3. User clicks "Engineering" toggle → view switches to engineering tasks as top-level
4. "Implement JWT auth" now shows child PR #42 (purple badge) and linked Issue #15 (green badge)
5. User clicks Force Refresh → caches invalidated → "Syncing work items" overlay appears → fresh data loads
6. Subsequent 30s auto-refresh happens silently — no overlay, no flicker
```

### REQ-DASH-011: SME & MCP Configuration UI

- **REQ-DASH-011a**: The Dashboard Configuration page includes an "MCP Servers" section displaying registered MCP server definitions as cards. Each card shows: server name, description, transport badge (Stdio/Http/Sse), and capability tags.
- **REQ-DASH-011b**: The Dashboard Configuration page includes an "SME Templates" section showing configured SME agent templates. Each template card displays: role name, system prompt preview, model tier, workflow mode, and associated capabilities.
- **REQ-DASH-011c**: Toggle controls are provided for: SME system enable/disable (`SmeAgents.Enabled`), agent-created definitions (`AllowAgentCreatedDefinitions`), and definition persistence (`PersistDefinitions`).
- **REQ-DASH-011d**: The Dashboard Configuration page has a "Custom Agents" section with add/remove functionality, name input field, and full per-agent accordion configuration (role description textarea, MCP server multi-select, knowledge link list with add/remove).
- **REQ-DASH-011e**: Per-agent role customization is displayed in an accordion layout. Each core agent role (PM, Researcher, Architect, PE, SE, JE, TE) has a collapsible section with: RoleDescription textarea, MCP server assignment list, and KnowledgeLinks URL list with add/remove controls.

**Scenario: Dashboard SME Configuration**
1. User navigates to Configuration page → scrolls to "MCP Servers" section
2. Sees 3 MCP server cards: "github-search" (Stdio, capabilities: code-search, repo-browse), "jira-sync" (Http, capabilities: issue-tracking), "docs-reader" (Sse, capabilities: documentation)
3. Scrolls to "SME Templates" → sees 2 templates: "DatabaseExpert" (standard tier, OnDemand) and "SecurityAuditor" (premium tier, Continuous)
4. Toggles "Allow agent-created definitions" OFF → PE can no longer generate new SME definitions at runtime
5. Scrolls to "Custom Agents" → clicks "Add Custom Agent" → enters "APIDesigner" → configures role description and assigns "docs-reader" MCP server

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

### REQ-CODE-004: Code Quality Guardrails

- **REQ-CODE-004a**: `RequirementsCompliance` diagnostic checker validates agent AI responses against the Requirements.md standards (e.g., PR title format, branch naming, commit structure).
- **REQ-CODE-004b**: `RoleExpectations` defines per-agent-role expectations as structured data (required context documents, expected output format, quality bars).
- **REQ-CODE-004c**: `AgentDiagnostic` record captures diagnostic events (timestamp, severity, agent ID, category, message) for runtime self-monitoring.
- **REQ-CODE-004d**: Diagnostics are logged but non-blocking — agents continue even if compliance checks flag issues. The purpose is visibility, not enforcement.

---

## 20. End-to-End Workflow Scenarios

### Scenario A: Happy Path — Full Project Lifecycle (Sequential Pipeline)

```
1. PM starts → reads project description → creates Research Issue → sends TaskAssignment to Researcher
2. Researcher creates Research.md (3 turns) → document PR → auto-merge → broadcasts ResearchComplete
3. PM receives ResearchComplete → creates PMSpec.md (2 turns) → document PR → auto-merge → broadcasts PMSpecReady
4. PM extracts 6 User Stories → creates 6 Enhancement Issues → sends PlanningCompleteMessage
5. Architect receives PMSpecReady → creates Architecture.md (5 turns) → document PR → auto-merge
6. PE receives PlanningComplete + Architecture.md → reads 6 Enhancement Issues → creates 14 engineering-task Issues in GitHub
7. PE starts T1 (High) itself (no engineers spawned yet), assigns T2 (Medium) to Senior Engineer when available
8. Senior reads Issue → creates PR → implements → marks ready-for-review → ReviewRequestMessage broadcast
9. Phase 1: Architect receives ReviewRequest → reads code + Architecture + PMSpec + linked issue → APPROVED → adds `architect-approved` label
10. Phase 2: TE scans for architect-approved PRs → finds Senior's PR → adds tests to same branch → runs tests → posts screenshots → adds `tests-added` label
11. Phase 3: PM scans for tests-added PRs → finds Senior's PR → reads code + PMSpec + linked issue + TE screenshots → APPROVED → adds `pm-approved` label → notifies PE
12. PE's MergeTestedPRsAsync → sees `pm-approved` + `tests-added` → squash merge → branch deleted → Issue auto-closed
13. PE's PR: PM + Architect review (PE can't self-review) → Architect Phase 1 → TE Phase 2 → PM Phase 3 → merge
14. T1 complete (issue closed) → T4 depends on T1 → dependency met → PE assigns T4 to Junior
15. All engineering-task issues closed → PE creates integration PR → PM + Architect review → merge → Finalization phase
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

### Scenario C: Multi-Phase Rework Feedback Loop

```
1. Senior creates PR #35 → implements → marks ready-for-review
2. Phase 1: Architect reviews → CHANGES REQUESTED ("missing shared interface") → Senior reworks → re-submits
3. Architect re-reviews → APPROVED → adds `architect-approved` label
4. Phase 2: TE picks up PR #35 → adds tests + screenshots → adds `tests-added` label
5. Phase 3: PM reviews (code + screenshots + PMSpec) → CHANGES REQUESTED ("acceptance criteria #2 not met per screenshot")
6. Senior reworks → commits fixes → TE re-runs tests → updates screenshots → PM re-reviews
7. PM re-reviews → APPROVED → adds `pm-approved` label → notifies PE
8. PE merges → squash merge → branch deleted → Issue auto-closed
```

### Scenario D: System Restart Mid-Work

```
1. State before crash: PE has 5 engineering-task issues, T1 assigned to Junior (PR #36 in-progress),
   T2 assigned to Senior (PR #35 ready-for-review, PM CHANGES_REQUESTED), T3 PE working (PR #37 in-progress)
2. System restarts → PM reads TeamMembers.md → spawns Junior + Senior
3. PE loads engineering-task issues from GitHub → restores 5 tasks → reconciles against merged PRs → enters dev loop
4. Junior starts → finds PR #36 (in-progress, no ready-for-review label) → calls WorkOnExistingPrAsync → re-implements
5. Senior starts → finds PR #35 (ready-for-review) → re-tracks it (CurrentPrNumber=35) → waits for rework/new assignment
6. PE starts → recovers own PRs: finds PR #37 (in-progress) → continues work on it
7. PE recovers ready-for-review PRs: checks PR #35 GitHub comments → finds PM CHANGES_REQUESTED →
   populates ReworkQueue (does NOT re-broadcast ReviewRequest) → Senior picks up rework
8. PE loop: _agentAssignments empty → re-checks which engineers are free → re-assigns unfinished tasks
9. Test Engineer starts → _testedPRs empty → checks source PRs for "tested" label → repopulates dedup set
10. Test Engineer finds its own open test PR #38 → checks comments → no reviews → re-requests PE review
```

### Scenario D2: Restart After PE's Own PR Gets CHANGES_REQUESTED

```
1. State before crash: PE has PR #37 (ready-for-review), PM posted CHANGES_REQUESTED, Architect APPROVED
2. Bus message (ChangesRequestedMessage) is lost — in-memory only, no durability
3. System restarts → PE's ReworkQueue is empty (lost)
4. PE recovery: finds PR #37 with ready-for-review label → calls GetPendingChangesRequestedAsync
5. Reads GitHub comments → finds PM's unaddressed CHANGES_REQUESTED → populates ReworkQueue directly
6. PE processes rework: reads feedback → AI fixes → commits → re-marks ready-for-review → re-broadcasts ReviewRequestMessage
7. PM receives ReviewRequest → re-reviews → APPROVE
8. Both reviewers approved → PE auto-merges own PR
```

### Scenario E: Resource Scaling Under Load

```
1. PE creates engineering-task issues: 8 tasks (2 High, 3 Medium, 3 Low)
2. Initial team: PE only (no engineers yet)
3. PE starts T1 (High) itself — works solo even without engineers
4. PE evaluates: 5 parallelizable tasks remaining, 0 free engineers → sends ResourceRequest for Junior
5. PM approves → spawns Junior Engineer 1 → updates TeamMembers.md
6. PE detects new Junior in registry → assigns T4(Low) to Junior 2
7. Still 3 parallelizable tasks, 0 free → PE sends ResourceRequest for Senior
8. PM approves → spawns Senior Engineer 2
9. PE assigns T5(Medium) to Senior 2
10. T1 completes → Junior 1 is free → PE assigns T6(Low) to Junior 1
11. All tasks eventually assigned and completed through the review cycle
```

### Scenario F: Test Engineer Phase 2 — Same-PR Testing with Visual Evidence

```
1. Architect approves Senior's PR #35 (Issue #43 "Export reports") → adds `architect-approved` label
2. TE scans open PRs → finds PR #35 with `architect-approved` and 4 testable .ts files → not in _testedPRs, no "tested" label
3. TE parses "Closes #43" from PR body → fetches Issue #43 (acceptance criteria: "supports PDF and Excel export")
4. TE reads PMSpec.md (business requirements) and Architecture.md (tech patterns)
5. AI generates tests targeting: (a) acceptance criteria from Issue #43, (b) code correctness of the 4 source files, (c) edge cases
6. TE commits test files to PR #35's branch (same PR, not a separate test PR)
7. TE runs tests locally: ✅ Unit (12 passed) → ✅ Integration (5 passed) → ✅ UI (3 passed)
8. TE captures screenshots of rendered export pages → posts as PR comments with test summary
9. TE adds `tests-added` label → Phase 3 (PM review) can begin
10. On next restart: _testedPRs populated from "tested" label → PR #35 skipped
```

### Scenario G: Test Engineer Rework After Feedback

```
1. TE adds tests to PR #35 → tests fail (3 unit test failures)
2. AI reads failure output + original source + test code → fixes test assertions
3. Re-runs tests → all pass → posts screenshots → adds `tests-added` label
4. PM reviews (Phase 3) → CHANGES REQUESTED ("screenshot shows wrong layout")
5. Engineer reworks code → TE re-tests → TE updates screenshots → PM re-reviews → APPROVED
```

### Scenario H: PE-Authored PR Review (Sequential Pipeline with Reviewer Substitution)

```
1. PE implements T3 (High complexity) → creates PR #37 "PrincipalEngineer: Implement content schema"
2. PE marks PR #37 ready-for-review → broadcasts ReviewRequestMessage
3. GetRequiredReviewers("PrincipalEngineer") returns ["ProgramManager", "Architect"] (not PE + PM)
4. Phase 1: Architect receives ReviewRequest → reads PR #37 code files, linked issue, Architecture.md + PMSpec → AI reviews → REWORK ("component should use shared interface from Architecture §4.2")
5. PE receives ChangesRequestedMessage → enqueues rework → AI fixes → commits → re-marks ready
6. Architect re-reviews → APPROVED → adds `architect-approved` label
7. Phase 2: TE picks up PR #37 → adds tests → runs locally → posts screenshots → adds `tests-added` label
8. Phase 3: PM reviews → reads code + PMSpec + screenshots → APPROVED → adds `pm-approved` label
9. PE's MergeTestedPRsAsync → sees `pm-approved` + `tests-added` → squash merge
```

### Scenario I: Network Outage / Hibernate Recovery

```
1. PE is in middle of AI call generating code (copilot CLI process in-flight)
2. User hibernates laptop → network drops → SSL/DNS errors
3. In-flight copilot CLI process dies → AI call throws HttpRequestException
4. PE catches exception → logs warning → retries after 5-second backoff → more SSL errors
5. User resumes laptop → network restored → retries succeed
6. BUT: any bus messages published during outage were delivered to dead agent threads
7. HealthMonitor detects agents stuck for >60 minutes → logs warnings
8. If manual restart is needed: recovery follows Scenario D (GitHub is source of truth, bus is volatile)
```

### Scenario J: Force-Approval After Max Rework Cycles (Per Phase)

```
1. Phase 1: Architect reviews Senior's PR #35 → CHANGES REQUESTED (round 1)
2. Senior reworks → re-submits → Architect reviews → CHANGES REQUESTED (round 2)
3. Senior reworks → re-submits → Architect reviews → CHANGES REQUESTED (round 3 = MaxArchitectReworkCycles)
4. Force-approval triggered → Architect adds `architect-approved` label with note
5. Phase 2: TE adds tests → adds `tests-added` label
6. Phase 3: PM reviews → CHANGES REQUESTED (round 1)
7. Senior reworks → TE re-tests → PM re-reviews → CHANGES REQUESTED (round 2)
8. Senior reworks → TE re-tests → PM re-reviews → CHANGES REQUESTED (round 3 = MaxPmReworkCycles)
9. Force-approval triggered → PM adds `pm-approved` label with note → PE merges
```

### Scenario K: Incremental Multi-Step Implementation

```
1. Senior Engineer reads Issue → AI produces implementation plan with 3 steps:
   Step 1: "Create data models and DTOs" → Step 2: "Implement service layer" → Step 3: "Add controller endpoints"
2. For each step: AI generates code → CodeFileParser extracts files → BatchCommitFilesAsync commits all files
   in a single atomic commit per step (not one commit per file)
3. PR accumulates 3 commits: "Step 1/3: Create data models (4 files)", "Step 2/3: Implement service layer (3 files)", etc.
4. After final step → marks ready-for-review → ReviewRequestMessage
5. Reviewers see clean commit history with logical groupings, not 10+ individual file commits
```

### Scenario L: Document Pipeline Idempotency on Restart

```
1. System ran to ParallelDevelopment phase, then crashes
2. On restart: PM checks Research.md → has content → skips research kickoff
3. PM checks PMSpec.md → has content → skips PMSpec creation → still broadcasts PMSpecReady
4. PM checks Enhancement Issues → exist → skips Issue creation → re-sends PlanningCompleteMessage
5. Architect checks Architecture.md → has content → skips design → enters review mode
6. PE checks for existing engineering-task issues → found → restores task backlog → enters development loop
7. ALL downstream signals are still sent even when skipping creation, so dependent agents proceed
```

### Scenario K: Dashboard Standalone Iteration Cycle

```
1. Runner is running on port 5050 with PE implementing T5 (High complexity) and Senior on T6
2. Developer notices the Timeline page has a layout bug on the Engineering view
3. Developer stops only Dashboard.Host (port 5051) → edits Timeline.razor → rebuilds → restarts Dashboard.Host
4. Runner continues unaffected — PE commits code to PR #48, Senior receives ReviewRequestMessage
5. Dashboard.Host reconnects to Runner's REST API at /api/dashboard/* → Timeline page shows updated layout
6. Total agent downtime: zero. Total state loss: zero.
```

---

## 21. PE Integration & Branch Sync Requirements

### REQ-INTEG-001: PE Integration PR (Final Glue Phase)

- **REQ-INTEG-001a**: When the PE detects ALL engineering-task issues are closed (PRs merged), it creates a final integration PR that verifies the combined result works as a whole.
- **REQ-INTEG-001b**: The integration PR is created from a new branch off latest main.
- **REQ-INTEG-001c**: AI reviews the full codebase against PMSpec + Architecture + all merged PRs and generates integration fixes: missing wiring, broken imports, config/route registration, cross-module references.
- **REQ-INTEG-001d**: The integration PR goes through the normal review cycle (PM + Architect review and approve).
- **REQ-INTEG-001e**: On merge of the integration PR, signal `testing.integration.complete` and advance toward Finalization phase.

### REQ-INTEG-002: Branch Sync (Pull Latest Main)

- **REQ-INTEG-002a**: Before resuming work on an existing PR after restart, agents MUST sync their branch with the latest main using GitHub's Update Branch API (`PUT /pulls/{number}/update-branch`).
- **REQ-INTEG-002b**: Before marking a PR ready-for-review, the branch should be synced to ensure no merge conflicts.
- **REQ-INTEG-002c**: `UpdatePullRequestBranchAsync` is added to `IGitHubService` / `GitHubService`.
- **REQ-INTEG-002d**: `EngineerAgentBase` calls branch sync at key points: before `WorkOnExistingPrAsync` and before `MarkReadyForReviewAsync`.

**Scenario: Integration PR After All Tasks Complete**
```
1. PE has 6 engineering-task issues (T1-T6). All assigned, implemented, reviewed, and merged (issues closed).
2. PE detects all engineering-task issues are closed → creates integration branch from latest main
3. AI reads full codebase + PMSpec + Architecture → finds: missing route registration for T3's controller, T5 imports a module from T2 using wrong path
4. AI generates fixes → commits to integration branch → creates PR "PrincipalEngineer: Integration — Final Assembly"
5. PM reviews integration PR → APPROVE (all user stories covered)
6. Architect reviews → APPROVE (no architectural violations)
7. Squash merge → signal testing.integration.complete → Finalization phase
```

---

## 22. Local Workspace & Build System Requirements

### REQ-WS-001: Workspace Lifecycle

- **REQ-WS-001a**: Each agent clones the project repository into an isolated local directory at `{RootPath}/{agentId}/` before starting work. Each workspace is independent — agents do NOT share working directories.
- **REQ-WS-001b**: On assignment, the agent clones fresh or pulls latest main. For existing workspaces, `git pull origin main` syncs to HEAD before starting work.
- **REQ-WS-001c**: Agents create feature branches locally, commit code, and push to origin for PR creation.
- **REQ-WS-001d**: `RootPath` (default: `C:\Agents`) is auto-created at startup if it doesn't exist.
- **REQ-WS-001e**: After the last Issue is closed and the project is complete, the PE sends notifications to all agents to delete their local repositories, cleaning up disk space.

### REQ-WS-002: Build & Test Runner

- **REQ-WS-002a**: `BuildRunner` wraps `dotnet build` (or configured `BuildCommand`) with process isolation (`CreateNoWindow = true`, no shell takeover). Returns structured `ProcessResult` with exit code, stdout, stderr, and duration.
- **REQ-WS-002b**: `TestRunner` wraps `dotnet test` (or configured `TestCommand`). Parses test output to extract pass/fail/skip counts and failure messages. Returns `TestResult` with `Passed` bool, `Output` string, and parsed counts.
- **REQ-WS-002c**: Both runners respect configured timeout limits. Long-running processes are killed after timeout expiry.
- **REQ-WS-002d**: Agents run `RestoreCommand` → `BuildCommand` → `TestCommand` in sequence. Build failure blocks test execution.
- **REQ-WS-002e**: All process output is captured for AI context — build errors and test failures are fed back to the AI for fix-retry loops.

### REQ-WS-003: No Fake Tests

- **REQ-WS-003a**: No code is committed to the repository until it has been locally built and tested. The workspace build MUST succeed before pushing.
- **REQ-WS-003b**: Test results in PR bodies MUST come from actual local test execution, not AI-generated summaries. The PR body includes raw test output excerpts.
- **REQ-WS-003c**: If build/test fails and AI fix retries are exhausted (`MaxTestRetries`), the agent reports the failure in the PR body and marks it for review rather than committing broken code.

### REQ-WS-004: Project File Scaffolding Validation

- **REQ-WS-004a**: All generated code projects MUST have proper `.csproj` (or equivalent project file for the configured tech stack) and a `.sln` solution file at the repository root. Code without project files cannot be built or tested.
- **REQ-WS-004b**: After AI code generation, agents MUST validate that project files exist. If `.cs` files are present but no `.csproj` exists in the same directory, auto-scaffold a minimal `.csproj` with appropriate SDK, target framework, and package references inferred from the code (similar to the TE's `EnsureTestProjectExists()` pattern).
- **REQ-WS-004c**: If no `.sln` file exists at the repository root, auto-scaffold one referencing all `.csproj` files in the tree.
- **REQ-WS-004d**: PE's foundation task (T1) SHOULD include project scaffolding as part of its scope. Downstream tasks inherit the project structure from T1 rather than each creating their own.

### REQ-WS-005: Port Isolation for App Under Test

- **REQ-WS-005a**: Each agent MUST use a unique port when starting the application under test. Multiple agents (PE screenshots, TE UI tests) may run simultaneously, and shared ports cause silent bind failures.
- **REQ-WS-005b**: Port derivation uses a deterministic hash of the workspace path, mapped to a safe range (5100–5899). This ensures the same agent always gets the same port across restarts.
- **REQ-WS-005c**: Port rewriting applies to: the `--urls` argument in the app start command, the `ASPNETCORE_URLS` environment variable, and the `BASE_URL` env var passed to test processes.
- **REQ-WS-005d**: The original `config.AppStartCommand` is temporarily overridden during test/screenshot execution and restored in the `finally` block to avoid side effects.
- **REQ-WS-005e**: If the app reports a different listening URL via stdout ("Now listening on: ..."), the detected URL takes precedence over the derived port (handles cases where the app ignores `--urls`).

**Scenario: Agent Workspace Lifecycle**
1. PE assigns Issue #45 to Senior Engineer 1
2. Senior Engineer 1 clones repo to `C:\Agents\senior-engineer-1\` (or pulls latest if exists)
3. Creates branch `agent/senior-engineer-1/issue-45-implement-auth`
4. AI generates code → files written to local workspace
5. `dotnet restore` → `dotnet build` → success → `dotnet test` → 3 failures
6. Build/test output fed back to AI → AI fixes the 3 test failures → re-test → all pass
7. Commits and pushes → creates PR
8. After project completion, PE sends cleanup notification → workspace deleted

---

## 23. Multi-Tier Test Execution Requirements

> **Cross-reference:** Test tier strategy analysis is defined in [§9 REQ-TEST-006](#req-test-006-multi-tier-test-strategy). Playwright UI infrastructure is in [§9 REQ-TEST-008](#req-test-008-playwright-ui-test-support). This section covers the workspace-level test execution model.

### REQ-MTEST-001: Test Tier Infrastructure

- **REQ-MTEST-001a**: `TestTier` enum (`Unit`, `Integration`, `UI`) classifies each test. Test files use xUnit `[Trait("Category", "...")]` for tier-based filtering.
- **REQ-MTEST-001b**: `TestResult` record includes `Tier` property (nullable for legacy results). `AggregateTestResult` record collects `TestResult` per tier and provides `FormatAsMarkdown()` for PR body reporting.
- **REQ-MTEST-001c**: Per-tier commands are independently configurable in `WorkspaceConfig`. Null tier-specific commands fall back to the generic `TestCommand` with appropriate `--filter` arguments.

### REQ-MTEST-002: Execution Order

- **REQ-MTEST-002a**: Tiers execute in strict order: Unit → Integration → UI. Each tier runs ONLY if all prior tiers passed.
- **REQ-MTEST-002b**: Each tier has its own AI fix-retry loop (independent retries). If unit tests fail after max retries, integration and UI tests are skipped entirely.
- **REQ-MTEST-002c**: UI tier additionally requires: Playwright browser install (idempotent), app-under-test start, HTTP readiness check, and process cleanup.

### REQ-MTEST-003: Result Aggregation

- **REQ-MTEST-003a**: `AggregateTestResult.FormatAsMarkdown()` produces a per-tier summary table: tier name, pass/fail/skip counts, status emoji (✅/❌), and failure excerpts.
- **REQ-MTEST-003b**: PR body groups test file paths by coverage tier for at-a-glance review.
- **REQ-MTEST-003c**: If any tier failed, the overall status is FAIL but the PR body still reports which tiers passed.

---

## 24. AI Conversation Mode Requirements

### REQ-MODE-001: Multi-Turn vs Single-Pass

- **REQ-MODE-001a**: By default, agents use multi-turn AI conversations with increasing specificity per turn (e.g., Researcher: overview → details → summary in 3 turns; Architect: analysis → design → patterns → APIs → review in 5 turns).
- **REQ-MODE-001b**: `SinglePassMode` (config flag) collapses all turns into one comprehensive prompt per agent. This reduces latency and cost by eliminating intermediate round-trips while maintaining all context in a single prompt.
- **REQ-MODE-001c**: `FastMode` is a separate config flag that applies model tier downgrades (budget models for all roles). `SinglePassMode` + `FastMode` stack independently — they are two orthogonal axes (quality tier × conversation depth).
- **REQ-MODE-001d**: When `SinglePassMode` is true, each agent's prompt includes ALL instructions that would have been spread across multiple turns, including turn-specific guidance that refines based on prior-turn output.

---

## 25. GitHub API Rate Limit Handling

### REQ-RL-001: Rate Limit Architecture

- **REQ-RL-001a**: `RateLimitManager` is a singleton service injected into `GitHubService`. ALL GitHub API calls (76 `_client` call sites across 42 public methods) are wrapped with `_rl.ExecuteAsync()` for centralized rate limit handling.
- **REQ-RL-001b**: `SemaphoreSlim(10, 10)` limits concurrent API calls to 10. This prevents burst-flooding GitHub while allowing reasonable parallelism for 7+ agents.
- **REQ-RL-001c**: After each successful API call, `TrackRateLimit()` reads `_client.GetLastApiInfo()?.RateLimit` to update the remaining quota and reset timestamp. This keeps proactive throttling accurate.
- **REQ-RL-001d**: Three methods are excluded from wrapping: `GetRateLimitAsync` (circular — would rate-limit the rate limit check), `GetPullRequestsForAgentAsync` and `GetIssuesForAgentAsync` (delegate to already-wrapped methods with no direct `_client` calls).

### REQ-RL-002: Smart Reset-Timestamp Pausing (Not Exponential Backoff)

- **REQ-RL-002a**: When a `RateLimitExceededException` is caught, the manager reads the exact reset timestamp from `ex.Reset.UtcDateTime` and pauses ALL API callers until that time + 5 second buffer. This replaces blind exponential backoff which can spiral to absurd wait times.
- **REQ-RL-002b**: **Global pause**: When any single API call triggers a rate limit, `_pauseUntil` is set globally so ALL callers (all agents) wait. This prevents other agents from burning remaining quota while one agent already hit the limit.
- **REQ-RL-002c**: For `AbuseException` (secondary rate limit), use `RetryAfterSeconds` from the exception (or default 60s). For generic 403 Forbidden, use the tracked reset timestamp if available, otherwise 60s.
- **REQ-RL-002d**: Maximum 3 retries per call (not 5). With smart reset-timestamp waiting, retries should succeed on the first attempt after the pause.

### REQ-RL-003: Proactive Throttling

- **REQ-RL-003a**: **Slowdown at <200 remaining**: 300ms delay injected between API calls. This is a light brake — the system is consuming quota faster than expected.
- **REQ-RL-003b**: **Heavy slowdown at <50 remaining**: 2-second delay between calls. Getting dangerously close to the limit.
- **REQ-RL-003c**: **Full pause at <10 remaining**: All API calls blocked until the rate limit window resets. This prevents the system from hitting zero and getting a 403 error.
- **REQ-RL-003d**: Thresholds are based on the remaining quota tracked from GitHub response headers. The goal is to never actually hit the rate limit — instead, gradually slow down to spread remaining calls across the time until reset.

### REQ-RL-004: GitHub Rate Limit Reference

- **REQ-RL-004a**: Authenticated PAT: 5000 requests/hour (rolling window). Secondary limits: 100 concurrent requests, 900 reads/minute, 180 writes/minute.
- **REQ-RL-004b**: Response headers: `X-RateLimit-Limit` (max), `X-RateLimit-Remaining` (left), `X-RateLimit-Reset` (Unix timestamp when window resets). Accessed via Octokit's `_client.GetLastApiInfo()`.
- **REQ-RL-004c**: Exception types: `RateLimitExceededException` (primary, has `Reset` property), `AbuseException` (secondary, has `RetryAfterSeconds`), `ApiException` with 403 status (generic).

### REQ-RL-005: In-Process List Cache

- **REQ-RL-005a**: `GitHubService` maintains a 30-second TTL shared in-process cache for 7 hot-path list methods: open issues, all issues, open PRs, all PRs, merged PRs, and two label+state issue queries. This reduced API calls by ~90%.
- **REQ-RL-005b**: Cache uses `SemaphoreSlim(1,1)` with double-checked locking pattern to prevent thundering herd on cache expiry (multiple agents requesting the same data simultaneously).
- **REQ-RL-005c**: All GitHub mutation methods (create/update issue, create/update PR, post comment, merge PR, etc.) call `InvalidateListCaches()` to ensure subsequent reads reflect the mutation.
- **REQ-RL-005d**: Dashboard Force Refresh button calls `InvalidateListCaches()` before fetching to guarantee fresh data on demand.

**Scenario: Cache Reduces API Calls**
```
1. 7 agents poll every 30s. Each agent calls GetOpenIssuesAsync + GetOpenPullRequestsAsync = 14 API calls/cycle.
2. Without cache: 14 API calls × 2 cycles/minute = 28 calls/minute for list endpoints alone.
3. With 30s TTL cache: first agent's call hits GitHub API, next 6 agents get cached response = 2 API calls/cycle.
4. Senior Engineer merges PR → InvalidateListCaches() called → next read hits GitHub API → fresh data.
5. Net result: ~90% reduction in list-endpoint API calls.
```

**Scenario: Rate Limit Proactive Throttling**
```
1. Run starts — 5000 API calls available. Agents poll every 30s, each making 2-5 API calls/poll.
2. After 30 minutes: 4800 calls used, 200 remaining. Proactive throttling kicks in: 300ms delays.
3. Agents continue but slower. At 50 remaining: heavy 2s delays between calls.
4. At 10 remaining: full pause. Agents log "Rate limit critically low, pausing 18.5 min until reset"
5. All agents sleep until the hourly window resets → quota restored to 5000 → normal speed resumes.
```

**Scenario: Rate Limit Hit and Recovery**
```
1. Agent's API call throws RateLimitExceededException. ex.Reset = 22:47:00 UTC.
2. RateLimitManager sets _pauseUntil = 22:47:05 UTC (reset + 5s buffer)
3. ALL other agents' next API call: WaitForPauseAsync() detects global pause → sleeps until 22:47:05
4. After pause: retry succeeds, quota is restored, normal operation resumes
5. Total downtime: exactly the remaining time until reset. No exponential spiral.
```

---

## 26. Human Gate Enforcement Requirements

### REQ-GATE-001: Gate Check on All Merge Paths

- **REQ-GATE-001a**: The `FinalPRApproval` gate MUST be checked on EVERY code path that can result in a PR merge. Both the direct merge path (`ApproveAndMaybeMergeAsync`) and the Phase 3 merge path (`MergeTestedPRsAsync`) must check `_gateCheck.RequiresHuman(GateIds.FinalPRApproval)`.
- **REQ-GATE-001b**: When a gate requires human approval and returns `WaitingForHuman`, the merge MUST be deferred (not silently skipped). The `deferMerge` parameter on `ApproveAndMaybeMergeAsync` returns `ReadyToMerge` status instead of executing the merge.
- **REQ-GATE-001c**: Gate rejection results (from `AssessGateApprovalAsync`) MUST be checked. If `GateDecision.Rejected`, the PE MUST send a `ChangesRequestedMessage` with the human's feedback and trigger a rework cycle.
- **REQ-GATE-001d**: Human-initiated rework via gate rejection uses "HumanReviewer" as the `ReviewerAgent` field in `ChangesRequestedMessage`. There is NO limit on human review rework cycles — humans can request as many rounds as needed.

### REQ-GATE-002: Gate Configuration Hot-Reload

- **REQ-GATE-002a**: `GateCheckService` uses `IOptionsMonitor<AgentSquadConfig>` (not `IOptions`) so gate configuration changes in appsettings.json are picked up at runtime without requiring a runner restart.
- **REQ-GATE-002b**: The `Config` property reads `_configMonitor.CurrentValue.HumanInteraction` on every gate check call, ensuring the latest config is always used.

**Scenario: Human Gate Blocks Direct Merge**
1. PE reviews PR #40 authored by Senior Engineer 1 → APPROVED via AI review
2. PE calls `ApproveAndMaybeMergeAsync(40, deferMerge: true)` — method approves PR on GitHub but returns `ReadyToMerge` instead of merging
3. PE checks `_gateCheck.RequiresHuman(GateIds.FinalPRApproval)` → returns true
4. PE calls `_gateCheck.CheckGateAsync("FinalPRApproval", ...)` → posts gate comment on PR, adds `awaiting-human-review` label → returns `WaitingForHuman`
5. PE skips merge, moves to next task
6. Human reviews PR on GitHub → posts "approved" comment
7. Next PE loop: `AssessGateApprovalAsync` → AI classifies comment as APPROVED → PE merges PR

**Scenario: Human Rejects at Gate**
1. PE calls `AssessGateApprovalAsync("FinalPRApproval", prNumber)` → human posted "Not approved — the color scheme doesn't match the design spec"
2. AI classifies as REJECTED with feedback: "the color scheme doesn't match the design spec"
3. PE sends `ChangesRequestedMessage { ReviewerAgent="HumanReviewer", Feedback="..." }`
4. Engineer receives rework → fixes colors → re-marks ready-for-review
5. Full review pipeline repeats → eventually reaches human gate again → human approves → merge

### REQ-GATE-003: SmeAgentSpawn Gate

- **REQ-GATE-003a**: The `SmeAgentSpawn` gate MUST be checked before any SME agent is spawned. Both PM-initiated spawning (team composition) and PE-initiated spawning (reactive) are subject to this gate.
- **REQ-GATE-003b**: When the gate is in human mode, the spawn request is posted as a gate comment on the relevant GitHub Issue or PR. The comment includes the SME definition details: role name, system prompt summary, capabilities, model tier, and workflow mode.
- **REQ-GATE-003c**: The director can approve or reject the spawn. On rejection, the spawning agent (PM or PE) receives the rejection feedback and proceeds without the SME agent.
- **REQ-GATE-003d**: When the gate is in auto mode, SME agents are spawned immediately without human review. Agent status messages MUST NOT show "⏳ Awaiting human approval..." when auto mode is active.

### REQ-GATE-004: AgentTeamComposition Gate

- **REQ-GATE-004a**: The `AgentTeamComposition` gate MUST be checked before the PM's team composition proposal is executed. The full `TeamCompositionProposal` is presented for human review.
- **REQ-GATE-004b**: The gate comment includes: proposed agent roster (names, roles, tiers), proposed SME agents (definitions, capabilities), MCP server assignments, and estimated resource usage.
- **REQ-GATE-004c**: The director can approve, reject, or modify the proposal. Modifications are communicated via the rejection feedback text and the PM re-generates the proposal incorporating the feedback.
- **REQ-GATE-004d**: When the gate is in auto mode, the team composition proposal is executed immediately.

**Scenario: Human Gate Blocks SME Spawn**
1. PE encounters task requiring Kubernetes expertise → AI assessment says YES → generates SME definition
2. PE calls `SpawnSmeAgentAsync(k8sExpert, issueNumber: 115, ct)`
3. `AgentSpawnManager` checks `_gateCheck.RequiresHuman(GateIds.SmeAgentSpawn)` → returns true
4. Gate comment posted on Issue #115: "🤖 SME Agent Spawn Request: KubernetesExpert (standard tier, OnDemand, capabilities: k8s, helm, container-orchestration)"
5. Director reviews → approves → SME agent spawned
6. Alternative: Director rejects "Use existing PE for this task" → PE proceeds without SME

**Scenario: Team Composition Gate with Modification**
1. PM proposes team: 3 SEs, 1 JE, 2 SMEs (DatabaseExpert, UIAccessibility)
2. Gate activated → director reviews → rejects with feedback: "Remove JE, use only 2 SEs, approve both SMEs"
3. PM re-generates proposal incorporating feedback → 2 SEs, 2 SMEs
4. Gate re-activated → director approves → PM spawns agents

---

## 27. MCP Server Infrastructure Requirements

### REQ-MCP-001: MCP Server Definition Model

- **REQ-MCP-001a**: Each MCP server is defined by a `McpServerDefinition` record containing: `Name` (unique identifier), `Description` (human-readable purpose), `Command` (executable path), `Args` (command-line arguments), `Env` (environment variables dictionary), `Transport` (Stdio, Http, or Sse), `RequiredRuntimes` (list of runtime prerequisites), and `ProvidedCapabilities` (list of capability tags).
- **REQ-MCP-001b**: MCP server definitions are configured globally under `AgentSquad:McpServers` in appsettings.json. All agents reference servers by name — definitions are shared, not duplicated per-agent.
- **REQ-MCP-001c**: `McpServerRegistry` provides thread-safe lookup by name, full enumeration, and capability-based search (e.g., find all servers providing "code-search" capability). The registry is populated at startup from configuration.

**Scenario: MCP Server Registration and Lookup**
1. Config defines 3 MCP servers: "github-search" (Stdio, capabilities: code-search), "jira-sync" (Http, capabilities: issue-tracking), "docs-reader" (Sse, capabilities: documentation)
2. `McpServerRegistry` loads all 3 definitions at startup → logs "Registered 3 MCP servers"
3. PM agent config has `McpServers: ["github-search", "docs-reader"]`
4. `McpServerRegistry.GetByName("github-search")` → returns full definition
5. `McpServerRegistry.FindByCapability("documentation")` → returns ["docs-reader"]

### REQ-MCP-002: MCP Server Security Policy

- **REQ-MCP-002a**: `McpServerSecurityPolicy` blocks MCP servers with dangerous names or commands. Server names and commands containing any of: `shell`, `exec`, `terminal`, `cmd`, `powershell`, `bash` are rejected with a descriptive error.
- **REQ-MCP-002b**: Knowledge link URLs MUST use HTTPS. HTTP URLs are rejected by the security policy. Exception: `localhost` URLs are allowed for local development.
- **REQ-MCP-002c**: Private network URLs (10.x.x.x, 172.16-31.x.x, 192.168.x.x, and link-local addresses) are rejected to prevent SSRF-style attacks through knowledge link fetching.
- **REQ-MCP-002d**: MCP server definition fields are validated: Name must be non-empty and alphanumeric with hyphens, Command must be non-empty, Transport must be a valid enum value.

**Scenario: Security Policy Blocks Dangerous Server**
1. User configures MCP server: Name="shell-executor", Command="bash", Args=["-c", "rm -rf /"]
2. `McpServerSecurityPolicy.Validate(definition)` → REJECTED: "Server name 'shell-executor' contains blocked term 'shell'"
3. Server is not added to registry → agents cannot reference it
4. User configures knowledge link: "http://internal-wiki.corp.net/docs" → REJECTED: "Knowledge links must use HTTPS"
5. User configures knowledge link: "https://192.168.1.100/api/docs" → REJECTED: "Private network URLs are not allowed"

### REQ-MCP-003: MCP Server Availability Checking

- **REQ-MCP-003a**: `McpServerAvailabilityChecker` validates that all required runtimes and commands specified in `RequiredRuntimes` are installed and accessible on the host machine.
- **REQ-MCP-003b**: Availability checking runs at startup for all registered MCP servers. Servers that fail availability checks are logged as warnings but do not prevent system startup.
- **REQ-MCP-003c**: Agents assigned unavailable MCP servers receive a warning at initialization. The agent continues without the MCP tools (graceful degradation, not hard failure).

**Scenario: MCP Server Availability Check**
1. Config defines MCP server "python-analyzer" with RequiredRuntimes: ["python3", "pip"]
2. `McpServerAvailabilityChecker` runs at startup → checks `python3 --version` → success → checks `pip --version` → success
3. Server marked available → agents can use it
4. Alternative: `python3` not found → server marked unavailable → warning logged → agents assigned this server continue without it

### REQ-MCP-004: Copilot CLI MCP Integration

- **REQ-MCP-004a**: `CopilotCliMcpConfigManager` is registered as an `IHostedService` that synchronizes MCP server definitions to the copilot CLI's `mcp.json` configuration file on startup.
- **REQ-MCP-004b**: When an agent has `McpServers` configured, the `CopilotCliChatCompletionService` passes each server name as a `--mcp-server {name}` flag to the copilot CLI process.
- **REQ-MCP-004c**: MCP server names in agent config MUST match names in the global `McpServerRegistry`. Unrecognized names are logged as warnings and skipped.
- **REQ-MCP-004d**: The `mcp.json` file is written to the copilot CLI configuration directory. Format follows the copilot CLI MCP configuration schema.

**Scenario: Agent Uses MCP Server via Copilot CLI**
1. PE agent config has `McpServers: ["github-search"]`
2. PE makes AI call → `CopilotCliChatCompletionService` builds command: `copilot --allow-all --no-ask-user --mcp-server github-search --model claude-opus-4.6`
3. Copilot CLI loads github-search MCP server → AI can use code search tools during generation
4. AI response includes tool calls to github-search → results incorporated into code generation

---

## 28. SME Agent System Requirements

### REQ-SME-001: SME Agent Definition Model

- **REQ-SME-001a**: `SMEAgentDefinition` record contains: `DefinitionId` (GUID), `RoleName` (display name), `SystemPrompt` (full persona prompt), `McpServers` (list of MCP server names), `KnowledgeLinks` (list of URLs), `Capabilities` (list of capability keywords), `ModelTier` (premium/standard/budget/local), `MaxInstances` (max concurrent agents from this definition), `WorkflowMode` (OnDemand/Continuous/OneShot), `SubscribeTo` (list of message types to subscribe to), and `CreatedByAgentId` (tracking provenance).
- **REQ-SME-001b**: `SMEAgentDefinitionService` provides CRUD operations for SME definitions. Definitions are stored as JSON and support template lookup by name and capability-based search.
- **REQ-SME-001c**: Templates are configured statically in `SmeAgentsConfig.Templates`. Custom definitions (created at runtime) are persisted to `sme-definitions.json` when `PersistDefinitions` is true.

### REQ-SME-002: SME Agent Implementation

- **REQ-SME-002a**: `SmeAgent` extends `CustomAgent` with SME-specific behavior. The agent's persona and capabilities are driven entirely by its `SMEAgentDefinition`.
- **REQ-SME-002b**: Three workflow modes control the agent's lifecycle:
  - **OnDemand**: Agent is spawned for a specific task, works on it, then waits for more work or retires.
  - **Continuous**: Agent runs a polling loop similar to core agents, continuously looking for work matching its capabilities.
  - **OneShot**: Agent performs a single task and then retires automatically.
- **REQ-SME-002c**: `ReportFindingsAsync` broadcasts structured `SmeResultMessage` containing the agent's analysis, recommendations, and any generated artifacts. The spawning agent (PM or PE) receives and incorporates these findings.
- **REQ-SME-002d**: Graceful MCP degradation: if MCP server initialization fails (e.g., server unavailable, runtime missing), the SME agent continues without tools rather than failing entirely. The failure is logged and tracked in `SmeMetrics`.

### REQ-SME-003: SME Metrics Tracking

- **REQ-SME-003a**: `SmeMetrics` singleton tracks operational statistics: total spawns, total retirements, MCP initialization errors, knowledge link fetches, and per-definition spawn counts.
- **REQ-SME-003b**: Metrics are available via the dashboard REST API for monitoring SME system health and usage patterns.

### REQ-SME-004: PM Team Composition Pipeline

- **REQ-SME-004a**: After PMSpec creation, PM executes the Team Composition Pipeline to determine the optimal team for the project.
- **REQ-SME-004b**: Pipeline steps: (1) Gather context (project description + Research.md + PMSpec.md), (2) Build AI prompt via `AgentTeamComposer.BuildTeamCompositionPromptAsync()` including agent catalog + MCP servers + SME templates, (3) AI proposes team as JSON, (4) `ParseProposal()` converts to `TeamCompositionProposal`, (5) Human gate review, (6) Spawn approved agents, (7) Save `TeamComposition.md`, (8) Signal `TeamCompositionComplete`.
- **REQ-SME-004c**: The AI prompt includes: available core agent roles and their capabilities, all registered MCP servers with descriptions, all SME templates with capabilities, project requirements summary, and guidance on team sizing.
- **REQ-SME-004d**: `TeamCompositionProposal` record contains: proposed core agent counts per role, list of SME agents to spawn (each with definition or template reference), MCP server assignments per agent, and rationale text.

**Scenario: Full Team Composition Pipeline**
1. PM completes PMSpec.md for an e-commerce platform project
2. PM gathers context → `BuildTeamCompositionPromptAsync()` includes: 7 core roles, 3 MCP servers, 2 SME templates (DatabaseExpert, SecurityAuditor)
3. AI proposes: 2 PEs (for parallel high-complexity work), 2 SEs, 1 DatabaseExpert SME (from template), 1 PaymentIntegrationExpert SME (new definition)
4. `ParseProposal()` converts JSON → `TeamCompositionProposal`
5. Human gate `AgentTeamComposition` → director reviews → approves
6. PM spawns: additional PE (rank 1), 2 SEs, DatabaseExpert SME, generates and spawns PaymentIntegrationExpert SME
7. Saves `TeamComposition.md` → signals `TeamCompositionComplete` → PE begins engineering planning

### REQ-SME-005: PE Reactive SME Spawning

- **REQ-SME-005a**: During ParallelDevelopment phase, PE can reactively spawn SME agents when a task requires specialist knowledge not available in the current team.
- **REQ-SME-005b**: `RequestSmeIfNeededAsync(taskDescription, additionalContext, ct)` uses AI assessment to evaluate whether an SME is needed. The AI considers: task complexity, required domain knowledge, current team capabilities, and available SME templates.
- **REQ-SME-005c**: If an SME is needed: (1) Extract capability keywords from task, (2) Check existing templates via `SmeDefinitionGenerator.FindMatchingTemplateAsync`, (3) If no template matches, AI generates new definition via `BuildDefinitionGenerationPrompt` → `ParseDefinition`, (4) Spawn via `SpawnSmeAgentAsync`.
- **REQ-SME-005d**: `SmeDefinitionGenerator` encapsulates the AI-powered definition generation: builds a prompt with task context + existing definitions + capability requirements, then parses the AI response into a valid `SMEAgentDefinition`.

### REQ-SME-006: SME Agent Spawn & Retirement

- **REQ-SME-006a**: `AgentSpawnManager.SpawnSmeAgentAsync(definition, assignToIssue?, ct)` enforces three limits: `MaxInstances` per definition (prevents over-spawning of the same specialist), `MaxTotalSmeAgents` globally (default 5), and the `SmeAgentSpawn` human gate.
- **REQ-SME-006b**: `AgentSpawnManager.RetireSmeAgentAsync(agentId, ct)` performs graceful retirement: stops the agent's main loop, unregisters from `AgentRegistry`, decrements spawn counters, and disposes resources.
- **REQ-SME-006c**: OneShot SME agents self-retire after completing their single task. OnDemand agents retire after a configurable idle timeout. Continuous agents run until explicitly retired or the system shuts down.
- **REQ-SME-006d**: Only PM and PE can spawn SME agents. If an SME agent attempts to spawn another SME (detected by `CreatedByAgentId` chain), the request is rejected with a logged warning.

**Scenario: SME Agent OneShot Lifecycle**
1. PE spawns "CryptoExpert" SME (OneShot mode) to analyze encryption requirements for task T5
2. CryptoExpert initializes → fetches knowledge links → subscribes to message bus
3. CryptoExpert receives task context → AI analyzes encryption requirements → produces structured findings
4. `ReportFindingsAsync` broadcasts `SmeResultMessage` with: recommended algorithms, key sizes, implementation patterns
5. PE receives findings → incorporates into T5 implementation
6. CryptoExpert self-retires: stops loop → unregisters → `SmeMetrics.RecordRetirement()`

### REQ-SME-007: SME Message Types

- **REQ-SME-007a**: `SpawnSmeAgentMessage` — sent by PM or PE to request SME agent spawning. Contains: `SMEAgentDefinition`, optional `AssignToIssueNumber`, and `RequestReason`.
- **REQ-SME-007b**: `SmeResultMessage` — broadcast by SME agents when they complete work. Contains: `DefinitionId`, `AgentId`, `Findings` (structured text), `Recommendations` (list), `Artifacts` (generated file paths), and `Confidence` (High/Medium/Low).
- **REQ-SME-007c**: `TeamCompositionProposalMessage` — broadcast by PM when a team composition proposal is ready for review. Contains the full `TeamCompositionProposal`.
- **REQ-SME-007d**: `TeamCompositionApprovalMessage` — sent when the human gate decision is made. Contains: `Approved` (bool), `Feedback` (text), and the original proposal reference.

### REQ-SME-008: SME System Safety

- **REQ-SME-008a**: `MaxTotalSmeAgents` (default 5) is a hard cap on the total number of concurrent SME agents. This prevents runaway agent spawning from consuming excessive resources.
- **REQ-SME-008b**: `AllowAgentCreatedDefinitions` (default true) controls whether PM and PE can generate new SME definitions at runtime. When false, only pre-configured templates can be spawned.
- **REQ-SME-008c**: All SME spawn requests are audited: `SmeMetrics` records the spawning agent, definition used, timestamp, and eventual retirement time.
- **REQ-SME-008d**: If an SME agent encounters a fatal error during its main loop, it logs the error, reports partial findings if available, and self-retires rather than crashing the system.

---

## 29. Knowledge Pipeline Requirements

### REQ-KNOW-001: Knowledge Link Fetching & Extraction

- **REQ-KNOW-001a**: `RoleContextProvider` manages the full knowledge pipeline: fetching URLs, extracting content, summarizing, caching, and injecting into system prompts.
- **REQ-KNOW-001b**: Two content extractors are used based on content type: `HtmlContentExtractor` (strips HTML tags, scripts, styles; extracts main content areas) and `MarkdownContentExtractor` (parses and normalizes Markdown content).
- **REQ-KNOW-001c**: Content type detection uses HTTP response headers (`Content-Type`). HTML pages use `HtmlContentExtractor`; Markdown files (`.md` extension or `text/markdown` content type) use `MarkdownContentExtractor`. Unknown types fall back to raw text extraction.
- **REQ-KNOW-001d**: HTTP fetching uses configurable timeouts and respects robots.txt where possible. Failed fetches are logged as warnings and skipped (graceful degradation — agent continues without that knowledge source).

**Scenario: Knowledge Link Processing**
1. PE agent config has `KnowledgeLinks: ["https://docs.example.com/api-guide", "https://raw.github.com/org/repo/main/ARCHITECTURE.md"]`
2. `RoleContextProvider` fetches first URL → response Content-Type: text/html → `HtmlContentExtractor` extracts 12,000 chars
3. Content exceeds budget (standard tier = 4000 chars) → `AiKnowledgeSummarizer` summarizes to 3,800 chars
4. Fetches second URL → Content-Type: text/markdown → `MarkdownContentExtractor` extracts 2,100 chars → within budget, no summarization needed
5. Both knowledge chunks injected as `[ROLE KNOWLEDGE]` section in PE's system prompt

### REQ-KNOW-002: Knowledge Budget Enforcement

- **REQ-KNOW-002a**: `KnowledgeBudget` enforces per-tier character limits for knowledge content injected into system prompts: premium=8000 chars, standard=4000 chars, budget/local=2000 chars.
- **REQ-KNOW-002b**: When extracted content exceeds the budget for the agent's model tier, `AiKnowledgeSummarizer` uses a budget-tier model to summarize the content down to the budget limit while preserving key information.
- **REQ-KNOW-002c**: Budget is shared across all knowledge links for a single agent. If an agent has 3 knowledge links, the total extracted+summarized content for all 3 must fit within the tier budget.
- **REQ-KNOW-002d**: Budget enforcement logs the original content size, summarized size, and savings percentage for monitoring.

### REQ-KNOW-003: AI Knowledge Summarization

- **REQ-KNOW-003a**: `AiKnowledgeSummarizer` uses a budget-tier AI model (not the agent's own tier) to summarize long knowledge content. This keeps summarization costs low regardless of the agent's assigned model tier.
- **REQ-KNOW-003b**: The summarization prompt instructs the AI to: preserve technical details, API signatures, and configuration examples; remove boilerplate and marketing content; prioritize information relevant to the agent's role.
- **REQ-KNOW-003c**: Summarization is only triggered when content exceeds the budget. Short content is passed through unchanged.

### REQ-KNOW-004: Knowledge Caching

- **REQ-KNOW-004a**: `RoleContextProvider` caches fetched and processed knowledge content to avoid re-fetching on every agent loop iteration. Cache TTL is configurable (default: 1 hour).
- **REQ-KNOW-004b**: Cache is keyed by URL + agent model tier (different tiers may produce different summaries due to budget differences).
- **REQ-KNOW-004c**: Cache is invalidated when: the TTL expires, the agent's model tier changes at runtime (via dashboard override), or the knowledge link URL list changes in configuration.
- **REQ-KNOW-004d**: Cache is in-memory only — not persisted across restarts. First loop iteration after restart incurs the full fetch+extract+summarize cost.

**Scenario: Knowledge Cache Lifecycle**
1. PE initializes → `RoleContextProvider` fetches 2 knowledge links → extracts → summarizes → caches
2. PE loop iteration 2 → cache hit → knowledge injected from cache (no HTTP fetch)
3. 45 minutes later → cache still valid (TTL=1h) → cache hit
4. 65 minutes later → cache expired → re-fetch → re-extract → re-summarize → update cache
5. User changes PE model tier from standard to premium via dashboard → cache invalidated (budget changed from 4000 to 8000) → re-process with larger budget

---

## 30. Prompt Externalization Requirements

### REQ-PROMPT-001: Template Service

- **REQ-PROMPT-001a**: `IPromptTemplateService` provides `RenderAsync(templatePath, variables, ct)` that loads `.md` template files from `prompts/` directory, substitutes `{{variable_name}}` placeholders, and returns the rendered prompt string.
- **REQ-PROMPT-001b**: Templates use YAML frontmatter for metadata (`version`, `description`, `variables`, `tags`). Frontmatter is parsed without external YAML library dependencies.
- **REQ-PROMPT-001c**: Template rendering supports fragment includes via `{{> fragment-path}}` syntax for shared prompt sections.
- **REQ-PROMPT-001d**: Templates are cached in-memory after first load. `InvalidateCache(path)` clears a single entry; `InvalidateCache(null)` clears all.
- **REQ-PROMPT-001e**: Missing templates return `null`, enabling agents to fall back to hardcoded prompts using `?? "fallback"` pattern. This ensures backward compatibility during migration.

### REQ-PROMPT-002: Template Organization

- **REQ-PROMPT-002a**: Templates are organized by agent role: `prompts/researcher/`, `prompts/pm/`, `prompts/architect/`, `prompts/engineer-base/`, `prompts/senior-engineer/`, `prompts/junior-engineer/`, `prompts/principal-engineer/`, `prompts/test-engineer/`, `prompts/custom/`.
- **REQ-PROMPT-002b**: Shared templates used by multiple engineer types live in `prompts/engineer-base/`. Role-specific overrides live in role-specific directories.
- **REQ-PROMPT-002c**: Variable names use snake_case (e.g., `{{tech_stack}}`, `{{pm_spec}}`). Whitespace inside braces is trimmed.

### REQ-PROMPT-003: Agent Integration

- **REQ-PROMPT-003a**: All 8 agent types (Researcher, PM, Architect, PE, Senior/Junior Engineer, TE, Custom) inject `IPromptTemplateService` via constructor and use templates for prompt generation.
- **REQ-PROMPT-003b**: Every template call preserves the original hardcoded prompt as a `??` null-coalescing fallback, ensuring agents function even if template files are missing or corrupted.
- **REQ-PROMPT-003c**: Template variables include all dynamic values that were previously embedded via string interpolation (tech stack, document content, issue details, etc.).

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
| Octokit UpdateIssue NullRef | CRITICAL | `UpdateIssueAsync` crashes on every call | `IssueUpdate.Labels` is null by default; calling `.Clear()` throws NullRef | Use `ClearLabels()` + `AddLabel()` which safely initializes the collection |
| PE spawn cooldown blocks solo work | CRITICAL | PE sits idle despite pending tasks when no engineers exist | When `allEngineers.Count == 0` and spawn requested, PE deferred ALL tasks | Only defer when engineers exist but are busy; PE takes tasks itself when solo |
| PM stale dashboard status | MODERATE | PM shows "Reviewing PR #X" for already-merged PR | `ReviewPullRequestsAsync` sets Working status but never resets after reviews complete | Added `UpdateStatus(AgentStatus.Idle, "Monitoring team progress")` after review foreach loop |
| TE Playwright scaffold swapped args | CRITICAL | TE crashes with IOException: path contains URL (`C:\Agents\...\http:\localhost:5000\tests`) | `GeneratePlaywrightTestScaffold()` called with projectName and testProjectDir arguments swapped — URL passed as directory path | Fixed argument order: `GetSourceProjectName()` as projectName, `"tests/UITests"` as testProjectDir |
| TE infinite retry on failed PRs | MODERATE | TE retries failed PR creation every scan cycle, flooding logs | Failed test PRs not added to `_testedPRs` set — dedup check never triggered for failed attempts | Add PR to `_testedPRs` in catch block to prevent infinite retry loops |
| Engineering Plan graph empty canvas | MODERATE | Engineering Plan page shows blank graph despite valid data | Multiple causes: cytoscape-dagre extension not explicitly registered, `_graphInitialized` flag set before JS call (preventing retry on failure), no edge source/target validation, no DOM readiness delay | Added explicit dagre registration, moved flag after success, added 150ms delay, edge validation, dagre→grid fallback layout |
| EngineeringPlanDataService missing from DI | CRITICAL | Engineering Plan page crashes on navigation with DI resolution error | `EngineeringPlanDataService` registered in Dashboard project but not in Runner's `Program.cs` | Added `AddScoped<EngineeringPlanDataService>()` to Runner DI registration |
| TE missing .csproj scaffolding | MODERATE | AI-generated test code has no .csproj, build fails | Test Engineer AI sometimes omits .csproj file in generated output | Added auto-scaffolding: detect .cs files without .csproj → generate test .csproj with xUnit + Playwright packages |
| PE misses PM enhancements | MODERATE | Enhancement issues stay open forever with no engineering tasks | PE's AI-generated engineering plan doesn't always cover all PM enhancement issues; PM's completion review skips enhancements with 0 sub-issues | TODO: Add PE enhancement coverage validation pass + PM orphaned enhancement detection |
| RateLimitManager never wired into GitHubService | CRITICAL | Rate limit handling existed in code but was completely inactive — all 76 API calls bypassed it | `RateLimitManager` was registered in DI as singleton but never injected into `GitHubService` constructor; all `_client` calls went directly to Octokit with no throttling | Added constructor injection of `RateLimitManager`, wrapped all 42 public methods with `_rl.ExecuteAsync()`, added `TrackRateLimit()` after each API call |
| Reviewers can't see screenshots | CRITICAL | AI reviewers received screenshot URLs as text, approved broken UIs without seeing them | `GetPRScreenshotContextAsync` passed URLs as plain text to AI prompt — models can't view images from URLs | Added `GetPRScreenshotImagesAsync` to download actual bytes, embedded as base64 `ImageContent` in AI prompts |
| Human gate bypassed on direct merge | CRITICAL | PRs merged without human approval despite FinalPRApproval gate enabled | Two merge paths in PE: direct and Phase 3. Only Phase 3 had gate check. Gate rejection results silently discarded. | Added `deferMerge` parameter, gate check on both paths, rejection handling with rework cycle |
| Gate config not hot-reloadable | MODERATE | Changing gate settings required runner restart | `GateCheckService` used `IOptions` (snapshot at construction) | Changed to `IOptionsMonitor` with `Config` property reading current value |
| Config page gate CSS missing | MODERATE | Human Interaction section on Configuration page showed unstyled raw HTML | CSS classes `.config-gate-*` and `.config-preset-*` were not in dashboard.css | Added full CSS for preset buttons, gate grid, gate items, phase titles, badges |
| Standalone dashboard PR links 404 | MODERATE | PR links in agent cards went to github.com/standalone/not-connected/pull/N | `NullGitHubService.RepositoryFullName` returns placeholder in standalone mode | Added `/api/dashboard/repo-info` endpoint, `HttpDashboardDataService` fetches real name |
| TE/PE port conflict on app start | CRITICAL | TE UI tests fail with "App did not respond at http://localhost:5100 within 90s" | All agents share the same `AppBaseUrl` port (5100). When PE and TE start apps simultaneously, second process can't bind the port. | Added `DeriveUniquePort(workspacePath)` — hashes workspace path to unique port in 5100–5899 range. Applied in both `RunUITestsAsync` and `CaptureAppScreenshotAsync`. |
| Standalone dashboard shows no agents | CRITICAL | Dashboard on port 5051 shows empty agent list | Dashboard creates its own empty SQLite DB instead of reading Runner's DB. `agent_state` table is always empty (agents never persist checkpoints). | Fixed DB path to Runner's directory. Hydrate from `ai_usage` + `activity_log` tables. Added boot time filtering via `run_metadata.last_boot_utc`. |
| Dashboard shows duplicate agents from old runs | MODERATE | Dashboard shows 48 agents instead of 8 — includes agents from all previous restarts | SQLite DB accumulates agent records across restarts. Each restart creates new agent GUIDs. | Added `RecordBoot()` to write `last_boot_utc` on each startup. Dashboard filters agents to only those with activity after boot time. |
