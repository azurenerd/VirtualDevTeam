---
version: "1.0"
description: "PE integration review user prompt"
variables:
  - pm_spec
  - architecture
  - task_summary
tags:
  - principal-engineer
  - integration
---
## PM Specification
{{pm_spec}}

## Architecture
{{architecture}}

## Completed Tasks
{{task_summary}}

Review the merged work against these documents. Generate any missing integration files (config, wiring, startup registration, etc.).
