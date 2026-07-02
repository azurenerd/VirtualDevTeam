---
version: "1.0"
description: "Single-pass research user prompt with full document structure requirements"
variables:
  - topic
  - topic_description
  - existing_project_context
---
Research the following topic for our software project.

**Topic:** {{topic}}

**Context:**
{{topic_description}}

{{#existing_project_context}}
## Existing Project Context
This is a feature for an EXISTING project. The following is a summary of the current codebase, architecture, and conventions. Your research MUST account for the real tech stack, patterns, and constraints already in place — do NOT recommend alternatives to what's already established unless there's a compelling reason.

{{existing_project_context}}
{{/existing_project_context}}

Produce a comprehensive, structured research document with these sections:

1. **Executive Summary** — Concise overview of findings and primary recommendation.
2. **Key Findings** — Most important discoveries, one per bullet (prefixed with '- ').
3. **Recommended Technology Stack** — Specific tools, frameworks, libraries with versions. Organize by layer (frontend, backend, database, infrastructure, testing).
4. **Architecture Recommendations** — Patterns, data flow, structural decisions.
5. **Security & Infrastructure** — Auth, hosting, deployment, operational concerns.
6. **Risks & Trade-offs** — Technical risks, bottlenecks, mitigation strategies.
7. **Open Questions** — Decisions needing stakeholder input.
8. **Implementation Recommendations** — Phasing, MVP scope, quick wins.

Use these exact section headers. Be specific, opinionated, and actionable. Include version numbers, compatibility notes, and real-world considerations.
