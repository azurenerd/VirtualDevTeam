---
version: "1.0"
description: "SE single-pass implementation user prompt"
variables:
  - pm_spec
  - architecture
  - issue_context
  - task_name
  - task_description
  - tech_stack
tags:
  - software-engineer
  - implementation
---
## PM Specification
{{pm_spec}}

## Architecture
{{architecture}}{{issue_context}}

## Task: {{task_name}}
{{task_description}}

Produce a complete implementation for this task. Output each file using this exact format:

FILE: path/to/file.ext
```language
<file content>
```

Use the {{tech_stack}} technology stack. CRITICAL: You MUST include a .csproj project file at the project root AND a .sln solution file at the repository root. Without these, `dotnet build` will fail. The .sln must reference the .csproj. Include all source code files, configuration, and tests. Every file MUST use the FILE: marker format so it can be parsed and committed.
