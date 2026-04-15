---
version: "1.0"
description: "Custom agent task processing user prompt"
variables:
  - task_title
  - task_description
  - project_context
tags:
  - custom
  - task
---
## Task: {{task_title}}

{{task_description}}

## Project Context
{{project_context}}

Produce your work product. Be thorough and specific.
