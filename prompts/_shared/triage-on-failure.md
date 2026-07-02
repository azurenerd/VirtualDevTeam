---
description: Discipline for diagnosing build, test, and runtime failures before patching
tags:
  - shared
  - debugging
  - failure-recovery
---

## Diagnose before you patch

When a build breaks, a test fails, or runtime behavior surprises you, do NOT immediately reach for a fix. The cheapest bug to resolve is the one you catch before it ends up layered under three new commits.

### Step 1 — Pause

No new features, no opportunistic refactors, no jumping ahead to the next task. Resolving one failure cleanly beats stacking three half-fixes.

### Step 2 — Capture the failure record

Before changing anything, write down (or keep on screen):
- The complete error message, copied verbatim — not paraphrased.
- The exact command, route, or action that produced it.
- The most recent change you made — file path plus a one-line description.
- The current workspace state — is the tree clean, mid-edit, or partially built?

### Step 3 — Confirm reproducibility

Can you trigger the failure on demand?
- **Yes** → continue to Step 4.
- **No** → the failure is intermittent. Run the failing path 3–5 times, count successes versus failures. Anything below 100% indicates a timing, concurrency, or environment-dependent issue. Do NOT mark this resolved without a regression test that consistently fails before your change.

### Step 4 — Write down a hypothesis BEFORE editing

State it as one sentence: "I think this fails because **<cause>**. If I'm right, applying **<change>** should resolve it. If the failure persists or changes shape, I'm wrong."

Doing this in writing prevents the fix-by-guessing pattern that turns one bug into a multi-cycle rework loop.

### Step 5 — Aim at the cause, not the symptom

Symptom-only fixes that don't belong here:
- Catching and discarding the exception.
- Bumping a timeout to mask flakiness.
- Skipping or commenting out the failing test.

Root-cause fixes do belong here. If you can't identify a root cause within roughly 5 minutes of investigation, add diagnostic logging and re-run the failing path — do NOT push a speculative patch.

### Step 6 — Lock in the cure

Either:
- (a) add a regression test that fails without your change and passes with it, OR
- (b) explain in the PR description why the failure can't be deterministically tested (timing race, environment-dependent, external service, etc.).

### Common anti-patterns to avoid

- **Combo fix** — bundling several unrelated repairs into one commit. One root cause per commit; reviewers can't reason about a grab-bag.
- **Skip-the-test** — deleting or `[Skip]`-annotating a failing test instead of repairing it. Forbidden unless the test itself is wrong AND you state that explicitly with a reason.
- **Push-without-verify** — pushing the fix without re-running the exact failing command locally. Always re-verify before handing off.
- **Symptom swallow** — `try { ... } catch { /* nothing */ }` to silence noise. Always log or rethrow with context.
- **Speculative refactor** — restructuring nearby code "while you're in there" without test coverage. Defer that cleanup or land it as a separate PR.
