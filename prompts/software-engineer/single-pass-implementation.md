---
version: "1.1"
description: "SE single-pass implementation user prompt (task-first scope-anchoring)"
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
## Task: {{task_name}}
{{task_description}}

## SCOPE RULE (read this before anything else) — CRITICAL
- Produce ONLY the files required by THIS task's acceptance criteria.
- Do NOT implement features from other tasks even if the PM spec / architecture describe them.
- If the task is 'scaffolding' or 'project foundation': emit project manifests, directory structure, placeholder entry-points, and .gitignore ONLY. Do NOT implement pages, components, services, or models — those belong to their own tasks.
- If the task description has a FilePlan (CREATE:/MODIFY:/USE:), follow it strictly. Only output CREATE and MODIFY files.
- Do NOT regenerate files that already exist on the branch (.sln, .csproj, Program.cs, existing components, CSS files, data files) unless the task EXPLICITLY requires changes to them. Regenerating existing infrastructure files causes merge conflicts with other PRs and is the #1 reason for review rejection.
- USE: files are references — read them for context but do NOT include them in your output.
- When in doubt, produce FEWER files rather than more. A downstream task will fill the gap.

## PM Specification (context — DO NOT implement things outside this task)
{{pm_spec}}

## Architecture (context — DO NOT implement things outside this task)
{{architecture}}{{issue_context}}

## Output contract
Implement ONLY the files needed for THIS task (see SCOPE RULE above). Output each file using this exact format:

FILE: path/to/file.ext
```language
<file content>
```

Use the {{tech_stack}} technology stack.
- When modifying an existing file, include the COMPLETE content of that file with your changes applied.
- Every file MUST use the FILE: marker format so it can be parsed and committed.

