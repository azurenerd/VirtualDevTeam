# Proposed Plan: Vertical-Slice Workflow Improvements for VDT

> **Status:** Proposal for consideration — **nothing here is implemented.** This document
> assesses ideas from an external "thin vertical slices" workflow (John Socha-Leialoha's
> Bowties [AI Workflow Guide](https://github.com/JohnSL/Bowties/blob/main/docs/project/ai-workflow-guide.md))
> against VDT's current pipeline, and proposes which (if any) are worth adopting.

## 1. Context & source

An internal LT discussion surfaced strong results from building with **thin vertical slices** plus an
**architecture-focused, just-in-time** workflow. Core observation from that thread:

> "AI behaves like a smart but impatient junior engineer — it optimizes for speed and takes shortcuts,
> which quickly leads to fragile systems." Horizontal slices **delay discovery of design issues** and
> **cause architectural drift as complexity grows.**

The remedy in the guide: keep a spec step, add architecture-focused skills, and **implement in thin
vertical slices** — each end-to-end, **user-testable by a human**, validated, then expanded.

## 2. What VDT already does (the user is right — we have vertical-slice guidance)

VDT already encodes a meaningful vertical-slice discipline:

- **SE plan generation mandates vertical slices.** `prompts/software-engineer/plan-generation-system.md`
  and `plan-generation-user-suffix.md` explicitly require each task to be a *"self-contained vertical
  slice that produces user-visible value"*, **reject horizontal layer-tasks** ("all models", "all
  services", "all pages"), fold presentation/styling into the feature that uses it, and require each task
  to be *"independently demonstrable."*
- **Foundation-first scaffolding (T1 / Wave W0)** with machine-trackable `AI_STUB(Tn)` / `AI_TODO(Tn)`
  markers so the app builds & runs before features land.
- **Wave-based dependency model** (W0 → W1 → W2…), star topology, distinct-file ownership to enable
  **parallel** execution without merge conflicts.
- **Architect phase** producing `Architecture.md` via multi-turn design prompts (data-model, components,
  cross-cutting, decisions, compile) before SE planning.
- **Visual Verification** section per task + **AppPlaytester scenario verification** (user-demoable
  checks) + **TestEngineer** coverage + **pre-publish self-assessment**.

So VDT's slices are real. The deltas below are about **when** design is validated, **how** tasking is
sequenced, and **how** we keep the AI from "shortcutting into fragility."

## 3. The external workflow's distinguishing ideas

| Idea | Bowties guide | VDT today |
|------|---------------|-----------|
| Slice definition | End-to-end **and human-demoable** ("testable" = *user-demoable*, not just test-covered) | User-visible value + visual verification (close match) |
| **Tasking timing** | **Just-in-time**: `/slices` writes a *roadmap of cards* (intent, boundary, acceptance criteria, arch note); `/build` appends the per-layer task breakdown **one slice at a time** and **re-cuts the next slice after each finishes** | **All tasks written up front** as engineering-task issues, then executed |
| **Execution order** | **Sequential**, validate-then-expand; **stop at every slice boundary** with tests passing | **Parallel** across engineers/waves (throughput-optimized) |
| Architecture gate | `/design` validates the **slice set** against **placement rules + ADRs** *before* building; design-shifting slices carry an **architecture note** | Architect produces `Architecture.md`; no explicit ADRs/placement-rules; workflow has **no backward transitions** |
| TDD | **Test-first** per slice (red→green→refactor); optional context-isolated TDD coordinator | **Test-after** (TE adds tests post-implementation) + self-assessment |
| HITL classification | Slices labeled **HITL / AFK / REFACTOR**; HITL slices surface the **architectural-pattern question before** implementing | Configurable human gates, but not per-slice architectural-pattern gating |
| "Don't shortcut" rule | **architecture-first-fix**: if cleanup reveals a deeper seam/layer/ADR problem, **stop and surface options** instead of patching through | Rework loops can "patch through"; no explicit stop-on-seam rule |
| Context hygiene | Incremental **knowledge base** (`aiwiki/`) re-orients new sessions from files | `AgentMemoryStore` + docs (different shape) |

## 4. Gap analysis — where there's genuine benefit

VDT's biggest philosophical difference is **parallel up-front tasking** vs the guide's **sequential
just-in-time tasking**. VDT chose parallelism deliberately (throughput; multiple specialist engineers).
That is a real strength and should *not* be discarded. But it has known costs that the guide's ideas
directly target — and several VDT "Lessons Learned" are symptoms of exactly these costs:

- **Plan churn / locking into a flawed design.** Because VDT writes the *entire* task breakdown up front,
  a mid-build design discovery forces expensive re-planning (and several T-FINAL re-invocation / wave-
  eligibility lessons exist). The guide's "re-cut the next slice after each finishes" keeps pivots cheap.
- **Late design-issue discovery.** VDT validates slices (scenario/visual verification) largely **after**
  merge and often **in parallel**, so design problems surface late. The guide validates **before**
  expanding the next slice.
- **Shortcutting into fragility.** VDT rework prompts can "patch through" a symptom; there is no explicit
  "stop and surface a seam/layer problem" rule.

## 5. Proposed improvements worth considering (ranked by value ÷ effort)

These are designed to **layer onto** VDT's parallel-wave model, not replace it.

### P1 — "architecture-first-fix" stop rule in rework/self-assessment (high value, low effort)
Add an explicit instruction to the engineer rework + self-assessment prompts: *if a fix reveals a deeper
design problem (wrong layer, violated boundary, a contract that should change), **stop and raise it**
(decision gate / PM clarification / FlowMonitor finding) rather than patching around it.* Directly
counters the "impatient junior" failure mode. Touches `prompts/engineer-base/rework-*.md`,
`self-assessment-*.md`. **No architecture change.**

### P2 — Per-slice "architecture note" + acceptance criteria in engineering-task issues (high value, low-med effort)
Extend the SE plan format so each task/slice that **introduces a new pattern or seam** carries a short
**architecture note** (which boundary it crosses, which interface it depends on) and an explicit
**human-demoable acceptance criterion** (already partially present via Visual Verification). Makes the
existing up-front plan carry the *design-impact* signal the guide gets from `/design`. Touches
`plan-generation-*.md` and the Architect review.

### P3 — Validate the slice SET against placement rules / lightweight ADRs at the Architect gate (med value, med effort)
Introduce a minimal **placement-rules + ADR** concept (VDT has neither today) that the Architect produces
and the SE plan is checked against — so horizontal/misplaced slices are rejected at **design time**, not
discovered at merge. Could be a section in `Architecture.md` + a check in PM/Architect PR review.

### P4 — Boundary "validate-then-expand" for the dependency frontier (med value, med-high effort)
Keep parallelism **within a wave**, but make **wave promotion** gate on *human-demoable* validation of the
prior wave's slices (we already have most of this via scenario verification + `IsWaveEligible` requiring
PR merged — see Lesson #45). The increment is to make wave promotion also consider **scenario-verified**
status, not just merged, so a wave can't build on an unvalidated slice. Note: this depends on scenario
verification actually running — see the recently fixed workspace-skip + the playtest-timeout changes.

### P5 — Optional test-first (TDD) for designated "core/seam" slices (med value, high effort)
For slices flagged as architecturally load-bearing, have the TE (or SE) write a failing user-level test
**before** implementation. Expensive to retrofit into VDT's test-after pipeline; scope to high-risk slices
only. Consider only after P1–P3.

### P6 — Per-module knowledge base for context hygiene (low-med value, high effort)
Formalize an incremental `aiwiki/`-style per-module ownership/flows/health KB to re-orient agents across
restarts from files. VDT's `AgentMemoryStore` overlaps; this is a larger investment with unclear marginal
value over current docs. **Lowest priority.**

## 6. Explicitly NOT recommended

- **Switching VDT to fully sequential, single-slice-at-a-time building.** This is the guide's model but it
  discards VDT's core parallel-throughput advantage and its multi-specialist architecture. The value is in
  borrowing the *validation discipline* (P1–P4), not the serial execution model.
- **Replacing the up-front plan with pure just-in-time tasking.** Incompatible with wave-based parallel
  assignment and orphan-recovery. P2/P3 capture most of the benefit (design-impact signal + early
  rejection) without abandoning up-front planning.

## 7. Recommendation

Adopt **P1** and **P2** first (cheap, directly target the "fragility from shortcuts" + "late design
discovery" failure modes, fit the existing prompt architecture with no workflow change). Evaluate **P3**
and **P4** next. Treat **P5/P6** as long-horizon. Re-run a small project after P1–P2 and compare
regression/rework rates against the current baseline before investing in P3+.

---
*Prepared as analysis only. No prompts, workflow code, or configuration were changed by this document.*
