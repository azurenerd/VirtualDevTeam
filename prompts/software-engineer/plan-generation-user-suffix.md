---
version: "1.0"
description: "SE engineering plan generation user prompt suffix"
variables:
  - projectComplexity
  - targetTaskCount
tags:
  - software-engineer
  - planning
---
Create an engineering plan mapping these Issues to tasks. REMEMBER:
- T1 MUST be the Project Foundation & Scaffolding task (High complexity, no dependencies). It sets up the solution structure, shared interfaces, base classes, config, DI registration, AND the shared presentation foundation (theme tokens/resources, global styles/templates, layout shell, common UI primitives) so all other tasks have a clear skeleton to build upon. T1 is in Wave W0 — it runs ALONE.
- T1 must create COMPREHENSIVE placeholders: every model, every interface, every component stub, every CSS section marker, sample data files, config files. After T1 merges the app must BUILD and RUN.
- T1 MUST use `// AI_STUB(Tn)` markers on ALL placeholder method bodies and `// AI_TODO(Tn)` on sections needing future work. Each marker names the task expected to replace it. This makes stubs machine-trackable for self-assessment and T-FINAL validation.
- Each later task's Description field should note which `AI_STUB(Tn)` markers it is expected to replace and in which files.
- ALL other tasks should depend on T1 at minimum and be in W1 or later.
- Design tasks for PARALLEL execution: each task should own distinct files with NO overlap.
- NEVER assign the same file as CREATE in two different tasks.
- Prefer vertical slices (one feature end-to-end INCLUDING its styling) over horizontal layers.
- Maximize tasks that depend ONLY on T1 (star topology, not chains).
- Assign each task a WAVE: W0 for T1 only, W1 for tasks after T1, W2+ for later waves.
- Each task must include ALL presentation assets needed for its feature (e.g., CSS, XAML/QML resources, scene/prefab files) — NEVER create standalone styling or theming tasks for individual features.
- This is a {{projectComplexity}} project — produce AT MOST {{targetTaskCount}} tasks (excluding T-FINAL). Consolidate related features into comprehensive vertical slices rather than splitting into many small tasks.

Output ONLY structured lines in this EXACT 10-field format:
TASK|<ID>|<IssueNumber>|<Name>|<Description>|<Complexity>|<Dependencies or NONE>|<FilePlan>|<Wave>|<SkillTags>

**CRITICAL — The Description field (field 5) MUST include a `## Visual Verification` section** at the end of the description. This tells the automated media pipeline what URLs to test and what to expect visually. Without it, screenshots will fail or capture blank pages.

Format within the Description field:
```
...task description text...

## Visual Verification
- App type: web-ui | api-only | cli | library
- Test URLs: /path1 (description), /path2 (description)
- Expected: What should be visible on each page
```

For scaffolding tasks (T1) that create an API without frontend: specify `api-only` and test `/swagger`. For tasks creating UI pages: specify `web-ui` and list the exact routes. For library/CLI tasks with no visual output: specify `none`.

**CRITICAL — The Description field (field 5) MUST also include a `## Observability` section** after Visual Verification — but be SMART about applicability. Tasks with runtime behavior must say what they log (key events + failures with context), any metrics, and how errors surface. Tasks that only produce static assets/sprites/images, static content/data, pure styling, or docs have nothing to observe — mark them `none` and add NO instrumentation.

Format within the Description field:
```
## Observability
- Applicability: runtime | none
- (runtime) Logs: key lifecycle events + every failure path, with correlation id
- (runtime) Metrics: counters/timers worth emitting (or "none")
- (runtime) Errors surface via: logs + API/UI error contract
- (none) Reason: e.g., "generates sprite PNGs; no runtime behavior to observe"
```

Examples — runtime: `Applicability: runtime; Logs: error on import failure (file, reason); Metrics: imports_total{outcome}; Errors surface via: log + ProblemDetails`. Static asset: `Applicability: none — generates sprite PNGs; no runtime behavior to observe`.

**CRITICAL — The Name field (field 4) MUST be a descriptive feature title** like "Build Dashboard Header Component" or "Implement Data Service". NEVER use a wave identifier (W1, W2, W3) as the Name — the Wave has its own dedicated field (field 9). A Name like "W2" is WRONG; a Name like "Implement Monthly Heatmap Grid" is CORRECT.

The FilePlan field should contain semicolon-separated file operations:
  CREATE:path/to/file.ext(namespace);MODIFY:path/to/existing.ext;USE:ExistingType(namespace)
  SHARED:path/to/file.ext — declare a file that multiple tasks may modify (use sparingly, T1 only)

The Wave field: W0 for T1 only, W1 for tasks parallelizable after T1, W2+ for later waves.

The SkillTags field: comma-separated domain tags for skill-based engineer assignment (e.g., frontend,blazor,css or backend,api,database or foundation).

Example:
TASK|T1|42|Project Foundation & Scaffolding|Create solution structure, shared models, interfaces, DI registration, and configuration|High|NONE|CREATE:.gitignore;CREATE:MyApp.sln;CREATE:MyApp/MyApp.csproj;CREATE:MyApp/Program.cs(MyApp);CREATE:MyApp/Models/AppConfig.cs(MyApp.Models);SHARED:MyApp/Program.cs|W0|foundation
TASK|T2|43|Implement Auth Module|Build JWT authentication with refresh tokens|Medium|T1|CREATE:MyApp/Services/AuthService.cs(MyApp.Services);MODIFY:MyApp/Program.cs;USE:IAuthService(MyApp.Interfaces)|W1|backend,api,security
TASK|T3|44|Build User Profile Page|Build user profile page with Blazor components|Medium|T1|CREATE:MyApp/Components/UserProfile.razor(MyApp.Components)|W1|frontend,blazor,css

Note: T1 is the ONLY task in W0. T2 and T3 are both in W1 (parallel-safe) and own completely separate files. The Name field is always a descriptive title, NEVER a wave identifier.

Only output TASK lines, nothing else.
