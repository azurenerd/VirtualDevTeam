# Azure DevOps Platform Setup Guide

This guide explains how to configure AgentSquad to use Azure DevOps (ADO) as the underlying platform for pull requests, work items, code hosting, and reviews.

## Prerequisites

- An Azure DevOps organization with a Git repository
- One of the following for authentication:
  - **PAT (Personal Access Token)** — simplest option, works everywhere
  - **Azure CLI bearer token** — for enterprises where PATs are restricted (e.g., Microsoft)

### PAT Scopes Required

If using a PAT, create one at `https://dev.azure.com/{org}/_usersSettings/tokens` with these scopes:
- **Code** — Read & Write
- **Work Items** — Read, Write & Manage
- **Pull Request Threads** — Read & Write
- **Build** — Read (for future CI integration)

### Azure CLI Bearer Token Setup

For enterprises using managed identities or restricted PAT policies:

```bash
# One-time login with your tenant
az login --tenant <your-tenant-id> --allow-no-subscriptions

# Token is auto-refreshed by AgentSquad — no manual steps after login
```

AgentSquad uses the ADO resource ID (`499b84ac-1321-427f-aa17-267ca6975798`) and auto-refreshes tokens 5 minutes before expiry.

## Configuration

### Dashboard UI

1. Open the Configuration page (`http://localhost:5051/configuration`)
2. In the **Project** card, change **Dev Platform** dropdown from `GitHub` to `Azure DevOps`
3. Fill in the ADO-specific fields that appear:
   - **ADO Organization** — your org name (e.g., `contoso` for `https://dev.azure.com/contoso`)
   - **ADO Project** — the project containing the repo
   - **ADO Repository** — the Git repository name
   - **Authentication** — choose PAT or Azure CLI Bearer
   - **Default Work Item Type** — typically `Task` (maps to GitHub Issues)
   - **Executive Work Item Type** — typically `User Story` or `Feature` (maps to executive requests)
4. Click **Save**

### appsettings.json

Alternatively, configure directly in `appsettings.json`:

```json
{
  "AgentSquad": {
    "DevPlatform": {
      "Platform": "AzureDevOps",
      "AuthMethod": "Pat",
      "AzureDevOps": {
        "Organization": "contoso",
        "Project": "MyProject",
        "Repository": "my-repo",
        "Pat": "your-ado-pat-here",
        "DefaultBranch": "main",
        "DefaultWorkItemType": "Task",
        "ExecutiveWorkItemType": "User Story",
        "StateMappings": {
          "Open": "New",
          "InProgress": "Active",
          "Blocked": "Active",
          "Resolved": "Closed"
        }
      }
    }
  }
}
```

For Azure CLI bearer token auth:

```json
{
  "AgentSquad": {
    "DevPlatform": {
      "Platform": "AzureDevOps",
      "AuthMethod": "AzureCliBearer",
      "AzureDevOps": {
        "Organization": "contoso",
        "Project": "MyProject",
        "Repository": "my-repo",
        "TenantId": "72f988bf-86f1-41af-91ab-2d7cd011db47",
        "DefaultBranch": "main"
      }
    }
  }
}
```

## Concept Mapping

| AgentSquad Concept | GitHub | Azure DevOps |
|---|---|---|
| Work Items | Issues | Work Items (Task, Bug, User Story) |
| Code Reviews | Pull Requests | Pull Requests |
| Review Comments | PR Review Threads | PR Comment Threads |
| Labels | Issue/PR Labels | Work Item Tags |
| Branches | Git Branches | Git Branches (refs) |
| File Operations | Git Trees API | Git Pushes API |
| Authentication | PAT (token) | PAT (Basic) or Bearer |

## Work Item State Mappings

ADO uses customizable states per work item type. The `StateMappings` config maps AgentSquad's internal states to your ADO process template states:

| AgentSquad State | Default ADO State | Description |
|---|---|---|
| `Open` | `New` | Newly created, not yet started |
| `InProgress` | `Active` | Agent is working on it |
| `Blocked` | `Active` | Agent is blocked (uses same ADO state) |
| `Resolved` | `Closed` | Work complete |

Override these in `StateMappings` if your process template uses different state names (e.g., Scrum uses "To Do" / "In Progress" / "Done").

## Key Differences from GitHub

1. **Work item deletion** — ADO supports permanent deletion via `DELETE ...?destroy=true` (requires PAT with "Work Items: Read, Write & Manage" scope). Falls back to closing if permissions are insufficient. PRs cannot be deleted on either platform — only abandoned/closed.
2. **Work item hierarchy** — ADO supports parent/child relationships natively. AgentSquad leverages this for task decomposition.
3. **WIQL queries** — Work item filtering uses ADO's SQL-like query language internally.
4. **Rate limiting** — ADO uses `X-RateLimit-*` headers. AgentSquad tracks these and backs off automatically.
5. **File commits** — Each file operation is an ADO Push (with RefUpdates + Changes) rather than a Git Tree update.
6. **Comment API version** — Work item comments require `api-version=7.1-preview` (not `7.1`). Returns 400 without the `-preview` suffix.
7. **PR description limit** — ADO enforces a hard 4000-character limit on PR descriptions. AgentSquad auto-truncates with a suffix note.
8. **HTML descriptions** — ADO Work Item Description field renders HTML, not Markdown. AgentSquad auto-converts Markdown to HTML via Markdig before writing.
9. **Work item types** — ADO uses "User Story" (not "Enhancement") for PM-created stories. Configured via `ExecutiveWorkItemType` setting.

## Lessons Learned from ADO End-to-End Runs

### Workspace Clone URLs
Agent workspaces (C:\Agents4\) must clone from the ADO repository URL, not GitHub. The `GetGitCloneUrl()` helper on `AgentSquadConfig` returns the correct URL based on `DevPlatform.Platform`.

### Premature Work Item Closure
In SinglePRMode with no parent-child work item links, the PM must check for open engineering tasks (by label) before closing User Stories. Old merged PRs from previous runs can trigger false "all work complete" signals.

### Dashboard Platform Awareness
Dashboard pages use `IPlatformHostContext` via `DashboardDataService.GetPullRequestUrl()` and `GetWorkItemUrl()` to construct platform-correct URLs. The `PlatformName` property dynamically returns "GitHub" or "Azure DevOps".

### Mini-Reset Procedure
To clean up and restart an ADO run:
1. Stop runner and dashboard processes
2. Delete agent workspace folders (`C:\Agents4\*`)
3. Delete state DB (`agentsquad.db`)
4. Abandon active PRs via ADO REST API
5. Delete or remove work items from previous run
6. Delete agent branches (`refs/heads/agent/*`)
7. Restart runner and dashboard

### Known Remaining GitHub References
Some non-critical paths still contain GitHub-specific code (review thread replies via GitHub API, raw.githubusercontent.com screenshot URLs, GateNotificationService URLs). These are documented in `future_fixes` and don't block ADO end-to-end runs.

## Architecture

The platform abstraction uses 7 capability interfaces:

```
IPullRequestService    — PR CRUD, merging, file diffs, commits
IWorkItemService       — Work item CRUD, hierarchy, dependencies, queries
IRepositoryContentService — File read/write, commits, tree operations
IBranchService         — Branch create/delete/exists/list
IReviewService         — Review comments, inline comments, threads
IPlatformInfoService   — Rate limits, capabilities, platform identity
IPlatformHostContext   — URL patterns (clone URL, web URLs)
```

When `Platform = AzureDevOps`, the DI container registers ADO implementations for each interface. When `Platform = GitHub`, it registers GitHub adapter classes that wrap the existing `IGitHubService`. The rest of AgentSquad code works against the interfaces — no platform-specific logic leaks into agents, orchestrator, or dashboard.

## Switching Back to GitHub

Change `DevPlatform.Platform` back to `GitHub` in the dashboard or appsettings.json. All existing GitHub configuration (`Project.GitHubRepo`, `Project.GitHubToken`) is preserved — the two platform configs are independent.
