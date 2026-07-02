---
version: "1.0"
description: "Guidance for generating technology-appropriate .gitignore files in T1 scaffolding"
tags:
  - engineer-base
  - scaffolding
  - gitignore
---
## .gitignore Requirements for T1 Scaffolding

The `.gitignore` MUST be the **first file** in T1's FilePlan (`CREATE:.gitignore`). Derive all patterns from the project's `{{tech_stack}}` and architecture — do NOT use a generic template or copy from a single stack.

### Categories to Cover
Analyze the technology stack and include patterns for EVERY applicable category:

1. **Build/compiler output** — directories where compiled artifacts land (e.g., bin/, obj/, dist/, build/, target/, out/, .next/, __pycache__/)
2. **Dependency directories** — package manager install targets (e.g., node_modules/, vendor/, .venv/, packages/)
3. **Package artifacts** — generated package files (e.g., *.nupkg, *.tgz, *.whl, *.gem)
4. **IDE/editor files** — workspace-specific config (e.g., .vs/, .idea/, *.swp, .vscode/settings.json)
5. **OS files** — system junk (e.g., .DS_Store, Thumbs.db, desktop.ini)
6. **Secrets/environment** — local config with credentials (e.g., .env, .env.local, .env.*.local, appsettings.Development.json)
7. **Test/coverage output** — generated test results (e.g., coverage/, test-results/, playwright-report/, .nyc_output/)
8. **Logs/temp/cache** — runtime artifacts (e.g., *.log, logs/, .cache/, tmp/)
9. **Generated/framework files** — framework-specific outputs (e.g., .angular/, .svelte-kit/, .parcel-cache/, .terraform/)

### Multi-Component Projects
For projects with multiple technologies (e.g., frontend + backend, mobile + API), create ONE root `.gitignore` that covers ALL components. Do NOT create nested `.gitignore` files in subfolders.

### Do NOT Ignore These
- Lockfiles (`package-lock.json`, `pnpm-lock.yaml`, `yarn.lock`, `Cargo.lock`, `poetry.lock`, `go.sum`)
- Migrations and seed data
- Sample/template config files (`.env.example`, `.env.template`)
- Runtime data files the app needs to function (`data.json`, static assets, fixture files)
- Source code or assets checked into the repo intentionally
- Infrastructure-as-code files (Dockerfiles, Terraform .tf files, CI/CD configs)

### VDT Agent Workspace Artifacts (ALWAYS include)
Every `.gitignore` MUST include these patterns to exclude VirtualDevTeam runtime artifacts that agents create in the project workspace during development:

```
# === VDT Agent Workspace (do not commit) ===
.candidates/
.candidates-eval/
.screenshots/
.virtualdevteam/
.agents/
.completion-manifests/
AgentDocs/
.squad/
.squad-workstream
```

These directories are created by VDT agents at runtime (strategy framework candidates, evaluation scratch, captured screenshots, agent state DB, workspace metadata, completion manifests, and agent documentation). They must NEVER be committed to the project repository.
