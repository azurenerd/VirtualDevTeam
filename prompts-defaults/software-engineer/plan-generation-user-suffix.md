---
version: "1.0"
description: "SE engineering plan generation user prompt suffix"
variables: []
tags:
  - software-engineer
  - planning
---
Create an engineering plan mapping these Issues to tasks. REMEMBER:
- T1 MUST be the Project Foundation & Scaffolding task (High complexity, no dependencies). It sets up the solution structure, shared interfaces, base classes, config, and DI registration so all other tasks have a clear skeleton to build upon.
- ALL other tasks should depend on T1 at minimum.
- Design tasks for PARALLEL execution: each task should own distinct files with NO overlap.
- Prefer vertical slices (one feature end-to-end) over horizontal layers.
- Maximize tasks that depend ONLY on T1 (star topology, not chains).

Output ONLY structured lines in this format:
TASK|<ID>|<IssueNumber>|<Name>|<Description>|<Complexity>|<Dependencies or NONE>|<FilePlan>

The FilePlan field should contain semicolon-separated file operations:
  CREATE:path/to/file.ext(namespace);MODIFY:path/to/existing.ext;USE:ExistingType(namespace)

Example:
TASK|T1|42|Project Foundation & Scaffolding|Create solution structure, shared models, interfaces, DI registration, and configuration|High|NONE|CREATE:.gitignore;CREATE:MyApp.sln;CREATE:MyApp/MyApp.csproj;CREATE:MyApp/Program.cs(MyApp);CREATE:MyApp/Models/AppConfig.cs(MyApp.Models)
TASK|T2|43|Implement auth module|Build JWT authentication with refresh tokens|Medium|T1|CREATE:MyApp/Services/AuthService.cs(MyApp.Services);USE:IAuthService(MyApp.Interfaces)
TASK|T3|44|Implement user profile|Build user profile CRUD|Medium|T1|CREATE:MyApp/Services/UserProfileService.cs(MyApp.Services);CREATE:MyApp/Controllers/ProfileController.cs(MyApp.Controllers)

Note how T2 and T3 both depend only on T1 (parallel-safe) and own completely separate files.

Only output TASK lines, nothing else.
