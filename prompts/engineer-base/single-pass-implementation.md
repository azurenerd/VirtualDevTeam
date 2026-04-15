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

Implement ONLY the files needed for this specific task. Output each file using this format:

FILE: path/to/file.ext
```language
<file content>
```

Use the {{tech_stack}} technology stack.

SCOPE RULE — CRITICAL:
- Only output files that are NEW (created by this task) or MINIMALLY MODIFIED to wire in the new functionality.
- If the task description has a FilePlan (CREATE:/MODIFY:/USE:), follow it strictly.
- Do NOT regenerate files that already exist on the branch (.sln, .csproj, Program.cs, existing components) unless the task EXPLICITLY requires changes to them.
- USE: files are references — do NOT include them in your output.
- Every file MUST use the FILE: marker format. File paths must be valid filesystem paths (e.g., src/Models/User.cs). Do NOT put code, directives, brackets, or instructions in the file path.
