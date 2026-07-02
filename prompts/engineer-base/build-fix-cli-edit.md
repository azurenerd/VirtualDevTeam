---
version: "1.0"
description: "Build error fix prompt for CLI edit mode - uses native edit tools"
variables:
  - step_number
  - total_steps
  - step_description
  - error_count
  - error_summary
  - scope_relaxation
tags:
  - engineer-base
  - build-fix
  - cli-edit
---
The code from step {{step_number}}/{{total_steps}} ({{step_description}}) has build errors.

BUILD ERRORS ({{error_count}}):
{{error_summary}}

{{> _shared/triage-on-failure}}

Fix the code so it compiles. Use your view tool to read the failing files, then use your edit tool to make targeted fixes.

When creating or moving files during build fixes, verify they are under a project directory referenced by the build chain (e.g., listed in `.sln`, `package.json` workspaces). Orphaned files under unreferenced directories will not be loaded by the app.

{{scope_relaxation}}
