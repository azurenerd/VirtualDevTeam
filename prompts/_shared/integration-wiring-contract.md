---
description: Ensure cross-module integration is explicit, wired, and verified
tags: [shared, integration, architecture]
---

## Integration & wiring contracts

AI agents build components correctly in isolation but fail to wire them together. The architecture MUST make integration explicit by including these sections.

### Integration Contracts table

For every cross-module dependency, include a table:

| Provider | Consumer | Wiring Call | Owner | Preconditions | On Failure |
|----------|----------|-------------|-------|---------------|------------|
| ConfigLoader | App | `app.configure(config.get())` | main.ts | config.ready resolved | MUST throw, exit 1 |

Owner = the file responsible for making the call. Use active voice: "main.ts calls X.init()".

### Shared invariants & single sources of truth

For values interpreted by multiple modules (dimensions, IDs, schemas, config values, bounds, etc.):

1. **Invariant** — what must stay consistent
2. **Source of truth** — which module/constant owns the authoritative value
3. **Consumers** — which modules read/derive from it
4. **Derivation rule** — import/call/lookup (NOT duplicated constant)

Duplicated constants across modules MUST NOT define shared behavior.

### Initialization sequence

Single ordered startup list. Mark each step `[SYNC]`/`[ASYNC]`/`[PARALLEL]`. End with **READY gate** — conditions that MUST ALL be true before operational state. App MUST NOT start until gate passes.

### Resource / artifact contract (when applicable)

When the project consumes files, assets, config, or external artifacts:

1. **Manifest schema** — machine-readable, not prose
2. **Discovery** — how code finds resources at runtime (including post-build paths)
3. **Validation** — what MUST pass before runtime use
4. **Binding** — how resources connect to consuming code and which module owns binding. Resources do not become available merely by existing in a directory.
5. **Consumer assumptions** — MUST be guaranteed by schema or validated before use

### Runtime configuration (correctness-affecting only)

For settings where wrong defaults silently produce incorrect behavior: specify the setting, required value, owner, verification method, and reapplication trigger.

### Integration verification

For each integration point:

1. **Testable assertion** — concrete check proving integration works (name modules, data, outcomes)
2. **Vertical slice** — minimal end-to-end proof via production wiring path
3. **No silent fallbacks** — missing integration MUST cause visible, logged failure

### Anti-patterns

If you can describe the architecture without naming which file calls which function, wiring is missing. Watch for: passive voice hiding the wiring owner, silent fallbacks masking failures, file existence mistaken for integration, assumed init order without enforcement, and duplicated constants across modules.

### Exit criteria

Use RFC 2119 keywords. Avoid passive voice.
**Bad:** "The config service provides configuration to the app."
**Good:** "main.ts MUST call `configService.load()` and MUST pass the result to `app.configure()` before `app.start()`."
