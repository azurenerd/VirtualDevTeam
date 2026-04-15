---
version: "1.0"
description: "Single-pass implementation user prompt for legacy PRs"
variables:
  - pm_spec
  - architecture
  - task_title
  - pr_body
  - tech_stack
tags:
  - engineer-base
  - implementation
---
## PM Specification
{{pm_spec}}

## Architecture
{{architecture}}

## Task: {{task_title}}
{{pr_body}}

Produce a complete implementation. Output each file using this format:

FILE: path/to/file.ext
```language
<file content>
```

Use the {{tech_stack}} technology stack. Include all source code files, configuration, and tests. Every file MUST use the FILE: marker format. File paths must be valid filesystem paths (e.g., src/Models/User.cs). Do NOT put code, directives, brackets, or instructions in the file path.
