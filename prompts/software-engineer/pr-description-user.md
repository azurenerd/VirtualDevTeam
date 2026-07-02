---
version: "1.0"
description: "SE PR description generator user prompt"
variables:
  - pm_spec
  - architecture
  - issue_context
  - task_name
  - task_description
  - implementing_scenarios
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

{{implementing_scenarios}}

Write a detailed PR description with:
1. **Summary**: What this PR implements
2. **Acceptance Criteria**: Specific, testable criteria
3. **Implementation Steps**: Ordered, numbered list of discrete steps. Step 1 = scaffolding. Each step is a committable unit. 3-6 steps.
4. **Testing**: What tests should cover
5. **Implements Scenarios**: List the scenario IDs this PR addresses (e.g., S01, S03). If the task's issue body has a "## Implements Scenarios" section, copy those IDs here. Format as a bullet list:
