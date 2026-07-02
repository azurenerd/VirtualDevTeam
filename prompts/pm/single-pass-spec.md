---
version: "1.2"
description: "Single-pass PMSpec generation prompt"
variables:
  - project_name
  - project_description
  - research_doc
  - design_sections
  - unanswered_decisions
  - approved_scenarios_yaml
  - existing_project_context
tags:
  - pm
  - spec
  - single-pass
---
I need you to create a PM Specification for our project.

> **PMSpec is DERIVED FROM the wizard-approved Scenarios below — not from the project description alone.** The generation order is: project description → clarifying questions → Scenarios (wizard-approved) → PMSpec elaborates user stories FROM those scenarios. PMSpec is an elaboration of the approved Scenarios, not a parallel artifact. You MUST NOT invent new scenarios; if the approved scenarios are insufficient for the project scope, raise a clarification request rather than silently expanding scope. Every user story you write must trace back to at least one approved scenario ID.

**Project Name:** {{project_name}}

**Project Description:**
{{project_description}}

## Research Findings
{{research_doc}}
{{unanswered_decisions}}
{{#existing_project_context}}
## Existing Project Context
This is a feature for an EXISTING project. The following summary describes the current codebase, architecture, and conventions. Your spec MUST account for the real tech stack, patterns, and constraints already in place. User stories and acceptance criteria should reference existing components, APIs, and patterns where applicable.

{{existing_project_context}}
{{/existing_project_context}}
## Wizard-Approved Scenarios

The following scenarios were approved by the operator in the wizard step. They are the authoritative source of user journeys for this project. Use them verbatim when writing the `## Scenarios` section of the PMSpec.

{{approved_scenarios_yaml}}

## Resolve Ambiguity Before Writing Stories

Before drafting any user stories or acceptance criteria, walk through the project description and the research findings and identify 3–8 places where the requirements left material gaps (data residency, persistence model, identity/branding, scale assumptions, auth strategy, deployment target, error-tolerance posture, accessibility expectations, etc.). For each gap, pick a resolution explicitly and write it down so downstream agents (Architect, Software Engineer) can challenge or extend it later. These decisions go in a required `### Resolved Ambiguities` block inside `## Constraints & Assumptions`.

Format every resolution as one line:

- **<topic>:** chose <resolution>; rejected <alternative> (revisit if <trigger>).

Illustrative entries (do not copy):

- **Data residency:** chose local-only browser storage; rejected cloud-synced persistence (no backend in scope). Revisit if multi-device support is requested.
- **Visual identity:** chose neutral enterprise theme; rejected organization-specific branding (no logo or color guide supplied).

Each user story you write afterwards should explicitly reference any of these decisions it depends on, so reviewers can spot misalignment quickly.

Produce a complete, structured PMSpec.md document with ALL of these sections:

# PM Specification: {{project_name}}

## Executive Summary
(2-3 sentences describing what we're building and why)

## Business Goals
(Numbered list of concrete business objectives)

## User Stories & Acceptance Criteria
(Each story as: **As a [role]**, I want [capability], so that [benefit]. Immediately after the benefit clause, add `Implements Scenarios: SXX, SYY` — citing every scenario ID this story contributes to. Followed by acceptance criteria as a checklist. For UI stories, reference the specific visual section from the design file.

**Cross-reference rule (BOTH directions required):** Every user story MUST carry an `Implements Scenarios:` citation — stories without it are invalid. Every scenario in `## Scenarios` MUST be cited by ≥ 1 user story OR explicitly tagged `infrastructure: true` in the scenarios YAML — orphan scenarios (uncited AND not infrastructure) block the PMSpec gate.)

{{design_sections}}## Scope
### In Scope
(Bullet list)
### Out of Scope
(Bullet list — explicit exclusions to prevent scope creep)

## Non-Functional Requirements
(Performance targets, security requirements, scalability needs, reliability SLAs)

## Success Metrics
(Measurable criteria for project completion)

## Constraints & Assumptions
(Technical constraints, timeline assumptions, dependency assumptions)

### Resolved Ambiguities
(Required: list every decision made under "Resolve Ambiguity Before Writing Stories" above, in the one-line format. Each downstream story that depends on one of these decisions should reference it.)

## Scenarios

> **NON-CONDITIONAL — every project MUST emit this section.** Unlike `## Image Deliverables`, the Scenarios section is NEVER omitted. Pure-backend APIs, CLI tools, data pipelines, scheduled jobs, and REST services all have scenarios — their journeys are `api_call`, `cli_invocation`, `data_pipeline`, `scheduled_job`, `webhook`, or `message_consume`. Omitting this section is a spec defect.

Begin with a short opening paragraph naming each approved scenario from the wizard with its `journey_kind`, e.g.:

> This spec covers 5 scenarios: S01 (ui_interaction — player starts game), S02 (ui_interaction — player builds tower), S03 (webhook — payment callback), S04 (api_call — list items), S05 (scheduled_job — nightly report).

Then write a Markdown narrative summary for each scenario (1–3 sentences each):

- **S01 — [Title]:** [1–3 sentences describing the actor, key steps, and observable terminal state.]

Finally, emit the `# scenarios` YAML code block — the deterministic machine-readable form. Agents split on the marker line `# scenarios` to extract the YAML body (identical convention to `# image-deliverables`). Populate from `{{approved_scenarios_yaml}}` — do not alter approved scenario IDs or `status: approved`. Update `implementing_tasks` as tasks are created; `verification_status` and `verification_evidence_url` are filled in by T-FINAL.

```yaml
# scenarios
# journey_kind enum values:
#   ui_interaction | api_call | scheduled_job | event_arrival | webhook
#   | message_consume | cli_invocation | system_initiated | data_pipeline
#
# observation_surfaces.kind values per journey type:
#   UI  → dom_query | dom_text | event_bus | canvas_state
#   API → http_response | db_row | db_count | queue_message
#   Job → log_line | db_row | db_count | file_artifact
#   CLI → process_exit_code | stdout_pattern | file_artifact

- id: S01
  title: "<scenario title>"
  journey_kind: ui_interaction    # ui_interaction | api_call | scheduled_job | event_arrival
                                  # | webhook | message_consume | cli_invocation
                                  # | system_initiated | data_pipeline
  actor: "<who or what initiates — e.g. 'Player', 'API caller (authenticated)', 'Stripe webhook', 'Scheduler (cron 02:00)'>"
  trigger: "<what the actor does to begin this journey>"
  preconditions:
    - "<what must be true before this scenario runs>"
  steps:
    - "1. <first observable step>"
    - "2. <next step>"
  expected_terminal_state:        # concrete, observable outcome; where to look depends on journey_kind:
    - "<observable outcome>"      #   ui_interaction: DOM state / canvas state / fired events
                                  #   api_call: HTTP status + response body shape + DB row
                                  #   scheduled_job: log line + DB rows + side-effects
                                  #   webhook: external system ack + DB state + downstream queue
                                  #   message_consume: queue ack + DB state + emitted events
                                  #   cli_invocation: exit code + stdout pattern + file artifact
  observation_surfaces:
    - kind: dom_query             # Per-kind required fields:
      selector: "<CSS selector>"  # dom_query:         selector
                                  # dom_text:          selector, expected_change
                                  # event_bus:         event_name
                                  # canvas_state:      description
                                  # http_response:     status, max_latency_ms
                                  # db_row:            query, expected (object)
                                  # db_count:          query, expected_change
                                  # queue_message:     topic, event_type
                                  # log_line:          pattern
                                  # process_exit_code: expected
                                  # stdout_pattern:    regex
                                  # file_artifact:     path
  subsystems_involved:
    - "<subsystem-slug>"
  priority: critical              # critical | important | nice-to-have
  status: approved                # proposed | approved | edited | rejected
  infrastructure: false           # set true for non-user-facing infrastructure scenarios; infrastructure
                                  # scenarios are exempt from the user-story cross-reference requirement
  implementing_tasks: []          # filled in by SE leader at task-creation time
  verification_status: not_yet_verified  # not_yet_verified | verified | broken | inconclusive
  verification_evidence_url: null        # set by T-FINAL — link to playtest artifact
```

**Cross-reference enforcement (both directions — violations are spec defects):**
- Every scenario MUST be cited by ≥ 1 user story in `## User Stories & Acceptance Criteria` via `Implements Scenarios: SXX` — OR tagged `infrastructure: true` in the YAML above. Orphan scenarios (uncited AND not infrastructure) block the PMSpec gate.
- Every user story in `## User Stories & Acceptance Criteria` MUST cite ≥ 1 scenario via `Implements Scenarios: SXX, SYY`. Stories without this citation are invalid.

## Security Model

Classify this application's archetype (pick **exactly one** and state it explicitly):

| Archetype | When to use |
|---|---|
| `SingleUserBrowserGame` | Browser-based, single anonymous player, no persistent server-side user accounts |
| `SingleUserDesktop` | Desktop app, single local user, data stored locally |
| `MultiUserSaaS` | Multiple authenticated users, cloud-hosted, multi-tenant or multi-user data |
| `PublicAPI` | Publicly accessible HTTP API, no user authentication required (or API-key-only) |
| `InternalAPI` | HTTP API restricted to authenticated internal callers or service-to-service |
| `InternalTool` | Internal-use web app or CLI, accessed only by employees or trusted operators |
| `CLITool` | Command-line interface, no network-facing surface, credentials only via OS environment or config file |
| `BackgroundService` | Daemon/worker, no user-facing surface, receives work via queue/scheduler |
| `DataPipeline` | Batch or streaming ETL, no interactive users |
| `Library` | Importable package, no runtime surface of its own |

**Archetype:** `<chosen archetype>`

Based on the archetype, fill in the mandatory controls checklist:

| Control | Decision | Notes |
|---|---|---|
| **Authentication strategy** | e.g., None / Cookie session / JWT Bearer / OAuth2 / API key / mTLS | — |
| **Authorization model** | e.g., None / RBAC / ABAC / owner-only / admin-only | — |
| **Input sanitization** | e.g., N/A / HTML-escape all output / parameterized queries / schema validation | — |
| **Secret storage** | e.g., N/A / OS keychain / env vars / secrets manager / .env (dev-only) | — |
| **CSRF / CORS posture** | e.g., N/A / SameSite=Strict / CORS allowlist / no cookies | — |
| **Output encoding** | e.g., N/A / HTML-escape / JSON-encode / signed responses | — |
| **Dependency-update policy** | e.g., dependabot weekly / manual on release / not applicable | — |

**Security Auditor agent activation contract:** If the archetype is `MultiUserSaaS`, `PublicAPI`, or `InternalAPI`, the Security Auditor agent (when configured) activates automatically after the Architecture phase to review authentication, authorization, and input-validation controls. Fill in the checklist above so the Security Auditor has a concrete baseline rather than deriving one from scratch. For other archetypes, the Security Auditor is advisory only.

## Explicit Stubs & Known Gaps

List every subsystem or feature that is **intentionally deferred or stubbed out** in this implementation. This is the explicit allow-list for the `StubFunctionBodyDetector`'s `STUB_OK:` annotation contract — only stubs with an entry here may carry a `// STUB_OK:` annotation in code without triggering a PR-blocking finding.

An empty table means "nothing is intentionally stubbed — all stubs in the implementation are defects."

| System / Feature | Reason for Deferral | Named Owner | Acceptance Criteria for "Stub Is OK Here" |
|---|---|---|---|
| e.g., `payment-processor` | Payment gateway integration out of scope for MVP | `future SME: payments` | Returns hardcoded success response; `STUB_OK:` annotation present in code referencing this spec entry |
| e.g., `analytics-telemetry` | Telemetry pipeline not built yet | `none-yet` | No-op calls; `STUB_OK:` annotation present |

If there are no intentional stubs, write explicitly: _"No intentional stubs. All `STUB_OK:` annotations in code are defects."_

## Image Deliverables (CONDITIONAL — emit ONLY when project needs visual assets)

Before this section, evaluate whether the project description and resolved ambiguities call for ANY of: sprite sheets, character art, tower/enemy/unit art, UI icons, hero illustrations, logos, favicons, marketing images, screenshots, mockups, branding visuals, animation frames, or any other generated raster/vector asset.

- If **NO visual assets are needed** (typical: backend API, CLI tool, library, data-pipeline, infrastructure-only project): **OMIT this entire section**. Do not emit an empty `[image-deliverables]` block. The downstream Artist agent uses the section's PRESENCE as the trigger to spawn — emitting an empty block forces a no-op Artist run and wastes cycles.

- If **visual assets ARE needed**, emit the section below as a YAML code block tagged with `image-deliverables` (NOT inside the YAML body where it would parse as a flow-sequence list and silently drop the schema). The downstream Artist agent splits on the marker line `# image-deliverables` to extract the YAML body deterministically.

```yaml
# image-deliverables
# Per-entity sprite sheets (game / character-driven projects only)
sprites:
  - entity: <entity-name>             # short slug used as the asset directory
    base-path: client/public/assets/sprites/<entity-name>/
    animations:
      - name: idle                    # animation state name (idle, walk, fire, die, etc.)
        frames: 4                     # number of distinct frames in the cycle
        frame-size: 256x256           # per-frame pixel dimensions
        frame-duration-ms: 150        # default frame duration for the runtime animator
      - name: <other animation>
        frames: <N>
        frame-size: <WxH>
        frame-duration-ms: <ms>

# UI icons / favicons / brand marks (any project with a UI surface)
icons:
  - name: <slug>
    path: <full path under repo root>
    size: <WxH>
    description: <one-line visual description for the artist>

# Marketing / hero / illustration art (marketing sites, landing pages, slide decks)
illustrations:
  - name: <slug>
    path: <full path under repo root>
    size: <WxH>
    description: <one-line visual description>

# Style anchor (REQUIRED if any sprites/illustrations declared above) — single image
# all other assets must visually match. Acts as the reference for cross-asset consistency.
style-anchor:
  path: client/public/assets/style-anchor.png
  description: <one-paragraph anchor description: art style, palette, line weight, perspective, lighting>
```

Use only the sub-blocks (sprites / icons / illustrations / style-anchor) the project actually needs. Each asset listed becomes a concrete deliverable the Artist must produce and the FlowMonitor's image-spec-mismatch detector will verify exists on the working branch.

**Generality rule (do NOT violate):** the decision to emit each sub-block must come from the project description, NOT from a hardcoded keyword whitelist. A "physics-based puzzle game with 3 levels" needs sprites; a "REST API for inventory management" does not; a "marketing site for a SaaS product" needs illustrations + icons but no sprites. Match by capability/intent, not by literal keyword presence.

Use these exact section headers. Be thorough, specific, and business-focused. Each user story must have clear acceptance criteria. This document will be the single source of truth for business requirements.

