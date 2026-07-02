---
description: Engineer-side guidance for consuming and enforcing integration contracts from Architecture.md
tags:
  - shared
  - integration
  - enforcement
---

## Integration contract enforcement

Architecture.md defines Integration Contracts specifying how components wire together. You MUST consume and enforce these contracts — not regenerate them.

### Before planning or implementing

1. Locate the **Integration Contracts table** in Architecture.md. If it is missing or your component is not listed, raise a blocking question — do not silently skip wiring.
2. Find rows where your component is the **Wiring Owner** — you are responsible for implementing those calls.
3. Find rows where your component is a **Consumer** — note the provider and preconditions you depend on.
4. Check the **Mandatory Initialization Sequence** — if your module appears, respect its position and SYNC/ASYNC/PARALLEL markers.
5. Check **Shared Invariants** — if your module consumes a cross-module value, import it from the single source of truth. NEVER duplicate the constant locally.

### During implementation

For each contract where you are the Wiring Owner:

- Implement the wiring call exactly as specified (active voice: "main.ts calls X.init()").
- Ensure preconditions are met before the call executes.
- Handle the specified failure mode (MUST throw / MUST log + degrade). NEVER silently fall back to defaults.
- If the wiring file is outside your initial file list but necessary to satisfy a contract, it is in scope — use the `INTEGRATION EDIT: <path> — <reason>` format.

### Before marking complete

- Every wiring call you own MUST be reachable from an initialization or composition path — not just defined but never called.
- Every resource or artifact your code consumes MUST be loaded through the binding mechanism specified in Architecture.md, not assumed present.
- If you discover a missing contract (your component needs wiring not listed in Architecture.md), document it in your implementation summary so integration review can catch it.
