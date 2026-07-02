# Final Quality Improvement Plan: Vision-Validation for Multi-Agent App Generation

> **Goal:** Make VirtualDevTeam (VDT) deliver apps that actually work for the user's stated vision — generically across any app type (game, SaaS, CLI, mobile, dashboard) — without adding ceremony, redundant agents, or false-positive-prone gates.
>
> **Source:** Synthesized from 10 parallel agent perspectives (QA, PM, Game-Designer, End-User-Simulator, SRE, Software-Architect, Stub-Detector, Customer-Support, PM-Milestone, Devil's-Advocate) plus 2 rubber-duck passes — including a second pass on the operator-proposed **Scenarios** mechanism.

---

## 1. Executive Summary

VDT shipped a tower-defense game (GridGuardians) that:
- Compiled cleanly
- Passed all eight agent approval gates
- Was scored 9/9/9 by the CLI-native judge
- Was declared "Final Integration & Validation" complete by T-FINAL

When a human played it: empty playfield, Wave 0/100 forever, no enemies, no gameplay. A **12-line hotfix** made it playable. The agents had the same tools as the human who fixed it in 12 minutes. **The gap is not capability — it is what T-FINAL was instructed to verify.** T-FINAL verified the code merged, not that the app worked.

This plan proposes a **5-layer architecture** to close this gap. The new **Scenarios** mechanism (Layer 0) is the unifying primitive — a first-class artifact of approved user journeys that threads from the wizard, through PMSpec / Architecture / Tasks / PRs, and finally into T-FINAL's scenario-by-scenario behavioral verification. Every other layer references the same Scenarios.

---

## 2. Root Cause Analysis (synthesized from 10 perspectives)

Five contributing factors, ordered by importance:

### 2.1 The original project description never reaches T-FINAL
The user wrote *"a tower defense game"*. By the time T-FINAL runs, the description has been translated through PMSpec → Architecture → Tasks → PRs. T-FINAL's `integration-review-user.md` receives `pm_spec` + `architecture` + `task_summary` but **NOT** the original `{{project_description}}`. The only agent whose job is holistic never sees the user's own words. *(Customer Support's "if you only do one thing.")*

### 2.2 Validation measures artifact existence, not behavior
Every gate in `WorkflowStateMachine.cs` measures agent activity ("did the agent emit signal X"), not user outcomes ("does the running app do Y"). The `Review → Completion` transition's `_ => new List<GateCondition>()` is **literally empty**. *(PM Milestone's "if you only do one thing.")*

### 2.3 Cross-feature integration has no owner
Each SE worker owned one feature in isolation. The bug lived in the **joint between features** — a space no single agent was assigned to own. T-FINAL was supposed to fill this gap but its prompt asks for code review, not behavioral integration verification. *(Software Architect + Devil's Advocate convergence.)*

### 2.4 Screenshots ≠ behavior
The strategy framework captured Playwright screenshots showing the HUD. Screenshots prove the app *renders*. They cannot prove it *responds*. Bug #1 (init race) leaves no visual trace on frame 1 — the canvas renders, the wave just never starts. *(End-User-Sim + QA + Game Designer.)*

### 2.5 Stubs are invisible to type checkers, build systems, and screenshot judges
`function register(_scene: any): void { /* TODO */ }` compiles, exports cleanly, runs without error. Every existing gate passes it. The body is hollow. *(Stub Detector.)*

---

## 3. The Scenarios Mechanism (NEW — Layer 0, operator-proposed)

### 3.1 Core idea

A **Scenario** is a structured object describing one end-to-end journey through the system — a user clicking through a UI, an API caller hitting a REST endpoint, a scheduled job firing, a webhook arriving, a CLI invocation, or any other initiating action with an observable terminal state. **Scenarios apply universally to front-end and back-end work alike.**

Each scenario has the following schema (single source of truth — embedded in PMSpec.md as a YAML block, machine-extractable):

```yaml
id: S03                                # Stable identifier (S01, S02, ...)
title: "Player builds first tower"     # Short human-readable
journey_kind: ui_interaction           # ui_interaction | api_call | scheduled_job
                                       # | event_arrival | webhook | message_consume
                                       # | cli_invocation | system_initiated | data_pipeline
actor: "Player"                        # Who/what initiates. Examples:
                                       #   "Player", "End-user", "Admin",
                                       #   "API caller (authenticated)",
                                       #   "Scheduler (cron 02:00)",
                                       #   "Stripe webhook", "RabbitMQ consumer",
                                       #   "CLI user", "Internal service A"
trigger: "User clicks 'Build Tower' button after landing on game screen"
preconditions:                         # What must be true before
  - "S01 has completed (game has started)"
  - "Player has ≥ 100 gold"
steps:                                 # Numbered execution path (terse, observable)
  - "1. Player clicks on empty tile in playfield"
  - "2. Tower placement preview appears"
  - "3. Player clicks 'Confirm'"
  - "4. Tower sprite renders at chosen tile"
  - "5. Gold counter decreases by tower cost"
  - "6. Tower begins targeting nearest enemy in range"
expected_terminal_state:               # Success criteria — concrete, observable.
                                       # Where to look depends on journey_kind:
                                       #   ui_interaction: DOM state / canvas state / fired events
                                       #   api_call: HTTP status + response body shape + DB row
                                       #   scheduled_job: log line + DB rows + side-effects
                                       #   webhook: external system ack + DB state + downstream queue
                                       #   message_consume: queue ack + DB state + emitted events
                                       #   cli_invocation: exit code + stdout pattern + file artifact
  - "DOM contains <tower-sprite> at clicked tile coordinates"
  - "Gold counter element shows new value"
  - "EventBus has fired 'tower:placed' event"
observation_surfaces:                  # WHERE the verifier should check evidence
  - kind: dom_query
    selector: ".tower-sprite[data-tile='5,7']"
  - kind: dom_text
    selector: ".hud-gold"
    expected_change: "decreased_by_cost"
  - kind: event_bus
    event_name: "tower:placed"
subsystems_involved:                   # Every system/component that must be alive
  - playfield-renderer
  - tower-placement
  - economy
  - event-bus
  - targeting
priority: critical                     # critical | important | nice-to-have
status: approved                       # proposed | approved | edited | rejected
implementing_tasks:                    # Filled in by SE leader at task-creation time
  - "T03: Tower placement UI"
  - "T07: Tower entity model"
verification_status:                   # Filled in by T-FINAL
  not_yet_verified | verified | broken | inconclusive
verification_evidence_url: null        # Set by T-FINAL — link to playtest artifact
```

**Backend-journey example (illustrates non-UI generality):**

```yaml
id: S08
title: "Stripe webhook marks invoice paid"
journey_kind: webhook
actor: "Stripe webhook (charge.succeeded event)"
trigger: "POST /webhooks/stripe with charge.succeeded payload and valid signature"
preconditions:
  - "Invoice INV-123 exists in 'pending' state"
  - "Customer has stripe_customer_id linked"
steps:
  - "1. Stripe POSTs charge.succeeded payload to /webhooks/stripe"
  - "2. Service validates Stripe-Signature header"
  - "3. Service looks up invoice by charge metadata"
  - "4. Service transitions invoice from 'pending' to 'paid'"
  - "5. Service emits invoice.paid domain event"
  - "6. Service responds 200 OK to Stripe within 5s"
expected_terminal_state:
  - "HTTP response: 200 OK within 5000ms"
  - "DB row: invoices WHERE id='INV-123' has status='paid' and paid_at IS NOT NULL"
  - "Domain event invoice.paid published to the message bus"
  - "Audit log entry recorded with stripe_event_id (idempotency)"
observation_surfaces:
  - kind: http_response
    status: 200
    max_latency_ms: 5000
  - kind: db_row
    query: "SELECT status, paid_at FROM invoices WHERE id='INV-123'"
    expected: { status: "paid", paid_at: "not_null" }
  - kind: queue_message
    topic: "invoice.events"
    event_type: "invoice.paid"
  - kind: log_line
    pattern: "stripe.webhook.processed event=charge.succeeded"
subsystems_involved:
  - webhook-router
  - stripe-signature-validator
  - invoice-repository
  - domain-event-publisher
  - audit-log
priority: critical
status: approved
```

**CLI example (illustrates third-major-kind generality):**

```yaml
id: S04
title: "Operator uploads CSV via CLI"
journey_kind: cli_invocation
actor: "CLI user (admin role)"
trigger: "myapp upload --file=customers.csv --tenant=acme"
steps:
  - "1. CLI parses arguments"
  - "2. CLI authenticates against API using stored credentials"
  - "3. CLI streams file to /uploads endpoint in 1MB chunks"
  - "4. Server validates schema, persists rows"
  - "5. CLI prints progress bar"
  - "6. CLI exits with code 0 and summary line"
expected_terminal_state:
  - "Exit code: 0"
  - "Stdout contains: 'Uploaded N rows successfully'"
  - "DB has N new customer rows under tenant=acme"
observation_surfaces:
  - kind: process_exit_code
    expected: 0
  - kind: stdout_pattern
    regex: "Uploaded \\d+ rows successfully"
  - kind: db_count
    query: "SELECT COUNT(*) FROM customers WHERE tenant='acme'"
    expected_change: "+N"
```

The schema is uniform; only the `journey_kind` + `observation_surfaces` change shape. This generality is what makes Scenarios universal across **front-end UI apps, REST APIs, scheduled jobs, message-driven workers, CLI tools, data pipelines, and webhook integrations.**

### 3.1.1 Where Scenarios live in the artifact tree

**Single source of truth: embedded in PMSpec.md as a YAML block (mirrors the existing `image-deliverables` pattern).** The PM agent writes scenarios INSIDE `PMSpec.md` under a `## Scenarios` section that contains:

1. **Markdown narrative summary** — a short human-readable description of each scenario for browsing operators.
2. **`# scenarios` YAML code block** — the deterministic machine-readable form. Agents split on the marker line `# scenarios` to extract YAML (identical convention to `# image-deliverables`).
3. **Numbered cross-references** — every user story in PMSpec's `## User Stories & Acceptance Criteria` section MUST cite scenario IDs it satisfies, and every scenario MUST be cited by at least one user story (else the scenario is orphaned).

**Optional sidecar `scenarios.json`** — generated at PMSpec-write time by the PM agent (or on read by `ScenarioRegistry`). The JSON is for tools that want JSON over YAML (orchestrator signals, T-FINAL playtester, Dashboard scenario viewer). The PMSpec YAML block is authoritative; the JSON is a deterministic mirror. If they ever drift, `ScenarioRegistry` raises a Critical FlowFinding.

**Why this design (vs sidecar-only):**

- ✅ PMSpec remains the central business artifact operators read — scenarios live where the rest of the spec lives.
- ✅ Mirrors the existing pattern (`image-deliverables`), so agents already know how to extract YAML blocks from PMSpec.
- ✅ Drift between scenarios and user stories is impossible — both are in one document, edited together.
- ✅ Sidecar JSON gives orchestrator and playtester a machine-clean structure without forcing them to parse Markdown.
- ✅ Generic for any project type — backend projects with zero UI still get a `## Scenarios` section enumerating their API/scheduler/webhook/queue/CLI journeys.

**The `## Scenarios` section is NEVER conditional.** Every project has scenarios — even pure-backend libraries (their scenarios are API-call journeys), even CLI tools (their scenarios are command invocations), even data pipelines (their scenarios are batch runs). Contrast: `image-deliverables` IS conditional (backend-only projects omit it). The PMSpec template enforces this distinction.

### 3.2 Wizard-time Scenario surfacing (NEW)

After the user enters a project description and answers the existing clarifying questions, a NEW wizard step appears: **"Scenarios"**:

1. **AI generates initial scenarios** from the project description, inferring `journey_kind` per scenario:
   - For a **tower-defense game**: 8–15 `ui_interaction` scenarios (launch → pick mode → start round → spawn enemies → build tower → tower fires → wave clears → upgrade → next wave → game over → restart).
   - For a **REST API for inventory management**: 6–12 `api_call` scenarios (POST /items happy path, POST /items unauthenticated → 401, GET /items pagination, PUT /items/{id} not-found → 404, DELETE /items/{id} idempotent, bulk import via CSV).
   - For a **scheduled report generator**: 3–5 `scheduled_job` scenarios (nightly run with data → email sent, nightly run with no data → no-op + audit log, run with downstream failure → retry-then-alert).
   - For a **Stripe-integrated billing service**: webhook scenarios (charge.succeeded → invoice paid, charge.failed → invoice still pending + retry scheduled, signature-invalid → 401 + alert).
   - For a **CLI tool**: command-invocation scenarios per public subcommand (happy path, invalid args → exit 2 + usage, missing-file → exit 1 + error message).
2. **User reviews each scenario** with three actions:
   - **Approve as-is** (✓)
   - **Edit** (modify title/steps/terminal state)
   - **Reject** (remove this scenario from scope)
3. **User can also add new scenarios** the AI missed.
4. **Approved scenarios written into `PMSpec.md`** as the `## Scenarios` section (with the `# scenarios` YAML block). `ScenarioRegistry` mirrors them to `scenarios.json` at the same write.

This is the *cheapest moment in the project lifecycle* for the user to align the AI with their intent — before any code is written. It is the natural extension of the existing **pre-PR clarification questions** mechanism, applied at the spec level.

### 3.3 PR-level Scenarios (NEW)

Every engineering-task issue references the scenarios it implements:

```markdown
## Implements Scenarios

- **S03** (Player builds first tower) — steps 1-4 (UI + sprite render)
- **S04** (Player upgrades tower) — step 2 (UI only)
```

This makes per-PR clarification questions **scenario-grounded**: instead of "any clarifications about implementation?" the agent asks "for S03 step 3, does 'Confirm' mean a button click or a second tile-click confirmation?" — every question pinned to the operator-approved scenario language.

### 3.4 T-FINAL Scenario-by-Scenario verification (NEW)

T-FINAL's mandate becomes: **boot the integrated app and execute every approved Scenario.** For each scenario, T-FINAL runs the steps via the new `IAppPlaytester` agent (Layer D), observes the terminal state, and reports per-scenario verdict (✓ verified | ✗ broken | ? inconclusive). Cannot emit `integration.complete` until ≥ 95% of *critical-priority* scenarios pass.

### 3.5 Why Scenarios beat my original "Primary Flow Walkthrough" proposal

| Aspect | Primary Flow Walkthrough (original) | Scenarios (operator-proposed) |
|---|---|---|
| Granularity | 1 numbered flow buried in PMSpec | N atomic stories with stable IDs |
| User visibility | Inside generated PMSpec doc | First-class wizard step with approval, then a `## Scenarios` section in PMSpec |
| Traceability | Implicit | Explicit (PRs cite scenario IDs; PMSpec user stories cross-reference scenarios bidirectionally) |
| Verifiability | All-or-nothing | Per-scenario verdict |
| Drift resistance | Drifts as PMSpec evolves | IDs are immutable; new work = new scenarios; sidecar JSON auto-generated from PMSpec YAML block so drift is impossible by construction |
| App-type generic | "Walkthrough" works for games, awkward for CRUD | Scenarios natural for UI / API / scheduled / webhook / queue / CLI / data-pipeline via `journey_kind` field |
| Document location | Buried inside PMSpec narrative | `## Scenarios` section in PMSpec (non-conditional) with Markdown summary + `# scenarios` YAML block mirroring the existing `image-deliverables` extraction pattern |

**Operator's instinct was correct — Scenarios is a structurally better primitive.**

### 3.6 Rubber-duck pass on the Scenarios mechanism

**Concern 1 — Wizard friction.** "Reviewing 8–15 scenarios is more upfront work for the user."
> **Mitigation:** AI proposes all scenarios; user just clicks Approve unless something is wrong. Same pattern as the existing pre-PR clarification questions. Empirically the operator already pays this cost via post-hoc bug discovery (12 minutes for GridGuardians); paying it once upfront is cheaper. Default: AI proposes 5–8 critical scenarios + 2–4 nice-to-haves; user can defer nice-to-haves to "later".

**Concern 2 — Scenario drift from PMSpec / Architecture.** "What if PMSpec / Architecture say things the scenarios don't, or vice versa?"
> **Mitigation:** Scenarios are the **source of truth**. PMSpec is GENERATED FROM approved scenarios. Architecture must map every scenario to a feature/component. Any subsystem not referenced by a scenario must be flagged as "infrastructure" (allowed) or "scope drift" (challenged). This inverts today's flow where PMSpec is the source and journeys are derived.

**Concern 3 — Scenarios become bureaucratic.** "Every PR has to tag scenarios; every task has to map to scenarios; sounds like Jira."
> **Mitigation:** SE leader writes task → scenario mapping at task-creation time; agents inherit it automatically. The cost is one extra line in the task issue ("Implements: S03, S04"). The benefit is T-FINAL knows exactly what to verify.

**Concern 4 — Scenarios miss internal/non-functional work.** "Database migrations, performance optimization, security hardening — these don't have user-facing scenarios."
> **Mitigation:** Mark such tasks as `infrastructure: true` with a `for_scenarios: [S03, S04]` field. Infrastructure tasks are not directly verified by T-FINAL but the scenarios they support are. Security scenarios DO exist (e.g., "Attacker attempts to view another user's data — denied"). Performance scenarios DO exist (e.g., "Player builds 50 towers; FPS stays ≥ 30").

**Concern 5 — What if user adds a scenario mid-project?**
> **Mitigation:** Scenarios are append-only after wizard approval. A mid-project addition becomes a new scenario (S16+) and triggers SE re-planning. Existing tasks are not invalidated. This is the equivalent of a sprint mid-add — already handled by the executive-request flow.

**Concern 6 — Scenarios are user-facing only.** "What about edge cases, error paths, multi-user concurrency?"
> **Mitigation:** Scenarios CAN be error-path scenarios. E.g., "S08: Player attempts to build tower with insufficient gold → error toast appears, gold not deducted." Concurrency scenarios: "S12: Two players attempt to edit the same document — second player sees lock indicator." These belong in the wizard alongside happy-path scenarios.

**Concern 7 — Adds another LLM hallucination surface.** "AI may generate scenarios that don't reflect what the user wanted."
> **Mitigation:** That's why each is explicitly user-approved before any code is written. The wizard step is precisely the human-correction surface. If the AI hallucinates a scenario the user didn't want, they reject it in the wizard — far cheaper than discovering at T-FINAL time.

**Concern 8 — Devil's Advocate retort: "We already have user stories; this is rebranding them."**
> **Response:** User stories today are bullet-list features generated inside the PMSpec doc. Scenarios are (a) structured objects with stable IDs, (b) operator-approved at wizard time, (c) machine-executable, (d) the source of T-FINAL verification verdicts, (e) the unit of per-PR traceability. The shift from "feature list" to "scenario object" is the same shift from "documentation" to "executable specification." Not rebranding.

**Verdict: Scenarios is a strong addition. Integrate it as Layer 0 (the foundation), not as a sub-bullet of any existing layer.**

---

## 4. The 10 Perspectives — One Paragraph Each + "If You Only Do One Thing"

### 4.1 QA / Test Engineer
Pipeline validates the app *looks like* what was asked for; not that it *does* what was asked for. Screenshots are passive evidence — they confirm rendering, not causality.
**One thing:** Before any PR is marked `tests-added`, require at least one Playwright `page.click()` + `page.waitForSelector()` assertion proving the app's primary action changes visible DOM state.

### 4.2 Product Manager
User stories enumerate parts, not a product. PM-review's file-existence-based verdicts cannot catch the "no-op stub is a file" pattern.
**One thing:** Add the Primary Flow Walkthrough section to `prompts/pm/single-pass-spec.md` (**now: derive PMSpec from approved Scenarios**); every failure traces back to a numbered scenario step that had no owner.

### 4.3 Game / UX Designer
Agents focused on FEATURES but missed the EXPERIENCE. "User-visible value" today means the PR renders without 500 errors — not that there's a coherent user journey.
**One thing:** Make the PM write one numbered journey scenario — with actor/action/system-response/terminal-state — for every feature, and make T-FINAL fail if that scenario cannot be executed on the running app. (**Now: this IS the Scenarios mechanism.**)

### 4.4 End-User Player-Simulator
`PlaywrightCandidatePreviewProducer` only calls `CaptureAppScreenshotAsync` — one PNG. The multi-frame `AppInteractionResult` pipeline is dead code. The infrastructure for behavioral simulation exists; it's just never wired to gating.
**One thing:** Promote `AppInteractionResult` multi-step sequence to a first-class gate. (**Now: this powers IAppPlaytester scenario execution.**)

### 4.5 SRE / DevOps
`WorkflowStateMachine.EvaluateGates()` for `Completion` is literally an empty list. Existing `MissingWork` detectors emit findings but have **no blocking power**.
**One thing:** Add a `testing.app.alive` gate to `EvaluateGates(Review → Completion)` requiring T-FINAL boot the artifact + run smoke + emit clean pass.

### 4.6 Software Architect
The bug lives in the joint between features — a space no agent owns. Current Architecture.md has no Event Catalog, no Feature Initialization Order, no cross-feature integration constraints.
**One thing:** Add `## Event Catalog` to Architecture.md; TE generates one structural test per event.

### 4.7 Stub / Dead-Code Detector
A stub satisfies all syntactic contracts while delivering zero semantic value. No existing gate looks at function body semantics.
**One thing:** Make `StubFunctionBodyDetector` a PR-blocking gate for empty/comment-only function bodies.

### 4.8 Customer Support / User-Voice
Vision drifts across six handoffs (user → PM → PMSpec → Architect → tasks → SE → T-FINAL). T-FINAL never receives the original project description.
**One thing:** Add `{{project_description}}` as a context variable to T-FINAL's `integration-review-user.md`.

### 4.9 PM Milestone / Release Manager
Phases are process milestones — none is a product milestone with measurable user-facing outcomes. The Completion gate is literally empty.
**One thing:** Replace the empty `Completion` gate with `app.canonical_journey_playwright_pass`. (**Now: `scenarios.all_critical_verified`.**)

### 4.10 Devil's Advocate
Pipeline already has 8 approval surfaces. Adding a 9th is rubber-stamp on rubber-stamp. The actual root cause is **absent cross-feature integration ownership at T-FINAL** — not missing validation. The operator's 12-line hotfix proves agents had the right tools; they had the wrong incentives/prompts.
**One thing:** Rewrite the T-FINAL prompt to require the SE leader to run the integrated app and explicitly verify each user-facing feature by name before declaring completion. (**Now: verify each Scenario by name.**)

---

## 5. Common Themes (3+ perspectives agreed)

| Theme | Agreed by |
|---|---|
| **T-FINAL must execute behavior against the running app, not review code/screenshots** | QA, SRE, PM-Milestone, End-User-Sim, Game-Designer, Devil's-Advocate, Customer-Support (7/10) |
| **PMSpec needs a structured "primary user journey" — now Scenarios** | PM, Game-Designer, Customer-Support, End-User-Sim, PM-Milestone (5/10) |
| **Original project description must flow through to T-FINAL** | Customer-Support, PM, Devil's-Advocate (3/10) |
| **Acceptance criteria must be machine-executable, derived from spec** | QA, End-User-Sim, PM, Game-Designer (4/10) |
| **Stub detection should be a hard PR-blocking gate** | Stub-Detector, SRE, QA (3/10) |
| **Cross-feature event/integration contracts must be declared and verified** | Software-Architect, SRE, QA (3/10) |
| **Confidence scores already computed should be surfaced, not discarded** | Customer-Support, Devil's-Advocate (2/10) |

---

## 6. THE UNIFIED PROPOSAL — 5 Layers

### Layer 0 — Scenarios (NEW, operator-proposed, foundation)

**0.1** Add a `Scenarios` step to the Develop wizard. AI proposes 5–15 structured scenarios derived from project description + clarifying-question answers, inferring `journey_kind` per scenario (UI / API / scheduled / webhook / queue / CLI). Each scenario has the schema in §3.1.

**0.2** Operator reviews each scenario: Approve / Edit / Reject + can Add new scenarios. Approved set written **inside `PMSpec.md`** as the `## Scenarios` section (Markdown summary + `# scenarios` YAML block, mirroring the existing `image-deliverables` extraction pattern). `ScenarioRegistry` mirrors the YAML block to a `scenarios.json` sidecar for orchestrator + playtester consumption.

**0.3** Wire scenarios through every downstream artifact:
- **PMSpec.md** — `## Scenarios` section is the single source of truth (NOT conditional — every project has scenarios). Every user story under `## User Stories & Acceptance Criteria` MUST cite scenario IDs it satisfies; every scenario MUST be cited by ≥ 1 user story (else it is orphaned and the PM emit blocks).
- **Architecture.md** — must map every scenario to feature(s)/component(s) implementing it (new `## Scenario → Component Map` section).
- **Engineering tasks** — each task lists `Implements Scenarios: S03 (steps 1-4), S04 (step 2)`.
- **PR descriptions** — `## Implements Scenarios` section (auto-generated from task at PR-creation time).
- **Per-PR clarification questions** — grounded in scenario language ("For S03 step 3, did you mean…?").
- **TE tests** — `tests/scenarios/S03_player_builds_tower.spec.ts` naming convention for UI; equivalent integration-test conventions for API (`tests/scenarios/S08_stripe_webhook_invoice_paid.test.ts`), CLI (`tests/scenarios/S04_cli_upload_csv.bats`), scheduled job (`tests/scenarios/S12_nightly_report.test.ts`), etc.
- **T-FINAL verification** — per-scenario verdict reported.

**0.4** New orchestrator signals:
- `scenarios.approved` (emitted after wizard step)
- `scenarios.architecture.mapped` (every scenario has ≥ 1 component owner)
- `scenarios.tasks.assigned` (every critical scenario has ≥ 1 task implementing it)
- `scenarios.all_critical_verified` (T-FINAL emitted after playtest)
- `scenarios.drift_detected` (raised by `ScenarioRegistry` if PMSpec YAML block and scenarios.json disagree)

**0.5** New service `ScenarioRegistry` in `VirtualDevTeam.Core/Scenarios/`:
- Loads scenarios from PMSpec.md `# scenarios` YAML block (authoritative source).
- Mirrors to `scenarios.json` sidecar at every write.
- Validates drift between YAML block and JSON sidecar; raises Critical FlowFinding on mismatch.
- Exposes typed API to all agents (`GetCritical()`, `GetByJourneyKind(kind)`, `GetByImplementingTask(taskId)`, etc.).
- Emits signals on state changes.
- Backed by a single `Scenario` record type matching the schema in §3.1.

### Layer A — Spec-Time (PMSpec enhancements)

**A1** PMSpec template adds (`prompts/pm/single-pass-spec.md`):
- `## Scenarios` (**NEW required section, non-conditional, replaces "Scenarios Index" idea from earlier drafts**) — contains:
  - **Markdown narrative summary** per scenario (1–3 sentences, human-readable, browseable)
  - **`# scenarios` YAML block** with the full structured schema from §3.1 (deterministic machine-readable form)
  - Section is structurally required for ALL projects (front-end, back-end, CLI, library, pipeline). Mirror the existing `# image-deliverables` extraction pattern — agents split on the marker line.
- `## Security Model` — archetype classification + mandatory controls checklist
- `## Explicit Stubs & Known Gaps` — intentionally-deferred systems named with owner

**A1.1** PMSpec template enforces bidirectional cross-referencing:
- Every user story under `## User Stories & Acceptance Criteria` MUST cite `Implements Scenarios: SXX, SYY` (lint rule: stories without citations are flagged).
- Every scenario MUST be cited by ≥ 1 user story OR explicitly tagged `infrastructure: true` (lint rule: orphan scenarios block the PMSpec gate).

**A2** PM agent now derives PMSpec FROM approved Scenarios, not from project description alone. PMSpec becomes an elaboration of Scenarios, not a parallel artifact. Order of generation: project description → clarifying questions → Scenarios (wizard-approved) → PMSpec elaborates user stories from scenarios.

**A3** Default the existing pre-PR clarification gate to "require approval" for the first project run (currently defaults to auto-proceed in some configs).

### Layer B — Architecture-Time

**B1** Architecture.md template additions (`prompts/architect/multi-turn-compile.md`):
- `## Scenario → Component Map` — every scenario → list of components implementing it (gap detection)
- `## Event Catalog` — every event with `emitter | required subscribers | lifecycle phase`. Discovery primitives that imply ordering (`import.meta.glob`, assembly scanning, reflection) MUST be paired with explicit topological sort.
- `## Feature Initialization Order` — derived dependency graph (Mermaid diagram). Cycles or ordering violations block architecture phase.
- `ARCH-CONTRACT:` annotation convention — every emit/subscribe in code carries a comment Architect parses + TE tests against.

**B2** `EventCatalogValidator` static-analysis pass:
- Scan candidate codebase for `.emit(` / `.on(` patterns
- Cross-reference against declared catalog
- Undeclared emitter = architecture violation
- Subscriber-without-producer = architecture violation
- Emitter-without-subscriber = warning (could be telemetry)

### Layer C — Development-Time (per PR / per task)

**C1** `StubFunctionBodyDetector` PR-blocking gate. New `IMissingWorkDetector` in `src/VirtualDevTeam.Orchestrator/`:
- Cat-A: comments matching `stub|placeholder|no.?op|wip|draft|for now|to be wired|integration point` in function body
- Cat-D: function bodies with zero executable statements
- Cat-E: `_param: any` signatures with empty bodies
- Confidence ≥ 0.80 → applies `stub-detected` label → removes `ready-for-review` → blocks PR

**C2** `STUB_OK:` annotation escape hatch:
- Functions intentionally empty MUST carry `// STUB_OK: <reason> — <agent-id> <date>` annotation
- Detector skips annotated stubs
- Annotation surfaces as `AgentDecision` (impact=High) on Reasoning page

**C3** Completion manifest sidecar. Every SE self-assessment produces JSON:
```json
{
  "exports": [{ "symbol": "register", "fullyImplemented": true, "stubOk": false, "reason": null }],
  "scenarios_implemented": ["S03", "S04"],
  "scenarios_steps_owned": [{ "scenario": "S03", "steps": [1, 2, 3, 4] }]
}
```
`fullyImplemented:false && stubOk:false` = hard block in `MarkPrCompleteAsync` BEFORE PR is submitted.

**C4** SE-leader cross-feature wave-completion test. After each wave of PRs merges, SE Leader (NOT the worker) must boot the COMPOSED app and assert the wave's scenarios advance. Wave-end smoke test, not per-PR.

**C5** `ImplementationDensityDetector` heuristic — public exported function with ≤ 20 LOC and no platform API calls = probable stub. Lower-confidence companion to C1.

### Layer D — Integration-Time (T-FINAL)

**D1** Thread `{{project_description}}` AND `{{scenarios_json}}` into `prompts/software-engineer/integration-review-user.md`. ONE-LINE change with highest individual leverage.

**D2** Rewrite T-FINAL prompt (`integration-review-system.md`) to require **scenario-by-scenario behavioral verification**:
- Boot the integrated app via `PreviewBuildService`
- For each approved scenario, attempt to execute the steps
- Report PER SCENARIO: verified ✓ | broken ✗ | inconclusive ?
- Cannot emit `integration.complete` until ≥ 95% of critical-priority scenarios verified
- Inconclusive scenarios flagged for operator manual review

**D3** `scenarios.all_critical_verified` gate condition in `WorkflowStateMachine.EvaluateGates()`. Replaces the empty `_ => new List<GateCondition>()` at the `Review → Completion` transition. Mandatory signal requires:
- `app.boots == true` (PreviewBuild exit-0)
- Every critical-priority scenario has `verification_status == verified`
- Zero unresolved `Critical` findings in `flow_findings`

**D4** Mandatory `playwright-smoke.spec.ts` artifact. T-FINAL must commit this as part of integration PR. Generated from `scenarios.json` — one test per critical scenario. Filename convention `tests/scenarios/<S##>_<title-slug>.spec.ts`. T-FINAL fails if file absent or empty.

**D5** `IAppPlaytester` agent role + multi-platform adapter pattern. New agent spawned at T-FINAL time:
- Input: `scenarios.json`, candidate worktree, app start command, base URL
- Output: `PlaytestReport` per scenario with verdict + evidence (DOM state, console logs, network traces, labeled screenshots)
- `IPlaytestAdapter` interface: Web (Playwright), CLI (process + stdin/stdout), Desktop (UI Automation), Mobile (Appium)
- Adapter chosen by `AppTargetType` inferred from build output

**D6** Three-layer LLM judge. Replace `CliNativeJudge`'s file-presence scoring:
- Layer 1: deterministic Playwright assertions (no LLM)
- Layer 2: LLM-vision on screenshot **sequence** ("does this look like the scenario claimed?")
- Layer 3: LLM-narrative on trace ("does the sequence tell a coherent story of the scenario?")

**D7** Confidence breakdown surfaced to operator on Approvals page:
- Per-scenario verdict + confidence
- Per-criterion confidence aggregation
- Overall confidence
- Auto-notify operator if any critical-scenario confidence < 0.5 OR overall < 0.7
- Add `RECOMMEND_MANUAL_REVIEW` verdict between APPROVED and REJECTED

**D8** 15-item production-readiness checklist at `Completion` gate. Auto-checkable: build success, smoke pass, no Cat-A stubs, no missing assets, all features registered, event-wiring valid, test coverage met, no compiler warnings, no debug leaks, config complete, screenshots semantically non-empty, smoke-spec exists, integration-PR clean, no unresolved FlowFindings, no security ship-blockers.

---

## 7. Implementation Phasing

**Phase 1 — Cheapest, highest-impact (1–2 hours):**
- D1: thread `{{project_description}}` through T-FINAL prompt
- D2: rewrite T-FINAL prompt to require behavioral verification by feature/scenario name
- A1 (partial): add `## Scenarios Index` section to PMSpec template
- A3: enable pre-PR clarification gate by default in wizard

**Phase 2 — Detector-tier (2–4 hours):**
- C1: `StubFunctionBodyDetector` + PR-blocking integration
- C2: `STUB_OK:` annotation contract
- C3: completion manifest sidecar wiring into `MarkPrCompleteAsync`

**Phase 3 — Scenarios mechanism foundation (4–8 hours):**
- Layer 0 ScenarioRegistry service + Scenarios.md / scenarios.json schema + signal wiring
- Wizard Scenarios step UI + AI-generation prompt
- Per-PR scenario tagging in task creation

**Phase 4 — Gate restructuring (4–8 hours):**
- D3: `scenarios.all_critical_verified` gate replacing empty Completion gate
- D7: confidence-breakdown UI on Approvals page
- D8: 15-item production-readiness checklist runner

**Phase 5 — Agent role addition (8–16 hours):**
- D5: `IAppPlaytester` role + Playwright (Web) adapter
- D4: `playwright-smoke.spec.ts` generation from scenarios.json
- D6: three-layer LLM judge restructure
- A1 (rest): Security Model + Explicit Stubs sections in PMSpec

**Phase 6 — Architecture-time + multi-adapter (16+ hours):**
- B1: Event Catalog + Initialization Order + Scenario→Component Map in Architect template
- B2: EventCatalogValidator static analysis
- D5 (rest): CLI / Desktop / Mobile playtest adapters (defer Desktop/Mobile until VDT generates such an app)

**Phase 1 alone fixes the GridGuardians-class of bug.** Phases 2–6 generalize and harden.

---

## 8. Generic-App Applicability Check

Validating each proposal against 5 app types:

| Proposal | Web Game | CRUD SaaS | CLI Tool | Mobile App | Dashboard |
|---|---|---|---|---|---|
| L0 Scenarios | ✓ play loop | ✓ CRUD lifecycle | ✓ command flows | ✓ navigation flows | ✓ view→filter→update |
| L0 Wizard step | ✓ | ✓ | ✓ | ✓ | ✓ |
| L0 Per-PR tagging | ✓ | ✓ | ✓ | ✓ | ✓ |
| A1 Security Model | ✓ no-XSS | ✓ multi-tenant | ✓ no-cred-leak | ✓ same | ✓ |
| B1 Event Catalog | ✓ Phaser events | ✓ REST events | n/a (skip) | ✓ navigation events | ✓ data-update events |
| C1 StubFunctionBodyDetector | ✓ | ✓ | ✓ | ✓ | ✓ |
| C3 Completion manifest | ✓ | ✓ | ✓ | ✓ | ✓ |
| D1 project_description in T-FINAL | ✓ | ✓ | ✓ | ✓ | ✓ |
| D3 scenarios.verified gate | ✓ playwright | ✓ playwright | ✓ exec+pipe | ✓ Appium | ✓ playwright |
| D5 IAppPlaytester | ✓ Web | ✓ Web | ✓ CLI adapter | ✓ Mobile adapter | ✓ Web |
| D8 Production checklist | ✓ | ✓ | ✓ | ✓ | ✓ |

**Every proposal is implementable across all 5 app types.** Scenarios become the universal contract; `IPlaytestAdapter` becomes the universal verification mechanism.

---

## 9. Rubber-Duck the Full Plan (counter-arguments + responses)

**C1: "You're adding 8 new gates. The pipeline already has 8. Just doubling rubber-stamp surface."**
> Phase 1 is **prompt changes, not new gates**. Phase 2's new gates are **deterministic static analysis** (StubDetector), not LLM rubber-stamps — they cannot false-approve. Phase 4's `scenarios.all_critical_verified` gate REPLACES the empty Completion gate; not additive. The new agent in Phase 5 (`IAppPlaytester`) doesn't approve; it produces evidence. The operator (or T-FINAL agent reading evidence) approves.

**C2: "Playtester AI agent can be confidently wrong in the same direction as the SE."**
> D5's Playtester does NOT make judgments alone. It executes deterministic actions (`page.click`, `page.waitForSelector`), reads observable state (DOM/network/console), and reports findings. The LLM in the stack is bounded to image classification (Layer 2) and narrative coherence (Layer 3). The LLM never approves; it produces evidence that gates check.

**C3: "ONE incident doesn't justify universal changes."**
> The plan generalizes from FAILURE PATTERNS observed in this incident, not from the specific bugs. The pattern "agent declares done; user finds broken" is universal. The 80+ items in `docs/LessonsLearned.md` testify variants recur. Phase 1 is one-line + prompt edits; even if only 50% of Phase 2+ items prevent future incidents, savings are net-positive.

**C4: "PMSpec already has acceptance criteria. The AC quality was poor, not absent."**
> Excellent point. Mitigated by Layer 0: Scenarios are operator-approved structured objects with named subsystems and observable terminal states, not bullet-list features. The format makes shallow acceptance criteria structurally impossible — you cannot write "user can press Play" as a 7-step scenario with subsystems_involved and expected_terminal_state without listing what actually happens after Play.

**C5: "False-positive rate of stub detector."**
> C2's `STUB_OK:` annotation contract gives an explicit escape hatch with traceability. False positives become a one-time annotation cost. Real stubs become hard-blocks. Signal-to-noise ratio operator-tunable via annotation acceptance rate.

**C6: "Cost of Playwright runs."**
> Behavioral playtest runs at T-FINAL (once per integration), not per-PR. Per-PR playtest is OPTIONAL. The wave-completion smoke test (C4) runs once per wave, not per PR. Total added latency = ~3–5 min for T-FINAL, < 1% of total project runtime.

**C7: "If a human can fix it in 12 minutes, why not just have humans always do T-FINAL?"**
> This is the actual ideal. Phase 1's D2 makes T-FINAL behave like a human reviewer. Phase 5's D5+D6 gives the T-FINAL agent the **same scaffolding** a human would use. The plan does not promise eliminating humans — it promises that when humans look, they see structured evidence (D7 confidence breakdown), not 8 green checkmarks they have to override.

**C8: "Multi-platform adapters are months of work."**
> Web adapter (Phase 5) is < 1 day given existing `PlaywrightRunner`. CLI adapter (Phase 6) is < 1 day. Mobile + Desktop are deferred until VDT generates such an app — apply on demand.

**C9 (NEW, rubber-duck on Scenarios): "Wizard friction will drive operators to skip approval and click through."**
> Mitigation: AI proposes a small number (5–8 critical scenarios + a few nice-to-haves). Each scenario card has a single Approve button (most common case). Edits and rejections are minority cases. Operator can also bulk-approve all "critical" scenarios in one click after a quick scan. Expected wizard added time: ~2–5 minutes. The alternative (post-hoc bug fixing) costs orders of magnitude more.

**C10 (NEW): "What happens if SE leader can't map a critical scenario to any task?"**
> The `scenarios.tasks.assigned` signal fails to emit; orchestrator blocks the Architecture → Engineering Planning transition; SE leader gets an explicit "no owner for S07" notification; operator sees this on the Approvals page. Failure mode is loud, not silent. Today's failure mode is silent.

**C11 (NEW, rubber-duck on backend generality): "Scenarios feel UI-centric. Will agents instinctively skip them for pure-backend projects?"**
> Risk addressed in three places: (a) `journey_kind` field is mandatory and explicitly enumerates non-UI kinds (`api_call`, `scheduled_job`, `webhook`, `message_consume`, `cli_invocation`, `data_pipeline`); (b) Wizard prompt examples in §3.2 explicitly show backend project archetypes (REST API, scheduled report, Stripe webhook, CLI tool) so AI proposals demonstrate the pattern; (c) PMSpec template's `## Scenarios` section is **non-conditional** (every project must have one) — contrast `image-deliverables` which is conditional. The naming "Scenarios" rather than "User Journeys" is deliberately neutral to avoid UI bias. A REST-only inventory API will have scenarios like "S03: API caller creates an item with valid payload → 201 + persisted row + emitted event."

**C12 (NEW, rubber-duck on inline vs sidecar): "Embedding scenarios in PMSpec.md will make it huge. Why not just sidecar?"**
> Trade-off considered. Decision: **embed in PMSpec.md with a YAML block + auto-generate sidecar JSON.** Rationale:
>
> - PMSpec is the document operators read and edit. Forcing them to open a separate file to see/edit scenarios breaks the single-document mental model that already works for User Stories, Resolved Ambiguities, and Image Deliverables.
> - The `image-deliverables` precedent shows YAML-in-Markdown is a working pattern in this codebase — agents already know how to extract it (split on `# <marker>` line).
> - Sidecar `scenarios.json` is GENERATED FROM the YAML block, not authored separately, so drift is impossible by construction (drift triggers a Critical FlowFinding via `ScenarioRegistry`).
> - PMSpec size: 10 scenarios × ~30 lines YAML each = ~300 additional lines. Existing PMSpecs are 200–600 lines. Adding ~300 lines is acceptable; if it becomes truly unwieldy at high scenario counts, future work can split critical/non-critical into two YAML blocks or move to a sidecar-only mode behind a feature flag. **Start with inline + sidecar; observe; adjust if pain emerges.**

---

## 10. "If You Only Do One Thing" — Synthesis

10 perspectives + 2 rubber-duck passes converged on a small ranked list:

1. **L0 — Implement the Scenarios mechanism end-to-end** (operator-proposed; rubber-duck pass strongly endorsed) — becomes the universal primitive threading user intent from wizard through to T-FINAL verification.

2. **D1 — Thread `{{project_description}}` + `{{scenarios_json}}` into T-FINAL's review prompt** (Customer Support, Devil's Advocate) — ONE-LINE template change, highest individual leverage.

3. **D2 — Rewrite T-FINAL prompt to require scenario-by-scenario behavioral verification** (Devil's Advocate, Game Designer, QA, PM Milestone) — instructs SE leader to run the app and verify each scenario by name, replacing code review with behavior review.

4. **D3 — Replace empty `Completion` gate with `scenarios.all_critical_verified`** (PM Milestone, SRE) — the literal code location where false-completion enters.

5. **C1 — `StubFunctionBodyDetector` as PR-blocking gate** (Stub Detector, SRE, QA) — catches the pathfinding-stub pattern at PR time, not at user-finds-broken time.

**THE single highest-leverage change**: **L0 + D1 + D2 together**.
- L0 makes Scenarios the universal contract.
- D1 threads the user's words + scenarios into T-FINAL.
- D2 instructs T-FINAL to verify each scenario by name.

The Devil's Advocate was correct that the bug was incentive-alignment at T-FINAL, not validation-tooling absence. Combining L0+D1+D2 fixes the incentive (T-FINAL knows what to verify) AND the contract (Scenarios are operator-approved truth) without adding new gates.

---

## 11. Open Questions for the Operator

1. **Phase 1 timing** — ship D1+D2+A1-partial+A3 immediately as a VDT hotfix, or batch with Phase 2 detectors?
2. **Wizard scenarios review default** — operator reviews EVERY scenario, or "approve all critical" bulk-action with edit-on-demand?
3. **Per-PR vs per-wave behavioral smoke** — every PR run smoke, or wave-completion checkpoint? (Trade-off: latency × N vs delayed failure detection.)
4. **`IAppPlaytester` as new agent role vs library** — new agent in registry (visible in dashboard) or service the T-FINAL agent calls? Affects observability vs simplicity.
5. **Adapter ordering** — Web → CLI → defer Mobile/Desktop until needed?
6. **Scenarios overrides from PMSpec** — if PMSpec evolves and contradicts a Scenario, who wins? (Proposed: Scenarios always win; PMSpec edits propose scenario edits requiring operator re-approval.)
7. **Nice-to-have scenario handling** — defer to "later" track or implement all? (Proposed: critical scenarios are gating; nice-to-haves are advisory and surface as suggestions, not failures.)

---

## 12. Concrete File-Level Change Index (for executors)

| File | Layer | Change |
|---|---|---|
| `prompts/software-engineer/integration-review-user.md` | D1 | Add `## Original Project Description` + `## Approved Scenarios` blocks (the scenarios block embeds the full YAML from PMSpec's `# scenarios` block) |
| `prompts/software-engineer/integration-review-system.md` | D2 | Rewrite to require scenario-by-scenario verification keyed on scenario IDs |
| `prompts/pm/single-pass-spec.md` | A1 | Add **`## Scenarios`** (non-conditional, Markdown summary + `# scenarios` YAML block mirroring `image-deliverables` pattern), `## Security Model`, `## Explicit Stubs & Known Gaps` sections; enforce bidirectional cross-references between user stories and scenarios; derive PMSpec from approved scenarios |
| `prompts/architect/multi-turn-compile.md` | B1 | Add `## Scenario → Component Map`, `## Event Catalog`, `## Feature Initialization Order` sections |
| `prompts/wizard/scenario-generation.md` | L0 | NEW — AI prompt for generating initial scenarios from project description (includes UI/API/scheduler/webhook/queue/CLI archetype examples) |
| `src/VirtualDevTeam.Core/Scenarios/Scenario.cs` | L0 | NEW — record type matching §3.1 schema (includes `journey_kind`, `observation_surfaces`) |
| `src/VirtualDevTeam.Core/Scenarios/ScenarioRegistry.cs` | L0 | NEW — loads scenarios from PMSpec.md `# scenarios` YAML block (authoritative); mirrors to `scenarios.json` sidecar on each write; validates drift and raises Critical FlowFinding on mismatch |
| `src/VirtualDevTeam.Core/Scenarios/ScenarioYamlExtractor.cs` | L0 | NEW — splits PMSpec.md on `# scenarios` marker line and parses YAML body (parallels the image-deliverables extractor pattern in `ImageSpecMismatchDetector`) |
| `src/VirtualDevTeam.Dashboard/Components/Wizard/ScenarioReview.razor` | L0 | NEW — wizard step UI with per-scenario Approve / Edit / Reject + Add-new |
| `src/VirtualDevTeam.Orchestrator/WorkflowStateMachine.cs` line ~486 | D3 | Replace empty `Completion` gate `_ => new List<GateCondition>()` with `scenarios.all_critical_verified` condition |
| `src/VirtualDevTeam.Orchestrator/Signals.cs` | L0, D3 | Add `scenarios.approved`, `scenarios.architecture.mapped`, `scenarios.tasks.assigned`, `scenarios.all_critical_verified`, `scenarios.drift_detected` |
| `src/VirtualDevTeam.Orchestrator/StubFunctionBodyDetector.cs` | C1 | NEW — `IMissingWorkDetector` pattern follows `PhantomTaskReferenceDetector` |
| `src/VirtualDevTeam.Agents/EngineerAgentBase.cs` MarkPrCompleteAsync | C3 | Read completion manifest; hard-block on `fullyImplemented:false && !stubOk` |
| `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs` FinalizeReadyForReviewAsync | C3 | Same as above (per Lesson #14 dual-path) |
| `src/VirtualDevTeam.Agents/ProgramManagerAgent.cs` | A1, A2 | Order generation as project_description → clarifying questions → Scenarios (wizard) → PMSpec (elaborates from scenarios); enforce scenario cross-reference lint rules |
| `src/VirtualDevTeam.Agents/SoftwareEngineerAgent.cs` task creation | L0 0.3 | Stamp `Implements Scenarios: SXX (steps N-M)` into every engineering-task issue at creation time |
| `src/VirtualDevTeam.Core/Agents/Playtest/IAppPlaytester.cs` | D5 | NEW — agent interface; accepts `Scenario[]` input, returns `PlaytestReport[]` with per-scenario verdict |
| `src/VirtualDevTeam.Core/Agents/Playtest/IPlaytestAdapter.cs` | D5 | NEW — adapter interface with `VerifyAsync(Scenario, AppHandle)` dispatched on `journey_kind` |
| `src/VirtualDevTeam.Core/Agents/Playtest/WebPlaytestAdapter.cs` | D5 | NEW — handles `ui_interaction` scenarios; wraps existing `PlaywrightRunner` |
| `src/VirtualDevTeam.Core/Agents/Playtest/ApiPlaytestAdapter.cs` | D5 | NEW — handles `api_call` + `webhook` scenarios; uses `HttpClient` + DB assertions from `observation_surfaces` |
| `src/VirtualDevTeam.Core/Agents/Playtest/CliPlaytestAdapter.cs` | D5 | NEW — handles `cli_invocation` scenarios; uses `Process.Start` + stdout/exit-code assertions |
| `prompts/playtester/*.md` | D5 | NEW — playtester role prompts |
| `src/VirtualDevTeam.Dashboard/Components/Pages/Approvals.razor` | D7 | Add per-scenario confidence breakdown |
| `src/VirtualDevTeam.Dashboard/Components/Pages/Scenarios.razor` | L0 | NEW — Dashboard page listing all scenarios + verification status (read-only mirror of `scenarios.json`) |

---

## 13. Acceptance Criteria for This Plan

This plan is considered successful if, after implementation:

- ✅ A repeat run of the GridGuardians project description **catches both bugs before T-FINAL emits `integration.complete`** — either by Scenario verification failure (Bug #1: init race breaks S03 step 6) or by StubFunctionBodyDetector (Bug #3: pathfinding stub).
- ✅ A new project of a **different app type** (e.g., CRUD SaaS, CLI tool) successfully completes with operator never having to discover post-hoc that a feature doesn't work.
- ✅ Operator wizard added time stays ≤ 5 minutes for projects with ≤ 10 critical scenarios.
- ✅ False-positive rate of StubFunctionBodyDetector (with STUB_OK annotation) stays ≤ 5%.
- ✅ T-FINAL completion latency increases by ≤ 5 minutes (playtest run time).
- ✅ For every existing app type VDT supports today (web game, CRUD SaaS, CLI tool, dashboard), the IPlaytestAdapter mechanism produces meaningful per-scenario verdicts.

---

*Plan synthesized from 10 parallel agent perspectives + 2 rubber-duck passes. Layer 0 (Scenarios) added in second pass per operator proposal. Generic across game / CRUD / CLI / mobile / dashboard / SaaS application types.*
