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

Implement ONLY the files needed for this specific task. Output each file using this exact format:

FILE: path/to/file.ext
```language
<file content>
```

Use the {{tech_stack}} technology stack.

SCOPE RULE — CRITICAL:
- Only output files that are NEW (created by this task) or MINIMALLY MODIFIED to wire in the new functionality.
- If the task description has a FilePlan (CREATE:/MODIFY:/USE:), follow it strictly. Only output CREATE and MODIFY files.
- Do NOT regenerate files that already exist on the branch (.sln, .csproj, Program.cs, existing components, CSS files, data files) unless the task EXPLICITLY requires changes to them.
- Regenerating existing infrastructure files causes merge conflicts with other PRs and is the #1 reason for review rejection.
- USE: files are references — read them for context but do NOT include them in your output.
- When modifying an existing file, include the COMPLETE content of that file with your changes applied.
- Every file MUST use the FILE: marker format so it can be parsed and committed.
