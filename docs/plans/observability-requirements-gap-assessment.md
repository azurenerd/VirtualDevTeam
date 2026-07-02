# Observability / Telemetry Requirements — Prompt Gap Assessment

> **Status:** Research/audit findings for consideration — **nothing implemented.** Validates whether
> VDT's prompts make the apps/features VDT builds emit the logs, metrics, and telemetry an operator
> needs to know "what's going on" and to diagnose/fix issues during build-out and testing.

## TL;DR

**VDT does NOT systematically require observability in the software it builds.** The architect and
software-engineer prompts contain only incidental, error-path mentions of logging — there is **no
first-class requirement** for structured logging, metrics, telemetry, health/diagnostics, or
error-surfacing. When a generated `Architecture.md` *does* cover observability well (as the TPCI
agencyplugin one does), it is because **that feature happens to be about monitoring**, not because the
prompts mandated it. A generic CRUD/UI feature would likely ship with little to no instrumentation,
leaving operators blind when something breaks during verification or testing.

## What the audit found

### 1. Architect prompts — no observability requirement (root cause)
Scanned `prompts/architect/*.md` for logging/telemetry/metrics/observability/monitoring/instrument/
trace/diagnostic terms:

- `full-system.md`, `role-description.md`, `multi-turn-cross-cutting.md`, `multi-turn-components.md`,
  `multi-turn-decisions.md`, `pr-review-system.md`: **zero** observability requirements.
- `multi-turn-data-model.md:12`: lists *"monitoring needs"* as one item inside a generic "Infrastructure
  Requirements" bullet — the only positive nudge, and it's easily skipped.
- `multi-turn-compile.md:48,120`: a telemetry **emitter-without-subscriber** wiring contract — this is
  about *graph-validation* (allowing telemetry emitters to have no subscribers), **not** a requirement
  that the app emit telemetry.

There is no NFR section, archetype, or checklist that forces the architecture to define a logging
strategy, metrics, correlation IDs, health endpoints, or error-surfacing — unlike the strong, explicit
treatment given to security archetypes and visual verification.

### 2. Software-engineer prompts — observability only as error-handling incidentals
Scanned `prompts/software-engineer/*` and `prompts/engineer-base/*`:

- `plan-generation-system.md` / `plan-generation-user-suffix.md`: **zero**. Every task is required to
  carry a mandatory `## Visual Verification` section — but **no** equivalent `## Observability` / logging
  requirement. Tasks can ship with no instrumentation and still pass the plan format.
- `implementation-system.md:30`: logging appears only as *"expose the failure via logging"* in the
  no-always-error-stub rule (an error fallback, not a positive instrumentation mandate).
- `self-assessment-system.md:33`, `dev-experience-guidance.md:46`: startup-crash verification and
  developer error pages — diagnostics for the *build pipeline*, not runtime observability of the feature.
- `integration-review-system.md:94`: "observable terminal states" — about scenario correctness, not logs/metrics.

### 3. Generated artifact — coverage is feature-luck, not prompt-driven
The latest TPCI `AgentDocs/agencyplugin/Architecture.md` (54 KB) actually has **good** observability:
an `Observability` row (`ILogger` + `Stopwatch`/`ActivitySource` spans; per-phase `ReviewStep.Summary`),
a "Preserve observability" goal, telemetry fields (`ExecutionModeUsed`, plugin-version pin), and explicit
startup logging contracts (`MUST log "IPrivacyReviewRunner resolved to {Type}"`).

**But this feature is intrinsically about monitoring plugin execution** — observability was the product,
so it landed in scope naturally (and the one `monitoring needs` nudge in `multi-turn-data-model.md`
likely helped). This is **not evidence the prompts enforce observability**; it's a best case. The audit's
concern is the *typical* feature, where nothing in the prompts guarantees the same.

### 4. Consequence (ties back to current pain)
When a built app has no structured logging/metrics, and something fails during scenario verification or
testing, **there is nothing for the operator (or FlowMonitor, or the playtester) to read.** That directly
amplifies the diagnosis difficulty we just hit (silent Inconclusive scenarios, opaque app-launch failures).
Good app-level observability is synergistic with VDT's own operability.

## Recommendations (prompt changes only — not implemented here)

| # | Change | Where | Effort | Value |
|---|--------|-------|--------|-------|
| R1 | Add a first-class **Observability & Diagnostics NFR**: the architecture MUST define a logging strategy (levels, key lifecycle + failure events, correlation/trace IDs), what metrics/telemetry to emit, a health/diagnostics surface, and how errors are surfaced (never silently swallowed). Give it the same weight as the security-archetype and visual-verification treatment. | `prompts/architect/full-system.md` + a `multi-turn-*` step | Low | High |
| R2 | Add a **mandatory `## Observability` section per task**, mirroring the existing mandatory `## Visual Verification`: each task states what it logs (key events + failures *with context*), any counters/metrics, and how failures are surfaced. Make **T1 (foundation)** establish the logging/telemetry baseline (logger config, correlation, a metrics sink) so every vertical slice inherits it. | `prompts/software-engineer/plan-generation-system.md` + `plan-generation-user-suffix.md` | Low–Med | High |
| R3 | Extend **self-assessment / integration review** with a check: the implemented slice logs its key lifecycle events and surfaces failures with actionable context (no silent `catch`). VDT already forbids silent error banners; extend to "failures must be logged, not swallowed." | `prompts/engineer-base/self-assessment-system.md`, `prompts/software-engineer/integration-review-system.md` | Low | Med |
| R4 | (Optional) Make the **scenario/visual-verification prompt** prefer reading app logs as corroborating evidence, so the verifier can cite log output — improving both app observability adoption and verification signal. | `prompts/test-engineer/*`, AppPlaytester prompt | Med | Med |

## Suggested validation
After R1–R2, run a **non-observability** feature (e.g., a simple CRUD/UI app) through the pipeline and
inspect the generated `Architecture.md` and the engineering-task issues for a real Observability section
and per-task logging requirements. If they appear and are concrete, the gap is closed; if not, tighten
the wording (the prompts must *require*, with an example, not merely *mention*).

---
*Prepared as analysis only. No prompts, code, or configuration were changed by this document.*
