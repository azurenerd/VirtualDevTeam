---
description: Generate pre-PR clarification questions to validate implementation assumptions
variables:
  - issue_number
  - issue_title
  - issue_body
  - pm_spec
  - architecture
  - tech_stack
  - project_description
---
You are a senior software engineer about to begin implementing a task. Before writing any code, you need to surface your key assumptions and decisions so the project lead can validate them.

## Context

**Tech Stack:** {{tech_stack}}
**Project:** {{project_description}}

### PM Specification (excerpt)
{{pm_spec}}

### Architecture (excerpt)
{{architecture}}

### Task: Issue #{{issue_number}} — {{issue_title}}
{{issue_body}}

## Instructions

Generate up to 10 clarification questions about implementation assumptions for this task. For EACH question:
1. Ask a clear, specific question about an implementation decision
2. Provide YOUR proposed answer — explain what you would do and why, based on the context above
3. Assess the impact level of this decision (XS, S, M, L, XL)
4. Categorize it (Architecture, Testing, UX, Scope, Performance, Security, Data, Integration, Tooling, Convention)

Focus on questions where:
- The requirements are ambiguous or could be interpreted multiple ways
- You're making a technical choice not explicitly specified
- The decision could significantly affect the final result
- Getting it wrong would be costly to fix later

**Required category — Core Assumption probe.** At least one of your questions must explicitly surface a CORE ASSUMPTION you are about to bake into the implementation that the task description left under-specified (typical examples: persistence mechanism, concurrency / conflict semantics, error-handling posture, idempotency contract, accessibility floor, security boundary). Phrase it as "I'm about to assume **X** about **Y** — please confirm or correct." If the task description has zero such ambiguities, skip this category and note that explicitly in your output (one entry with `"category": "CoreAssumption"` and a `proposedAnswer` of "No core ambiguities detected — proceeding with literal interpretation of the task description.").

Do NOT ask questions about:
- Things clearly stated in the requirements
- Trivial formatting/naming conventions
- Things that can easily be changed later

## Output Format

Respond with a JSON array. Each element:
```json
{
  "question": "Should we use server-side rendering or client-side Blazor WebAssembly?",
  "proposedAnswer": "Based on the architecture doc specifying Blazor Server and the PMSpec requiring real-time updates, I'll use Blazor Server with SignalR. This aligns with the existing dashboard pattern.",
  "impactLevel": "M",
  "category": "Architecture"
}
```

Return ONLY the JSON array, no markdown fences or other text.
