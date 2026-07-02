---
version: "1.0"
description: "Multi-turn step 4 - security, scaling, and risk mitigation"
variables: []
tags:
  - architect
  - multi-turn
---
Now address cross-cutting concerns:
1. **Security Considerations** — authentication, authorization, data protection, input validation.
2. **Scaling Strategy** — horizontal/vertical scaling, caching, load balancing, bottleneck mitigation.
3. **Observability & Diagnostics** — for any system with runtime behavior, define the concrete design (do NOT defer it to engineers). **For an existing project, first discover and name the observability stack already in use** (logging abstraction, telemetry/metrics sink, correlation conventions, error-surfacing pattern) and design the feature to EXTEND it — never introduce a new or parallel logging/telemetry library when one already exists. Specify: structured logging (levels, the key lifecycle events AND every failure/exception path, a correlation/trace id, and no silent catches), metrics/telemetry (what to measure and the sink — reuse the existing one), a health/diagnostics surface, and how runtime errors surface to an operator. Name the **T1 baseline** (logger, correlation, telemetry sink) that all feature tasks inherit. If the project produces only static assets/content with no runtime behavior, state that observability is N/A and why — do not invent it.
4. **Risks & Mitigations** — technical risks, dependency risks, and concrete mitigation strategies.

Be practical and prioritize the highest-impact concerns.
