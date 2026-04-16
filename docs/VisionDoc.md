# AgentSquad Vision: Human-Agent Hybrid Development Platform

## Executive Summary

AgentSquad today is a fully autonomous multi-agent AI system — 7 specialized agents collaborate through GitHub PRs/Issues to build software end-to-end without human intervention. The next evolution transforms AgentSquad from an **autonomous pipeline** into a **collaborative development platform** where human developers and AI agents work as true teammates, each playing to their strengths.

The core insight: **the future of software development is not humans OR agents — it's humans WITH agents**. Every developer on a team is paired with a virtual agent counterpart that handles 90%+ of the execution, while the human provides judgment, creativity, domain expertise, and strategic direction. This document lays out the complete analysis and implementation plan for this transformation.

---

## 1. The Human-Agent Collaboration Model

### 1.1 Role Pairing Architecture

Each human team member selects a **role** that matches their expertise and is paired with a matching AI agent:

| Human Role | Agent Counterpart | Collaboration Style |
|---|---|---|
| **Director** | Orchestrator Agent | Full-team oversight, all agents report up |
| **Product Manager** | PM Agent | Spec reviews, priority decisions, stakeholder alignment |
| **Architect** | Architect Agent | Design reviews, technology decisions, pattern approval |
| **Software Engineer** | SE Agent | Engineering plan review, task decomposition approval |
| **Software Engineer** | Software Engineer Agent(s) | Code review, PR approval, implementation guidance |
| **Test Engineer** | Test Engineer Agent | Test strategy, coverage decisions, quality gates |
| **UX Developer** | (Future) UX Agent | Design system, accessibility, user experience |

#### Single-Role vs. Director Mode

- **Single-Role Mode**: A human identifies as one role (e.g., "I am the Architect") and is paired with their Architect Agent. They guide and review only that agent's work, while other agents operate autonomously or are supervised by other humans.
- **Director Mode**: A human oversees the entire virtual team. They see all agent activity, can intervene with any agent, and have approval authority over all workflow gates. This mirrors the current Copilot CLI experience where one person guides the whole squad.
- **Hybrid Team**: Multiple humans each take a role. The PM human guides the PM agent, the Architect human guides the Architect agent, etc. The Director coordinates the humans, who each coordinate their agents.

### 1.2 Graduated Autonomy Model

Not every decision needs human approval. The system uses a **5-level autonomy classification**:

| Level | Name | Description | Example |
|---|---|---|---|
| **L0** | Full Autonomy | Agent acts without any human involvement | Formatting, import organization, basic refactoring |
| **L1** | Inform | Agent acts and notifies the human after the fact | Test execution, build results, status updates |
| **L2** | Advise | Agent proposes an action, proceeds unless human objects within a timeout | PR merge after all checks pass, dependency updates |
| **L3** | Approve | Agent pauses and waits for explicit human approval | Architecture decisions, spec sign-off, production deployment |
| **L4** | Collaborate | Agent and human work together interactively | Complex debugging, design exploration, stakeholder meetings |

Each decision point in the workflow has a **default autonomy level** that can be overridden by the human per-project or per-phase. This is the "permission matrix" — the human says "you can decide X on your own (L0), but I need to approve Y (L3)."

---

## 2. Workflow Integration Points (Approval Gates)

### 2.1 Complete Gate Inventory

The AgentSquad workflow has **17 natural integration points** where human review or approval can be inserted. Each maps to a phase in the `WorkflowStateMachine`:

#### Phase: Initialization
| Gate ID | Gate Name | Default Level | What's Reviewed |
|---|---|---|---|
| `G-01` | Project Kickoff | L3 (Approve) | Project description, goals, constraints |
| `G-02` | Agent Team Composition | L2 (Advise) | Which agents to spawn, model tier assignments |

#### Phase: Research
| Gate ID | Gate Name | Default Level | What's Reviewed |
|---|---|---|---|
| `G-03` | Research Findings | L1 (Inform) | Research.md — competitive analysis, technology landscape |
| `G-04` | Research Completeness | L2 (Advise) | Signal: are all research threads complete? |

#### Phase: Architecture
| Gate ID | Gate Name | Default Level | What's Reviewed |
|---|---|---|---|
| `G-05` | PM Specification | L3 (Approve) | PMSpec.md — business requirements, user stories, acceptance criteria |
| `G-06` | Architecture Design | L3 (Approve) | Architecture.md — system design, component diagrams, technology choices |

#### Phase: Engineering Planning
| Gate ID | Gate Name | Default Level | What's Reviewed |
|---|---|---|---|
| `G-07` | Engineering Plan | L3 (Approve) | EngineeringPlan.md — task breakdown, assignments, dependencies |
| `G-08` | Task Assignment | L2 (Advise) | PR creation + engineer assignment for each task |

#### Phase: Parallel Development
| Gate ID | Gate Name | Default Level | What's Reviewed |
|---|---|---|---|
| `G-09` | PR Code Complete | L2 (Advise) | Individual PR ready for review — code, tests, documentation |
| `G-10` | PR Review Approval | L1 (Inform) | Peer review result (approve/request changes) |
| `G-11` | Rework Exhaustion | L3 (Approve) | Agent exhausted max rework cycles — human decides next step |
| `G-12` | Source Bug Escalation | L2 (Advise) | TE found source bugs, escalating to engineer |

#### Phase: Testing
| Gate ID | Gate Name | Default Level | What's Reviewed |
|---|---|---|---|
| `G-13` | Test Results | L1 (Inform) | All test tiers complete — unit, integration, UI |
| `G-14` | Test Screenshots/Videos | L2 (Advise) | Visual test artifacts for UI verification |
| `G-14b` | Human Final PR Approval | L3 (Approve) | Human is last reviewer before merge — after ALL agent reviews + TE tests complete |

#### Phase: Review
| Gate ID | Gate Name | Default Level | What's Reviewed |
|---|---|---|---|
| `G-15` | Final Review | L3 (Approve) | All PRs merged, all tests passing, ready for completion |

#### Phase: Completion
| Gate ID | Gate Name | Default Level | What's Reviewed |
|---|---|---|---|
| `G-16` | Deployment Decision | L3 (Approve) | Ship/no-ship decision |
| `G-17` | Retrospective | L1 (Inform) | Summary of what was built, decisions made, lessons learned |

### 2.2 Gate Configuration UX

Each gate is configurable per-project via a YAML-like configuration in the dashboard:

```yaml
gates:
  G-05:  # PM Specification
    level: L3          # Require approval
    timeout: 4h        # Auto-approve after 4 hours if no response
    notify: [teams, browser, email]
    assignee: "@behumphr"
    fallback: approve   # What happens on timeout: approve | block | escalate
  G-09:  # PR Code Complete
    level: L0          # Full autonomy — agent decides
  G-11:  # Rework Exhaustion
    level: L4          # Collaborate — open interactive session
    notify: [teams, browser]
    assignee: role:engineer  # Notify the human in the engineer role
```

### 2.3 Dynamic Gate Adjustment

Gates can be promoted or demoted at runtime:
- **Auto-promotion**: If an agent's decisions at L0/L1 consistently get overridden by the human, the system automatically suggests promoting that gate to L2/L3.
- **Trust building**: As the human approves consecutive decisions without changes, the system suggests demoting to a lower level ("Your PM agent's specs have been approved 5 times in a row — would you like to switch to Advise mode?").
- **Emergency override**: The Director can instantly promote all gates to L3 (full approval required) with a single "pause all" command.

---

## 3. Communication & Notification System

### 3.1 Multi-Channel Notification Architecture

The notification system supports **5 channels**, configurable per-gate and per-user:

#### Channel: In-App (SignalR)
- **Latency**: <100ms
- **Best for**: Real-time dashboard updates, status changes, progress tracking
- **Implementation**: Extend existing `AgentHub` SignalR hub with `NotifyApprovalRequired`, `NotifyDecisionMade`, `NotifyEscalation` methods
- **UX**: Notification bell icon in dashboard header with badge count; slide-out notification panel

#### Channel: Browser Push
- **Latency**: 1-5 seconds
- **Best for**: Urgent approvals when user is not on the dashboard tab
- **Implementation**: Web Push API via Service Worker in the Blazor app; requires one-time user consent
- **UX**: Native OS notification with action buttons ("Approve" / "Review" / "Dismiss")

#### Channel: Microsoft Teams
- **Latency**: 2-10 seconds
- **Best for**: Team-visible notifications, approvals that need discussion
- **Implementation**: Teams Workflow webhook (Power Automate) with Adaptive Cards; cards include approve/reject buttons that POST back to the AgentSquad API
- **UX**: Rich Adaptive Card with context summary, diff preview, and action buttons

#### Channel: Email
- **Latency**: 30-120 seconds
- **Best for**: Non-urgent notifications, audit trail, offline access
- **Implementation**: SMTP or Microsoft Graph API; HTML email with deep links back to dashboard
- **UX**: Formatted email with summary, links to PR/document, and quick-action links

#### Channel: SMS/Text
- **Latency**: 5-30 seconds
- **Best for**: Critical escalations only (e.g., build broken in production, rework exhausted)
- **Implementation**: Twilio or Azure Communication Services
- **UX**: Short message with deep link; reserved for L3/L4 escalations only

### 3.2 Notification Routing Logic

```
When gate triggered:
  1. Check gate level and assignee
  2. If L0/L1: log only (L1 also sends in-app notification)
  3. If L2: send to configured channels; start timeout timer
  4. If L3: send to configured channels; block workflow until response
  5. If L4: send to configured channels + open interactive session invitation
  
Escalation chain (if no response within timeout):
  1. Re-notify same channels with URGENT flag
  2. After 2x timeout: notify Director (if different from assignee)
  3. After 3x timeout: apply fallback action (approve/block/escalate)
```

### 3.3 Notification Deduplication & Batching

- **Batching window**: Aggregate multiple L1 notifications within a 30-second window into a single digest (e.g., "3 PRs completed, 1 test suite passed")
- **Deduplication**: If the same gate fires multiple times (e.g., agent retries), only notify once per distinct state change
- **Snooze**: Human can snooze notifications for a specific gate or agent for N hours
- **Quiet hours**: Global quiet-hours config; notifications queue and deliver at next active period

---

## 4. The Director Dashboard

### 4.1 Current State

The existing dashboard (`AgentSquad.Dashboard`) is a Blazor Server app with:
- `AgentHub` (SignalR) for real-time updates
- `DashboardDataService` subscribing to `AgentRegistry` events
- Pages: Agent status cards, workflow phase indicator, activity log
- No interactive controls — purely observational

### 4.2 Director Command Center Transformation

The Director tab becomes the central command for the human to orchestrate everything:

#### 4.2.1 Layout (4-Panel Design)

```
┌──────────────────────────────────────────────────────────────┐
│  🎯 DIRECTOR COMMAND CENTER                    [🔔 3] [⚙️]  │
├──────────────────┬───────────────────────────────────────────┤
│                  │                                           │
│  AGENT ROSTER    │   MAIN WORKSPACE                         │
│  ────────────    │   ──────────────                         │
│  🟢 PM           │   [Workflow Pipeline View]               │
│    └ Paired: You │   [Document Review Panel]                │
│  🟢 Architect    │   [PR/Code Review Panel]                 │
│    └ Paired: -   │   [Interactive Chat Panel]               │
│  🟡 SE           │                                           │
│    └ Waiting...  │                                           │
│  🟢 Sr Eng 1     │                                           │
│  🟢 Sr Eng 2     │                                           │
│  🔴 Jr Eng 1     │                                           │
│  🟢 Test Eng     │                                           │
│                  │                                           │
│  PENDING (3)     │                                           │
│  ────────────    │                                           │
│  ⏳ G-05: Spec   │                                           │
│  ⏳ G-06: Arch   │                                           │
│  ⏳ G-11: Rework │                                           │
│                  │                                           │
├──────────────────┴───────────────────────────────────────────┤
│  📋 ACTIVITY LOG & DECISION TRAIL                            │
│  [Filter: All | Decisions | Approvals | Escalations]         │
│  10:15 PM Agent: PMSpec.md ready for review          [L3] ⏳ │
│  10:12 Sr Eng 1: PR #42 code complete               [L1] ✅ │
│  10:08 Architect: Architecture.md updated            [L2] ⏰ │
└──────────────────────────────────────────────────────────────┘
```

#### 4.2.2 Agent Roster Panel (Left Sidebar)
- **Real-time status** for each agent (idle, working, waiting for approval, error)
- **Pairing indicator**: Which human is paired with this agent (or "autonomous")
- **Quick actions**: Click agent → view current work, pause, resume, reassign
- **Health metrics**: CPU time, token usage, error rate per agent

#### 4.2.3 Main Workspace Panel (Center)
Dynamically shows the relevant content based on what's selected:

- **Workflow Pipeline View**: Visual pipeline of all phases with progress indicators. Click a phase to see details. Gates with pending approvals pulse.
- **Document Review Panel**: Side-by-side view of documents (PMSpec.md, Architecture.md, etc.) with inline commenting and approve/reject buttons. Renders Markdown with syntax highlighting.
- **PR/Code Review Panel**: Embedded diff viewer for PRs. Shows test results, screenshots, videos. Approve/request-changes buttons. Links to GitHub for full context.
- **Interactive Chat Panel**: Copilot CLI-style chat with a specific agent. Type instructions, ask questions, guide decisions. This is the "paired programming" experience — the human can directly instruct their agent counterpart in natural language, just like the Copilot CLI terminal experience.

#### 4.2.4 Activity Log & Decision Trail (Bottom)
- **Chronological log** of all agent activities and decisions
- **Filterable** by: agent, decision level, gate, status (pending/approved/rejected)
- **Decision classification badges**: Each entry shows its autonomy level (L0-L4)
- **Audit trail**: Click any entry to see full context — what the agent decided, why, what data it used
- **Export**: Download as CSV/JSON for compliance reporting

#### 4.2.5 Notification Center (Top-Right Bell Icon)
- **Badge count** of pending approvals
- **Slide-out panel** with grouped notifications
- **Quick actions** directly in the notification: "Approve", "Review", "Snooze"
- **Filter**: By urgency, agent, gate type

### 4.3 Copilot CLI Parity Features

The Director Dashboard should mirror the Copilot CLI experience:

| CLI Feature | Dashboard Equivalent |
|---|---|
| Free-form text instructions | Interactive Chat Panel per agent |
| Seeing agent's thought process | Real-time activity log with reasoning |
| Approving/rejecting proposals | Gate approval buttons with context |
| Asking clarifying questions | Agent can post questions; human responds in chat |
| Viewing file changes | Embedded diff viewer |
| Running commands | Agent status + output panel |
| Multi-turn conversation | Chat history per agent session |

---

## 5. Decision Tracking & Classification System

### 5.1 Decision Registry

Every decision made by any agent is logged to a persistent **Decision Registry**:

```csharp
public sealed record AgentDecision
{
    public required string DecisionId { get; init; }          // Unique ID
    public required string AgentId { get; init; }             // Which agent
    public required string GateId { get; init; }              // Which gate (G-01..G-17)
    public required AutonomyLevel Level { get; init; }        // L0..L4
    public required string Summary { get; init; }             // Human-readable summary
    public required string Rationale { get; init; }           // Agent's reasoning
    public required DecisionStatus Status { get; init; }      // Pending/Approved/Rejected/Auto
    public required DateTime CreatedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedBy { get; init; }                  // Human who resolved, or "auto"
    public string? HumanFeedback { get; init; }               // Comments from human
    public Dictionary<string, string> Context { get; init; }  // Relevant data (PR#, doc path, etc.)
}
```

### 5.2 Decision Dashboard View

A dedicated "Decisions" tab shows:

1. **Pending Decisions** — requiring human action, sorted by urgency
2. **Recent Decisions** — last 50 decisions with status (approved/rejected/auto)
3. **Decision Analytics**:
   - Override rate by agent (how often humans change agent decisions)
   - Average approval latency
   - Decision distribution by level (pie chart: L0/L1/L2/L3/L4)
   - Confidence trend (are agents getting better at autonomous decisions?)

### 5.3 Classification Intelligence

The system learns from human feedback:

- **Override tracking**: When a human rejects an L0/L1 decision, the system flags it as a "miss"
- **Threshold adjustment**: After N misses at a given level, suggest promoting the gate
- **Pattern detection**: If a human consistently approves L3 decisions without changes, suggest demoting to L2
- **Weekly digest**: Email/Teams summary of all decisions made, override rate, and suggested level adjustments

---

## 6. Escalation Workflow

### 6.1 Escalation Triggers

Escalations are triggered when:

1. **Agent exhausts retries** — rework cycles depleted, build/test failures persist
2. **Agent confidence drops** — AI model expresses uncertainty in its output
3. **Conflict between agents** — two agents disagree (e.g., SE rejects Architect's plan)
4. **Gate timeout** — human hasn't responded within the configured timeout
5. **Critical error** — unhandled exception, data loss risk, security concern
6. **Agent requests help** — agent explicitly signals it needs human input (new `HumanHelpRequestMessage` on the bus)

### 6.2 Escalation Flow

```
Agent encounters issue
  │
  ├─ Can it self-resolve? → Yes → Continue (log at L0)
  │
  └─ No → Classify severity
           │
           ├─ Low (L1): Log + notify → Continue
           ├─ Medium (L2): Notify + wait with timeout → Fallback
           ├─ High (L3): Notify + block → Must get human response
           └─ Critical (L4): Notify ALL channels + page Director
```

### 6.3 Non-Blocking Escalation

Critical design principle: **escalations should not block unrelated work**.

- When Agent A escalates at G-11, only Agent A's workflow pauses
- Other agents continue working on their tasks
- The orchestrator tracks which agents are "waiting for human" and redistributes work if possible
- Example: If Sr Eng 1 is waiting for approval on PR #42, Sr Eng 2 can pick up the next task

### 6.4 Human Response Options

When a human receives an escalation, they can:

1. **Approve** — proceed as the agent proposed
2. **Reject with feedback** — provide specific instructions for the agent to retry
3. **Take over** — human does the work themselves (agent watches and learns)
4. **Reassign** — send to a different human or a different agent
5. **Override** — human makes a different decision and the agent proceeds with it
6. **Defer** — push to the next review cycle (with optional timeout)
7. **Escalate up** — send to the Director if the role-paired human can't resolve

---

## 7. Interactive Agent Chat (The Copilot CLI Experience in the Dashboard)

### 7.1 The Vision

The most powerful feature: every agent has an **interactive chat panel** where the human can have a natural-language conversation, just like the Copilot CLI experience. This is the "90% agent, 10% human guidance" interface.

### 7.2 Chat Capabilities

```
Human: "The PMSpec looks good, but I want to add a dark mode toggle to the dashboard."

PM Agent: "I'll add a new user story for dark mode. Here's the updated section:
  
  **US-08: Dark Mode Toggle**
  As a user, I want to switch between light and dark themes...
  
  Acceptance Criteria:
  - Toggle persists across sessions
  - Respects system preference by default
  - All components support both themes
  
  Shall I update PMSpec.md and notify the Architect?"
```

The human types "Yes, and also make sure we add accessibility requirements for contrast ratios." The agent updates the document, commits, and moves on — just like a CLI conversation, but in the browser.

### 7.3 Chat Architecture

```
Human types message in Dashboard Chat Panel
  │
  ├─ Message → SignalR → AgentHub → Targeted agent's message queue
  │
  ├─ Agent receives → Processes with ChatHistory context
  │
  ├─ Agent responds → SignalR → Dashboard Chat Panel
  │
  └─ If agent needs to take action (commit, create PR, etc.)
     → Agent executes normally through existing pipelines
     → Status updates stream to Activity Log
```

The chat panel maintains **per-agent conversation history** (ChatHistory from Semantic Kernel), so context persists across messages. The human can ask "What did you decide about the database schema?" and the agent references prior turns.

### 7.4 Chat vs. Gate Approval

Chat and gates are complementary:
- **Gates** are structured, policy-driven checkpoints — "Do you approve this spec? [Approve] [Reject]"
- **Chat** is unstructured, creative interaction — "I don't like the microservice approach, let's think about a modular monolith instead"

The human can use chat to provide guidance that influences agent behavior *between* gates, not just at gates.

### 7.5 Human Final PR Approval & Review Board (Gate G-14b)

#### The Vision: "Director's Screening Room"

When `HumanFinalApproval` is enabled in configuration, no PR is merged until a human reviews and approves it. This is the **final gate** in the PR lifecycle — after the SE Worker implements, the Architect reviews architecture, the SE Leader reviews code quality, and the Test Engineer runs all tests (unit, integration, UI) and posts results with screenshots and video recordings.

The human "Director" sees the PR only after all agent reviewers have done their best to catch issues, saving the human from having to find problems that automated review would have caught. The human's role shifts from "find bugs" to "validate intent" — does this PR actually deliver what was requested?

#### PR Review Flow with Human Gate

```
SE Worker creates PR
  → Architect reviews (architecture alignment, screenshot analysis)
  → SE Leader reviews (code quality, completeness)
  → SE Worker addresses rework (if changes requested)
  → Architect + SE Leader re-approve
  → TE runs tests (unit, integration, UI), posts results + screenshots + video
  → [NEW] Human Final Approval — PR is held in "awaiting-human-review" state
  → Human approves → PR is merged
  → Human requests changes → Comment posted, SE Worker picks up rework cycle
```

#### The Review Board UI

The Review Board is a dedicated dashboard view designed like a **director reviewing storyboards** at a film studio. Each PR awaiting human approval is presented as a "scene" card with all the context needed for a fast, informed decision:

**Card Layout (per PR):**

| Section | Content |
|---|---|
| **Header** | PR title, linked issue/story, author agent, creation timestamp |
| **Requirements** | Linked issue acceptance criteria + relevant PMSpec section (auto-extracted) |
| **Agent Review Trail** | Chronological feed of all agent reviews — Architect's architecture assessment, SE Leader's code quality notes, each with their APPROVE/CHANGES REQUESTED verdict |
| **Code Changes** | Embedded diff viewer (or link to GitHub diff) showing actual files changed |
| **Screenshots** | Gallery of all screenshots posted by SE (UI preview) and TE (test screenshots), rendered inline for visual comparison against requirements |
| **Video Recordings** | Embedded video player for Playwright test recordings — the human can watch a real walkthrough of the feature being tested. If video analysis becomes available via multimodal models (e.g., Gemini), an AI-generated summary of the video can accompany the player. |
| **Test Results** | Test summary table: passed/failed/skipped counts by tier (unit, integration, UI), with expandable failure details |
| **Decision Controls** | `[Approve & Merge]` `[Request Changes]` `[Skip to Next]` buttons, with optional comment field for change requests |

**Board-Level View:**
- All PRs awaiting human review shown as cards in a kanban-style board
- Sortable by: creation time, complexity, number of agent review cycles, test pass rate
- Filter by: agent author, issue/story, review status
- Badge indicators: 🟢 all tests pass, 🟡 some warnings, 🔴 test failures (should be rare after TE review)
- Quick-approve mode: for high-confidence PRs (all agents approved, all tests pass, screenshots look good), a single-click approve

**Notification Integration:**
- When a PR reaches "awaiting-human-review" state, the notification system (Section 3) sends an alert via configured channels (Teams, email, SMS)
- The notification includes a deep link directly to the PR's Review Board card
- Configurable: batch notifications (e.g., "3 PRs ready for your review") vs. individual alerts

#### Configuration

```json
{
  "AgentSquad": {
    "HumanReview": {
      "Enabled": true,
      "RequireHumanApprovalBeforeMerge": true,
      "AutoMergeIfAllAgentsApprove": false,
      "NotifyChannels": ["teams", "email"],
      "ReviewTimeoutHours": 24,
      "TimeoutAction": "notify-again"
    }
  }
}
```

#### Future: AI-Assisted Video Review

Currently, AI models (Claude, GPT) support image analysis but not direct video processing. When multimodal video understanding becomes available via standard APIs (e.g., Google Gemini already supports this), the system could:
1. Extract the Playwright test recording for each PR
2. Send the video to a multimodal model with the prompt: "Watch this walkthrough of the feature. Compare it to these requirements. Does the UI behave correctly? Are there visual errors, broken layouts, or missing functionality?"
3. Post the AI video analysis as a comment on the PR before the human reviews it
4. The human then sees both the raw video AND the AI's assessment, further accelerating their review

As an intermediate step before native video support, the system could extract key frames from Playwright recordings (using ffmpeg at ~1fps or on scene changes) and send those as a multi-image sequence to Claude/GPT for analysis — achieving ~80% of the value without waiting for native video APIs.

---

## 8. Configuration UX

### 8.1 Project Setup Wizard

When starting a new project, the Director uses a step-by-step wizard:

**Step 1: Project Definition**
```
Project: ReportingDashboard
Repository: azurenerd/ReportingDashboard
Description: "Build an internal reporting dashboard with..."
Tech Stack: [Blazor Server, .NET 8, SQL Server]
```

**Step 2: Team Composition**
```
Humans on this project:
  @behumphr — Director (manages all agents)
  
Agent Team:
  ✅ PM Agent (premium tier)
  ✅ Researcher Agent (standard tier)
  ✅ Architect Agent (premium tier)
  ✅ Software Engineer × 2 (premium tier)
  ✅ Software Engineer × 2 (standard tier)  [spawned on demand]
  ✅ Test Engineer (standard tier)
  ☐ UX Agent (not available yet)
```

**Step 3: Autonomy Configuration**
```
Preset: [Balanced] ← Default
  ├─ ⚙️ Full Auto — All L0 (agent decides everything)
  ├─ ⚙️ Balanced — Critical gates L3, routine L0-L1
  ├─ ⚙️ Supervised — Most gates L2-L3
  └─ ⚙️ Full Control — All gates L3-L4

Or customize per-gate:
  G-01 Project Kickoff:     [L3 ▾] Approve
  G-05 PM Specification:    [L3 ▾] Approve
  G-06 Architecture Design: [L3 ▾] Approve
  G-07 Engineering Plan:    [L2 ▾] Advise
  G-09 PR Code Complete:    [L1 ▾] Inform
  G-11 Rework Exhaustion:   [L3 ▾] Approve
  ...
```

**Step 4: Notification Preferences**
```
Notification Channels:
  ✅ In-App (SignalR)     — Always on
  ✅ Microsoft Teams      — Webhook URL: [________________]
  ☐ Browser Push         — [Request Permission]
  ☐ Email                — Address: [________________]
  ☐ SMS                  — Phone: [________________]

Route by severity:
  L1 (Inform):    In-App only
  L2 (Advise):    In-App + Teams
  L3 (Approve):   In-App + Teams + Browser Push
  L4 (Collaborate): All channels
```

**Step 5: Timeouts & Fallbacks**
```
When human doesn't respond to approval requests:
  Default timeout: [2 hours ▾]
  Fallback action: [Auto-approve ▾] / Block / Escalate to Director
  
  Override per gate:
    G-05 PM Spec:     Timeout 4h, Fallback: Block
    G-11 Rework:      Timeout 30min, Fallback: Auto-approve
```

### 8.2 Runtime Configuration Panel

The Dashboard's Settings page allows live configuration changes:
- Toggle gates on/off without restarting
- Adjust timeouts
- Pause all agents with one click ("Emergency Stop")
- View and modify the full permission matrix
- Import/export configuration as YAML

---

## 9. Technical Implementation Plan

### 9.1 New Core Types

```csharp
// --- Autonomy & Gate System ---

public enum AutonomyLevel { FullAutonomy, Inform, Advise, Approve, Collaborate }

public enum GateStatus { NotReached, Pending, WaitingForHuman, Approved, Rejected, TimedOut, Skipped }

public sealed record GateDefinition
{
    public required string GateId { get; init; }
    public required string Name { get; init; }
    public required ProjectPhase Phase { get; init; }
    public required AutonomyLevel DefaultLevel { get; init; }
    public AutonomyLevel? OverrideLevel { get; init; }
    public TimeSpan? Timeout { get; init; }
    public GateFallbackAction Fallback { get; init; } = GateFallbackAction.Block;
    public List<string> NotifyChannels { get; init; } = [];
    public string? AssignedTo { get; init; } // Role or username
}

public enum GateFallbackAction { Approve, Block, Escalate }

// --- Decision Registry ---

public sealed record AgentDecision
{
    public required string DecisionId { get; init; }
    public required string AgentId { get; init; }
    public required string GateId { get; init; }
    public required AutonomyLevel Level { get; init; }
    public required string Summary { get; init; }
    public required string Rationale { get; init; }
    public required DecisionStatus Status { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedBy { get; init; }
    public string? HumanFeedback { get; init; }
    public Dictionary<string, string> Context { get; init; } = new();
}

public enum DecisionStatus { Pending, Approved, Rejected, AutoApproved, TimedOut, Overridden }

// --- New Message Types ---

public record HumanApprovalRequestMessage : AgentMessage
{
    public required string GateId { get; init; }
    public required string CorrelationId { get; init; }
    public required AutonomyLevel Level { get; init; }
    public string? DocumentPath { get; init; }
    public int? PrNumber { get; init; }
    public string? Context { get; init; }
}

public record HumanApprovalResponseMessage : AgentMessage
{
    public required string CorrelationId { get; init; }
    public required bool IsApproved { get; init; }
    public string? Feedback { get; init; }
    public string? ResolvedBy { get; init; }
}

// --- Notification System ---

public interface INotificationService
{
    Task SendAsync(NotificationPayload payload, CancellationToken ct = default);
}

public sealed record NotificationPayload
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required AutonomyLevel Severity { get; init; }
    public required List<string> Channels { get; init; }  // "signalr", "teams", "push", "email", "sms"
    public string? ActionUrl { get; init; }
    public Dictionary<string, string> Actions { get; init; } = new(); // "approve" → URL, "reject" → URL
}
```

### 9.2 Modified Components

| Component | Changes Required |
|---|---|
| `WorkflowStateMachine` | Add `GateDefinition[]` registry, `EvaluateHumanGate()`, `WaitingForHuman` state per-gate |
| `AgentBase` | Add `RequestHumanApprovalAsync()` helper, `AwaitApprovalAsync()` with timeout |
| `PullRequestWorkflow` | Add `IsHumanApprovalRequired()`, modify merge flow, add `awaiting-human-review` label |
| `InProcessMessageBus` | Add `CorrelationId` support, optional SQLite persistence for approval messages |
| `DashboardDataService` | Subscribe to approval events, maintain pending approval queue |
| `AgentHub` (SignalR) | Add `NotifyApprovalRequired`, `SubmitApprovalResponse`, `SendNotification` |
| `AgentSquadConfig` | Add `HumanInTheLoop` section with full gate configuration |
| `AgentSpawnManager` | Add approval check before spawning new agents (when configured) |
| `AgentRegistry` | Add `HumanPairing` tracking (which human is paired with which agent) |

### 9.3 New Components

| Component | Purpose |
|---|---|
| `GateManager` | Manages gate definitions, evaluates gate conditions, handles timeout/fallback |
| `DecisionRegistry` | Persists all agent decisions, provides query API for dashboard |
| `NotificationService` | Multi-channel notification dispatch (SignalR, Teams, Push, Email, SMS) |
| `TeamsNotificationChannel` | Teams Adaptive Card webhook integration |
| `BrowserPushChannel` | Web Push API + Service Worker registration |
| `EmailNotificationChannel` | SMTP or Microsoft Graph email sender |
| `ApprovalController` | ASP.NET Core API controller for external approval callbacks (Teams buttons, etc.) |
| `InteractiveChatService` | Manages per-agent chat sessions between human and agent |
| `ApprovalQueue.razor` | Dashboard page: pending approvals with context and action buttons |
| `DocumentReview.razor` | Dashboard page: document diff viewer with approval workflow |
| `DecisionTracker.razor` | Dashboard page: decision registry viewer with filters and analytics |
| `DirectorCommandCenter.razor` | Dashboard page: 4-panel layout with full orchestration controls |
| `NotificationPreferences.razor` | Dashboard page: per-user notification channel configuration |

### 9.4 Implementation Phases

#### Phase 1: Foundation (2-3 weeks)
- Core types (AutonomyLevel, GateDefinition, AgentDecision, new messages)
- `HumanInTheLoop` config section
- `GateManager` with definition registry and evaluation
- `DecisionRegistry` with SQLite persistence
- Modify `AgentBase` with `RequestHumanApprovalAsync()`

#### Phase 2: Dashboard UX (2-3 weeks)
- `ApprovalQueue.razor` with SignalR real-time updates
- `DirectorCommandCenter.razor` 4-panel layout
- Notification bell icon + slide-out panel
- Interactive chat panel per agent
- Agent roster with pairing indicators

#### Phase 3: Notification Channels (1-2 weeks)
- `INotificationService` + dispatch logic
- SignalR channel (extend AgentHub)
- Teams webhook with Adaptive Cards
- Browser Push with Service Worker
- Email via SMTP/Graph

#### Phase 4: Workflow Integration (2-3 weeks)
- Modify `WorkflowStateMachine` for human gates
- Modify `PullRequestWorkflow` for approval-gated merges
- Modify `CommitAndMergeDocumentPRAsync` for document review
- Add `awaiting-human-review` label management
- Escalation-before-force-approve in `EngineerAgentBase`

#### Phase 5: Intelligence & Polish (2-3 weeks)
- Trust calibration (auto-suggest level changes)
- Decision analytics dashboard
- SLA tracking and alerting
- Configuration import/export
- Project setup wizard

**Total estimated effort: 10-14 weeks** for a single developer, or **5-7 weeks** with 2 developers working in parallel.

---

## 10. Industry Context & Competitive Landscape

### 10.1 Current Market State

The human-agent hybrid development model is emerging as a major industry trend:

- **Microsoft Copilot Studio** (2025-2026): Multi-stage approvals with AI + human gates, Adaptive Cards for approvals, configurable escalation policies
- **GitHub Copilot Workspace** (2025): Agent-mode coding with human approval at key checkpoints — very close to our single-role paired model
- **CrewAI / AutoGen / LangGraph**: Multi-agent frameworks with role-based orchestration, but focused on task execution, not ongoing team collaboration
- **Temporal / Orkes**: Workflow orchestration with HITL patterns — checkpoints, approval signals, durable execution
- **Devin (Cognition)**: AI developer agent with human oversight, but single-agent, no team model
- **IBM watsonx Orchestrate**: Enterprise agent orchestration with human approval workflows

### 10.2 What Makes AgentSquad Different

AgentSquad's vision is unique in several critical ways:

1. **Team-scale, not single-agent**: Most tools pair one human with one agent. AgentSquad models an *entire development team* with role specialization and inter-agent collaboration.

2. **Role pairing, not just oversight**: The human isn't a supervisor watching agents — they are a *team member* paired with an agent counterpart, contributing expertise at their level.

3. **Graduated autonomy with learning**: The permission matrix isn't static. The system learns from human feedback and suggests autonomy adjustments over time.

4. **GitHub-native**: All artifacts (PRs, Issues, reviews) live in GitHub — the system of record that teams already use. No vendor lock-in to a proprietary platform.

5. **The "Director" pattern**: One human can orchestrate an entire virtual dev team, scaling their impact by 10x+ while maintaining quality through strategic intervention.

### 10.3 Research-Backed Principles

The design incorporates proven patterns from industry research:

- **Calibrated autonomy** (Zapier, 2025): Maximize autonomous actions while minimizing unnecessary escalations
- **Pre-action approval gates** (Temporal, 2025): Pause-and-approve for high-stakes decisions, enforced architecturally
- **Confidence-based escalation** (multiple sources): Dynamic thresholds tuned by real-world performance
- **Post-action audit** (EU AI Act Article 14): Immutable decision logs for compliance and traceability
- **Context-rich handoffs** (Zylos, 2026): Package full agent state for human review — no context loss
- **Policy in code, not prompts** (Cordum, 2025): Deterministic enforcement of approval requirements

---

## 11. Beyond Software Development

### 11.1 Extensible Role Framework

While this vision focuses on software development, the architecture is role-agnostic. The same patterns apply to:

- **Content teams**: Writer Agent + Editor Human, Designer Agent + Art Director Human
- **Data teams**: Data Analyst Agent + Data Scientist Human, Pipeline Agent + ML Engineer Human
- **Operations teams**: SRE Agent + Ops Human, Security Agent + Security Engineer Human
- **Research teams**: Literature Agent + Researcher Human, Analysis Agent + PI Human

The role pairing model, graduated autonomy, and approval gates work universally wherever humans and AI collaborate on knowledge work.

### 11.2 Future Extensions

- **Multi-project orchestration**: One Director managing multiple AgentSquad instances across repos
- **Cross-team dependencies**: Agent teams in different repos coordinating through shared gates
- **Learning across projects**: Autonomy levels and trust scores persist across projects — a well-calibrated agent gets more autonomy on the next project
- **Voice interface**: "Hey AgentSquad, approve the PM spec" via voice command in Teams
- **Mobile companion**: Approval notifications with quick-approve buttons on phone

---

## 12. Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| **Over-notification fatigue** | Human ignores important escalations | Severity-based routing, batching, snooze, auto-silence for L0/L1 |
| **Approval bottleneck** | Workflow blocked waiting for human | Configurable timeouts with fallback actions, parallel work on unrelated tasks |
| **Trust miscalibration** | Agent makes bad decisions autonomously | Conservative defaults (L3 for critical gates), override tracking, weekly review digest |
| **Complexity explosion** | Too many configuration options | Presets (Full Auto / Balanced / Supervised / Full Control), sensible defaults |
| **Context switching cost** | Human interrupted too frequently | Batch notifications, quiet hours, async-first design (L2 with timeout, not L3) |
| **Stale approvals** | Human approves based on outdated state | Show current state + changes since request; invalidate approval if state changed |
| **Security/compliance** | Agent makes unauthorized changes | Immutable audit trail, role-based access, approval gates for production-impacting changes |

---

## 13. Success Metrics

How we measure whether the hybrid model is working:

| Metric | Target | How Measured |
|---|---|---|
| **Developer productivity multiplier** | 5-10x per developer | Story points completed per sprint vs. baseline |
| **Human time per project** | <20% of total effort | Hours of human interaction vs. total agent computation time |
| **Approval latency** | <30 min average for L3 gates | Time from notification to human response |
| **Override rate** | <15% of agent decisions changed by human | Decisions rejected or modified / total decisions |
| **Quality gate pass rate** | >90% PRs pass first human review | PRs approved without changes requested |
| **Escalation resolution time** | <2 hours average | Time from escalation to resolution |
| **Trust calibration accuracy** | Autonomy level suggestions accepted >70% | Suggested level changes adopted by human |
| **Notification actionability** | <5% notifications snoozed/ignored | Notifications acted on / notifications sent |

---

## 14. Conclusion

The transformation of AgentSquad from an autonomous pipeline to a human-agent hybrid platform represents a paradigm shift in how software is built. The key insight is not that AI replaces developers — it's that AI *augments* developers by handling 90%+ of the execution while humans provide the judgment, creativity, and strategic direction that AI still cannot match.

This vision is achievable with the existing AgentSquad architecture. The foundations — message bus, workflow state machine, GitHub integration, dashboard, agent roles — are already in place. The additions required are primarily in the **approval gate system**, **notification channels**, **decision tracking**, and **interactive chat** — all of which layer on top of existing infrastructure rather than replacing it.

The phased implementation plan (10-14 weeks) allows incremental delivery of value, with each phase adding meaningful human-agent collaboration capabilities that can be tested and refined before moving to the next.

**The future of software development is collaborative. AgentSquad is how we get there.**

---

*Document version: 1.0*
*Author: AgentSquad Team*
*Last updated: April 2026*