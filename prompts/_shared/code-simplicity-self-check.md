---
description: Reviewer-style simplicity check for self-assessment and peer review
tags:
  - shared
  - review
  - simplicity
---

## Simplicity rubric

The aim is NOT a smaller line count. The aim is code a new contributor can read once and understand without you sitting next to them.

### Five questions to ask before approving

1. **Same behavior?** Does the simpler shape produce the same output, the same error behavior, and the same observable side effects for every input the original handled? When in doubt, leave it.
2. **Same conventions?** Does the change match the project's existing patterns — naming, imports, error handling, log shapes? Inconsistency-creating "simplification" is just churn; reject it.
3. **Comprehension over compression?** Pick explicit code over compact code whenever the compact form makes a reader pause. Stacked ternaries, chained reduces with inline closures, and magic constants all qualify.
4. **One use, one site?** Don't extract a helper, interface, or abstraction until there are at least three real call sites. Premature extraction is harder to remove than to add later.
5. **Anything dead?** Unused imports, unreachable branches, no-op variables (`var _ = ...`), backwards-compat shims with no remaining consumer, `// removed` stubs — all should go.

### Quick smell list

- ❌ A 500-line file where 100 lines would carry the same intent.
- ❌ A class wrapping one method (use a function or a static helper).
- ❌ An interface with a single implementation (defer the abstraction until at least two implementations exist).
- ❌ A configuration knob for a value that has never changed in code or in production.
- ❌ Defensive null-checks for parameters the type system already forbids.
- ❌ `Util` / `Helper` / `Manager` grab-bag classes that grow past ~200 lines (split or inline them back).
- ❌ Comments that restate what the code obviously does — keep only the WHY-comments.
- ❌ TODO / FIXME / XXX with no associated tracking issue (file the issue or delete the comment).
- ❌ `try { ... } catch { /* nothing */ }` with no logging and no rethrow.

### Output

If you're reviewing: list each violation as `[Simplicity #N]` with `<file>:<line>` and a concrete fix.

If you're self-assessing: count the violations. Three or more means revise the implementation before publishing for peer review.
