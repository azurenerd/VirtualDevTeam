---
version: "1.1"
description: "SE integration review user prompt"
variables:
  - project_description
  - scenarios_yaml_block
  - pm_spec
  - architecture
  - task_summary
tags:
  - software-engineer
  - integration
---
## Original Project Description
{{project_description}}

## Approved Scenarios
{{scenarios_yaml_block}}

> **Verification contract:** The scenarios above are operator-approved and represent the binding definition of "this app works." Every `priority: critical` scenario MUST reach `verified ✓` before you may declare integration complete. The Original Project Description (above) is the ultimate ground truth — if any scenario contradicts it, the project description wins and that scenario must be flagged `inconclusive ?` for operator review.

## PM Specification
{{pm_spec}}

## Architecture
{{architecture}}

## Completed Tasks
{{task_summary}}

Review the merged work against these documents. Generate any missing integration files (config, wiring, startup registration, etc.).
