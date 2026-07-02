---
description: Lock a public surface's contract before writing the implementation
tags:
  - shared
  - api
  - design
---

## Contract-first surfaces

When the task introduces a new service, route, public class, or shared module, define the contract BEFORE writing the implementation. The contract IS the specification — refactoring it after consumers exist is at least 10x more expensive than getting it right up front.

### Define for each public method, route, or component prop

- **Inputs** — types, which fields are required vs optional, validation rules and the codes/exceptions used for rejections.
- **Outputs** — success shape, all relevant types, what callers can rely on.
- **Error surface** — typed exceptions, error codes, or status codes that consumers can pattern-match on. "Throws Exception" is not a contract; it's a punt.
- **Idempotency** — is calling this twice with the same input safe? If so, say so; if not, say why and what callers must do instead.
- **Side effects** — state changes, events emitted, log lines produced, files or rows written.

### What you observe becomes contract

Anything a consumer can detect — error message text, response timing, ordering, whitespace in JSON output — eventually becomes a de facto contract once consumers depend on it. Implications:

- Don't expose internal implementation details inside error messages.
- Don't promise an ordering you don't intend to maintain. Use unordered `Set`/`Dictionary` at boundaries when order doesn't matter.
- Don't leak stack traces in API responses; log them server-side and return a generic error code.
- Plan for deprecation: every public surface needs a removal/migration path, even if that path is "delete the helper when call sites reach zero."

### Anti-patterns to avoid

- **Procrastinated return type** — "I'll figure out the shape as I implement." Define the type first; refactoring it after consumers exist is the expensive option.
- **Bare exception** — `throw new Exception("...")`. Use a typed error hierarchy callers can branch on.
- **Visibility ambiguity** — "I'll keep this private for now." If the next task needs it, the surface is already public-shaped; don't make the next engineer pry it open.
- **Twin functions** — two methods that do "almost the same thing" with different parameter orders. Pick one canonical form and route the other through it.

### Sequence

1. Sketch the contract as TypeScript-style or C#-style interface declarations in a comment or scratch file.
2. List the 2–3 most important error conditions and how callers will recognize them.
3. Then write the implementation — the contract is now your spec.
