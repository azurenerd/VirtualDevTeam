---
version: "1.0"
description: "SE PR description generator user prompt"
variables:
  - pm_spec
  - architecture
  - issue_context
  - task_name
  - task_description
tags:
  - software-engineer
  - pr-description
---
## PM Specification
{{pm_spec}}

## Architecture
{{architecture}}{{issue_context}}

## Task: {{task_name}}
{{task_description}}

Write a detailed PR description with:
1. **Summary**: What this PR implements
2. **Acceptance Criteria**: Specific, testable criteria
3. **Implementation Steps**: Ordered, numbered list of discrete steps. Step 1 = scaffolding. Each step is a committable unit. 3-6 steps.
4. **Testing**: What tests should cover
