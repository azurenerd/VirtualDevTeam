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
- Produce ONLY the files required by THIS task's acceptance criteria — PLUS any minimal edits to existing files that are STRICTLY NECESSARY for this task to actually work end-to-end.
- You MAY modify files outside your declared CREATE list when the task cannot function without it. The canonical cases are:
  - Registering a new service/interface in the app entry point (e.g., `Program.cs`: `builder.Services.AddSingleton<IFoo, Foo>()`).
  - Adding a `@using` / `@inject` to `_Imports.razor` so a new component is resolvable.
  - Adding a route/endpoint mapping for a new page.
  - Adding `using` or `import` statements in existing files that reference your new types.
  - Adding framework-required middleware that your new feature depends on (e.g., `app.UseAntiforgery()` when your component uses forms).
- When you make such a cross-file edit, you MUST:
  1. Include the COMPLETE updated file content via a `FILE:` marker.
  2. Limit the change to the MINIMUM needed (no refactors, no renames, no style changes).
  3. Add a short comment at the top of your response (above the FILE markers) in this format:
     `INTEGRATION EDIT: <path> — <one-sentence reason>`
- Do NOT re-scaffold the project, regenerate `.sln`/`.csproj` templates, rewrite Program.cs from scratch, or restyle existing CSS — that's NOT integration, that's scope creep.
- If the task is 'scaffolding' or 'project foundation': emit project manifests, directory structure, placeholder entry-points, and .gitignore ONLY. Do NOT implement pages, components, services, or models — those belong to their own tasks.
- If the task description has a FilePlan (CREATE:/MODIFY:/USE:), follow it strictly for the primary files. Integration edits per the rules above are also allowed.
- USE: files are references — read them for context but do NOT include them in your output unless you also need an integration edit there.
- Prefer FEWER files. Every FILE: marker not needed for your task to work (primary OR integration) is scope creep.

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

