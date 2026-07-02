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

{{> _shared/context-engineering}}

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
- **Before creating ANY new source file**, search the workspace for `AI_STUB` or `AI_TODO` markers targeting your task ID. Replace stubs IN-PLACE at their existing path — do NOT create a parallel file elsewhere. If a stub is at `src/MyApp/Components/Foo.razor`, replace that file at that exact path — do not create `MyApp/Components/Foo.razor` under a different project directory.
- **For T1/scaffolding tasks**: The `.gitignore` must be comprehensive and technology-specific — derived from the {{tech_stack}} stack and architecture. Cover: build/compiler output, dependency directories, package artifacts, IDE/editor files, OS junk, secrets/env, test/coverage output, logs/temp/cache, and framework-generated files. Do NOT ignore lockfiles, migrations, seed data, or runtime data files the app needs.
- **For T1/scaffolding tasks**: Create developer-experience configuration files so the project works after a fresh `git clone`. Every framework needs a file to activate development mode (ASP.NET Core: `Properties/launchSettings.json` with `ASPNETCORE_ENVIRONMENT=Development`; Node.js: `.env` or `package.json` scripts with `NODE_ENV=development`; Python: `.flaskenv` or `settings/development.py`; Java/Spring: `application-dev.yml`). Without these, production defaults break static assets, RCL content, hot reload, and debug pages. Include default ports and working database connection strings. See `dev-experience-guidance.md` for details.
- **Startup must be idempotent** — if the task creates/modifies database seed code, config-endpoint seed, or anything that runs during `app.Build()`/`app.Run()`: running the app TWICE without deleting the database must NOT crash. Use EF Core `OnModelCreating + HasData`, or `INSERT OR IGNORE` / `INSERT ... ON CONFLICT DO NOTHING`, or a check-then-insert (`if (!await db.Set<T>().AnyAsync(...)) await db.AddAsync(...)`). Never combine `EnsureCreated()` with imperative `INSERT` into UNIQUE columns. If the API can't start, the frontend will render a blank canvas — pipeline tests will silently pass but every screenshot will be white. Validate by mentally running `dotnet run` twice in a row.
- **NO CI/CD pipeline files unless the task description EXPLICITLY requests them.** Local-run-only is the default. Do NOT create `.github/workflows/*.yml` (GitHub Actions), `.azure-pipelines.yml`, `azure-pipelines.yml`, `Jenkinsfile`, `.gitlab-ci.yml`, `Dockerfile`-for-CI, or any other CI/CD config file based on "good engineering practice" heuristics. If a project genuinely needs CI, the task description will say so explicitly (e.g., "set up GitHub Actions CI to run tests on every push"). Reason: agent-authored CI workflows fire on every push, fill the operator's inbox with red failure emails, and burn build minutes for a project the user runs locally only. When in doubt, leave CI out — the user can add it later if needed.

---

If your task description, file plan, or architecture excerpt mentions any of the following, apply the security checklist below before finalizing your output: authentication, login, password, token, session, OAuth, JWT, API key, secret, cookie, encryption, hashing, sanitization, input validation, file upload, parsing user-provided data, CORS, CSP, rate limiting, or external HTTP calls. If your task is purely a static UI / local-only HTML / pure render with none of those triggers, you may skip the checklist (only the "Hard do not" rules from the bottom of the checklist still apply).

{{> _shared/security-checklist}}

If your task introduces a new service, API route, public class, shared module, or any cross-task surface, lock the contract before writing the implementation per the guidance below. If your task is a leaf-level UI page or a wholly internal component with no public surface, you can skip this section.

{{> _shared/api-interface-contract}}

If Architecture.md defines integration contracts, initialization sequences, or resource binding contracts, verify and enforce them for your task per the guidance below. If your task is a leaf-level component with no cross-module wiring, you can skip this section.

{{> _shared/integration-contract-enforcement}}

If your task touches lists, database queries, loops over large data, or hot rendering paths, run the performance checklist below before finalizing. For static-site / small-data-volume slices, the data-access rows are not applicable and the UI/memory rows are sufficient.

{{> _shared/performance-checklist}}
