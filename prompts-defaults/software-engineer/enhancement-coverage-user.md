---
version: "1.0"
description: "SE enhancement coverage user prompt"
variables:
  - enhancement_number
  - enhancement_title
  - enhancement_body
  - existing_tasks_summary
tags:
  - software-engineer
  - validation
---
## Uncovered Enhancement #{{enhancement_number}}: {{enhancement_title}}
{{enhancement_body}}

## Existing Engineering Tasks
{{existing_tasks_summary}}

Is this enhancement covered by the existing tasks, or was it missed?
