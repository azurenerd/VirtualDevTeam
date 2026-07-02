# In-Place Mode — Working on Existing Repositories

> **Status:** Implemented — available in the Develop wizard and Configuration page.

## Overview

In-Place mode lets VirtualDevTeam work on your **existing** repository checkout — no cloning needed. This is ideal for:
- **Large monorepos** (100+ GB) where cloning takes hours
- **Enterprise repos** with complex setup (GVFS, LFS, custom tooling)
- **Pre-configured environments** where build tools are already installed
- **Quick iteration** when you want to test VDT on a project that's already set up

VDT **never modifies your working tree**. Each agent gets a lightweight git worktree branched from your `.git` directory.

## Quick Start

1. Open the Dashboard at `http://localhost:5050`
2. Go to **Configuration** → **Workspace**
3. Set **Workspace Mode** to **In-Place**
4. Set **Existing Checkout Path** to your repo (e.g., `C:\src\BigProject`)
5. (Optional) Set **Worktree Root** for where agent worktrees are created
6. Start a new project via the **Develop** wizard

## Three Workspace Modes

| | Clone (default) | Worktree | In-Place |
|---|---|---|---|
| **How it works** | Full `git clone` per agent | One shared clone + worktrees | Your existing checkout + worktrees |
| **Disk usage** | N × repo size | 1 × repo + small worktrees | 0 extra (you already have it) |
| **Init time** | Minutes per agent | Seconds per worktree | Instant |
| **Agent isolation** | Full directory isolation | Branch isolation | Branch isolation |
| **Your working tree** | Never touched | Never touched | Never touched |

## Configuration

### In appsettings.json

```json
{
  "VirtualDevTeam": {
    "Workspace": {
      "WorkspaceMode": "InPlace",
      "LocalCheckoutPath": "C:\\src\\BigProject",
      "WorktreeRoot": "C:\\src\\.vdt-worktrees",
      "SparseCheckoutPaths": ["src/services/auth", "src/shared"],
      "RequireCleanHostTree": true
    }
  }
}
```

### In develop-settings.json

```json
{
  "workspaceMode": "InPlace",
  "existingRepoPath": "C:\\src\\BigProject",
  "worktreeRoot": "C:\\src\\.vdt-worktrees",
  "sparseCheckoutPaths": ["src/", "build/"]
}
```

## Safety Guarantees

1. **Your working tree is never modified** — all agent work happens in worktrees
2. **Marker file protection** — every VDT worktree has a `.vdt-worktree-id` file; destructive operations refuse to run on unmarked directories
3. **Clean tree check** — by default, VDT refuses to start if you have uncommitted changes (set `RequireCleanHostTree: false` to override)
4. **Serialized git operations** — `SharedCloneManager` uses a `SemaphoreSlim` to prevent `.git/config.lock` races between agents

## Service Registry (Large Projects)

For monorepos with multiple services, define them in the configuration:

```json
{
  "VirtualDevTeam": {
    "LargeProject": {
      "Enabled": true,
      "Services": [
        {
          "Name": "auth-api",
          "Path": "src/services/auth",
          "BuildCommand": "dotnet build Auth.csproj",
          "TestCommand": "dotnet test Auth.Tests.csproj",
          "Port": 5001,
          "HealthUrl": "http://localhost:5001/health",
          "UseExistingDevServer": true,
          "ExpertiseTags": ["dotnet", "auth"]
        },
        {
          "Name": "frontend",
          "Path": "src/web",
          "BuildCommand": "npm run build",
          "TestCommand": "npm test",
          "Port": 3000,
          "UseExistingDevServer": true,
          "ExpertiseTags": ["typescript", "react"]
        }
      ]
    }
  }
}
```

### External Dev Server

When `UseExistingDevServer: true`, VDT connects to your already-running dev server instead of launching one. This is the recommended approach for large projects where startup is complex.

## Cleanup

Run the cleanup script to remove orphaned worktrees:

```powershell
pwsh -File scripts/cleanup-orphan-worktrees.ps1 -WhatIf  # preview
pwsh -File scripts/cleanup-orphan-worktrees.ps1           # clean up
```

## Sparse Checkout

For large repos, use sparse checkout to limit what's materialized in each worktree:

```json
"SparseCheckoutPaths": ["src/services/auth", "src/shared", "build"]
```

Root build files (`*.sln`, `Directory.Build.props`, `global.json`, `package.json`) are always included automatically.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| "InPlace checkout has uncommitted changes" | Your working tree has local changes | `git stash` or set `RequireCleanHostTree: false` |
| `.git/config.lock` errors | Concurrent worktree operations | VDT serializes these; if persists, delete the lock file manually |
| "InPlace path is not a git repository" | Wrong path configured | Verify the path contains a `.git` directory |
| Worktrees accumulate on disk | Orphaned from crashed sessions | Run `cleanup-orphan-worktrees.ps1` |
