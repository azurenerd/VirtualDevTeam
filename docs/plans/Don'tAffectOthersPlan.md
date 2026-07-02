# Don't-Affect-Others Plan

> **Status:** ✅ IMPLEMENTED 2026-05-11. See "Implementation Notes" at the bottom for what shipped vs the original proposal.
> **Sources:** Local audit of `C:\git\VirtualDevTeam` + research on `bradygaster/squad` + .NET ecosystem patterns (`dotnet/aspnetcore`).

## Executive Summary

VirtualDevTeam has a solid foundation for multi-developer hygiene — PATs live in `dotnet user-secrets`, the largest runtime directories (`.agents/`, `Logs/`, `FixRecommendations/`, `.candidates/`, `experiment-data/`) are gitignored, and the project already uses the `data.template.json` pattern as a precedent. The core problem is a cluster of **five tracked files that are either machine-specific, runtime-mutable, or accidental**: two Node crash dumps with absolute local paths, a `preview-settings.json` with a hardcoded `C:\Clones\Compass1` path, an `sme-definitions.json` that agents write to on every run, an agent-generated markdown report that slipped in, and a broken `.gitignore` pattern for the dashboard data file. Alongside these tracking bugs, the project lacks the companion template files and first-run documentation that would let a new developer go from `git clone` to running runner in under 15 minutes without touching tracked files.

---

## Current State — What's Already Good

| Thing | Evidence |
|---|---|
| **PATs use `dotnet user-secrets`** | `Session.md:28-30` explicitly warns "NEVER PUT SECRETS/TOKENS IN `appsettings.json`"; `Session.md:62` references `dotnet user-secrets list` |
| **`develop-settings.json` gitignored** | `.gitignore:17` — per-run project config is correctly local-only |
| **`appsettings.Development.json` gitignored** | `.gitignore:20` — `**/appsettings.Development.json` pattern |
| **SQLite databases gitignored** | `.gitignore:31-34` — `*.db`, `*.db-shm`, `*.db-wal`, `*.sqlite`, `*.sqlite3` |
| **`.agents/` workspace gitignored** | `.gitignore:88-89` — all cloned target repos stay local |
| **`Logs/`, `*.log`, `*.pid` gitignored** | `.gitignore:52-55` |
| **`FixRecommendations/` gitignored** | `.gitignore:124` (recent commit `c404061`) |
| **`.candidates/`, `experiment-data/` gitignored** | `.gitignore:81-84` — strategy framework artifacts stay local |
| **Reset scripts gitignored** | `.gitignore:99-101` — `scripts/minimal-reset.ps1`, `fresh-reset.ps1`, `reset-runner.ps1` |
| **`.playwright-mcp/` gitignored** | `.gitignore:104-105` |
| **`data.template.json` pattern already exists** | Root `data.template.json` — the project already has the "commit template, gitignore the populated copy" precedent |
| **`appsettings.json` has empty-string secrets** | All secret fields (`GitHubToken`, `ApiKey`) are `""` — safe to track |

---

## Gaps — Files / Practices That DO Leak Between Developers

### Gap 1 — `preview-settings.json` tracked with machine-absolute path  🔴 HIGH

**What it is:** `src/VirtualDevTeam.Runner/preview-settings.json` is committed (SHA `8d75cd1a`). Content:

```json
{
  "clonePath": "C:\\Clones\\Compass1",
  "buildCommandOverride": "",
  "runCommandOverride": "",
  "port": 0,
  "securityWarningAcknowledged": true
}
```

**Why it leaks:** `clonePath` is a Windows absolute path to a single developer's machine. Every other developer who clones will have `git status` show this file as modified the moment they point their preview elsewhere. A `git add .` push commits their path over the shared one.

**Concrete fix:**
1. Add `preview-settings.json` to `.gitignore`
2. Commit `preview-settings.template.json` with placeholder values
3. `git rm --cached src/VirtualDevTeam.Runner/preview-settings.json`

**Rubber-duck:** If any code reads `preview-settings.json` via `File.ReadAllText` (not via the configuration system), removing it from git will cause `FileNotFoundException` for fresh clones until first-run copies the template. Verify the reader gracefully handles missing file or check for null before the copy step.

---

### Gap 2 — Node crash dumps committed to the Runner directory  🔴 HIGH

**What they are:** Two files tracked in repo:
- `src/VirtualDevTeam.Runner/report.20260410.061140.87128.0.001.json` (25 KB, SHA `a0dabc2e`)
- `src/VirtualDevTeam.Runner/report.20260410.063219.98380.0.001.json` (26 KB, SHA `87f38a0c`)

Both are Node.js `--report-on-fatalerror` OOM crash reports containing the developer's CWD (`C:\Git\VirtualDevTeam\src\VirtualDevTeam.Runner`), PIDs, memory maps, and full command-line arguments for `copilot --no-ask-user --no-auto-update ...`.

**Why it leaks:** Accidental commits — runtime diagnostic artifacts revealing machine layout and running commands. 50 KB of noise per clone.

**Concrete fix:**
1. Add `report.[0-9]*.json` to `.gitignore` (after the `*.pid` line)
2. `git rm --cached "src/VirtualDevTeam.Runner/report.20260410.061140.87128.0.001.json"`
3. `git rm --cached "src/VirtualDevTeam.Runner/report.20260410.063219.98380.0.001.json"`

**Rubber-duck:** Pattern could collide with a legit structured report named `report-{ts}.json`. Use the Node.js–specific shape `report.2[0-9][0-9][0-9][0-1][0-9]*.json` or place the gitignore entry in a scoped `src/VirtualDevTeam.Runner/.gitignore` to avoid false positives elsewhere.

---

### Gap 3 — `sme-definitions.json` is tracked but runtime-mutable  🟠 MEDIUM-HIGH

**What it is:** `src/VirtualDevTeam.Runner/sme-definitions.json` is tracked as `{}` (8 bytes, SHA `d177980a`). The runner config sets `"PersistDefinitions": true, "DefinitionsPath": "sme-definitions.json"`. After any run with SME agents, this file is populated with agent definitions specific to that project.

**Why it leaks:** Every developer accumulates their own SME definitions in this file. Permanent `git status` modification after the first run. Trap for `git commit -a` users — pushes another developer's SME catalog to the shared repo.

**Concrete fix:**
1. Add `sme-definitions.json` to `.gitignore`
2. Rename committed `{}` file to `sme-definitions.template.json` as canonical starter
3. First-run script copies template → real file if not present
4. `git rm --cached src/VirtualDevTeam.Runner/sme-definitions.json`
5. `git add src/VirtualDevTeam.Runner/sme-definitions.template.json`

**Rubber-duck:** Verify the runner creates the file cleanly if absent at startup (rather than throwing). Test `AllowAgentCreatedDefinitions: true` still works with an empty `{}` file. This is also lesson #10 in copilot-instructions.md ("Stale SME definitions auto-respawn") so the cleanup behavior already considered this file.

---

### Gap 4 — Agent-generated markdown committed to Runner directory  🟠 MEDIUM

**What it is:** `src/VirtualDevTeam.Runner/tech-stack-evaluation.md` (45 KB, SHA `abec322b`) — a full tech evaluation for a "Luxury Pool Construction Educational Website" project. Clearly runtime output from a prior run, not a project document.

**Why it leaks:** Agents write markdown to the runner's CWD. Any output that doesn't match an existing gitignore pattern gets committed.

**Concrete fix:**
1. `git rm --cached src/VirtualDevTeam.Runner/tech-stack-evaluation.md`
2. Add a scoped `src/VirtualDevTeam.Runner/.gitignore` with `*-evaluation.md`, `*-research.md`, etc.
3. **Longer-term**: route agent-output markdown to `.agents/` or a dedicated `runner-output/` directory (already-gitignored), not the Runner CWD.

**Rubber-duck:** Broad `*.md` exclusion would suppress legit docs. Use specific suffixes or constrain to Runner directory only.

---

### Gap 5 — Broken gitignore pattern for dashboard data file  🟡 MEDIUM

**What it is:** `.gitignore:76-77`:
```
# Dashboarduser data - contains project-specific info, use data.template.json as reference
src/ReportingDashboard/wwwroot/data/data.json*.pid
```

Pattern `data.json*.pid` matches `data.json.pid` (nothing useful), NOT `data.json`. Intent (per comment + existing `data.template.json`) is to ignore `data.json`. Meanwhile `src/ReportingDashboard/wwwroot/data/dashboard-data.json` is present and contains internal project references (`dev.azure.com/contoso/VirtualDevTeam/_backlogs`, `CSE Garage · Agent Squad Workstream · FY25-Q3 / Q4`).

**Why it leaks:** Malformed pattern fails to ignore what it claims. A developer who populates `data.json` accidentally commits internal milestone data.

**Concrete fix:**
1. Fix gitignore: replace `src/ReportingDashboard/wwwroot/data/data.json*.pid` with `src/ReportingDashboard/wwwroot/data/data.json`
2. Evaluate whether `dashboard-data.json` should also be gitignored or templated (contains internal team data).

**Rubber-duck:** If JS loads `data.json` by filename, fresh clones with no file will show blank dashboard until first-run copies the template. Document this.

---

### Gap 6 — No `develop-settings.template.json` companion  🟡 MEDIUM

**What it is:** `develop-settings.json` is correctly gitignored, but no committed template documents the schema. Field set can only be inferred by reading source or copying a colleague.

**Why it leaks (in reverse):** New developers don't know what the file should contain, so they guess or skip it. Onboarding friction.

**Concrete fix:** Commit `src/VirtualDevTeam.Runner/develop-settings.template.json` with all fields and `_comment` annotations (see Proposed Structure below).

---

### Gap 7 — No first-run setup documentation  🟡 MEDIUM

**What it is:** `Session.md` documents how Copilot CLI agents operate the runner — not how a fresh developer gets started. `README.md` has no "first time clone → configure → run" path.

**Concrete fix:** Add a **"First-Run Setup"** section to `README.md` (see Proposed Structure).

---

## How Squad Does It (bradygaster/squad)

Brady Gaster's Squad project (Node/TypeScript) has a different runtime model but its hygiene patterns translate directly.

**Per-machine vs. shared state split** (from [`.gitignore`](https://github.com/bradygaster/squad/blob/dev/.gitignore)):
- `.squad/config.json` — model preferences, developer-local → **gitignored**
- `.squad/sessions/`, `.squad/log/`, `.squad/.first-run`, `.squad/.watch-pids` — machine-specific runtime → **gitignored**
- `.squad/` directory itself → **committed** — agent charters, team decisions, agent history (shared team baseline)
- `squad.config.ts` → **committed** — shared team topology + model config

**Transferable patterns:**
1. **Ignore per-session runtime state, commit team schema.** VDT equivalent: gitignore `sme-definitions.json`, commit `sme-definitions.template.json`.
2. **`.first-run` marker file.** Squad writes `.squad/.first-run` on first `squad init`. VDT could write `src/VirtualDevTeam.Runner/.first-run` that `run.ps1` checks — if absent, auto-copy templates.
3. **`squad doctor` for setup validation.** Squad has a `doctor` command for prerequisite checks. VDT's `scripts/verify-setup.ps1` is the equivalent — extend it to also validate required files exist.
4. **Idempotent `init` is safe to re-run.** Squad's CONTRIBUTING.md notes "scaffold ... is idempotent — safe to run multiple times." VDT's first-run script should be too.

---

## Patterns From Broader .NET Ecosystem (aspnetcore)

`dotnet/aspnetcore` commits `appsettings.json` with safe defaults and `""` for secrets. Secrets go into `dotnet user-secrets` keyed by `UserSecretsId` GUID in `.csproj`. Environment overrides use `appsettings.{Environment}.json`, with `Development` typically gitignored.

**Key principle**: *`appsettings.json` is schema + safe defaults; user-secrets is the per-developer overlay for anything sensitive or machine-specific.* VirtualDevTeam already follows this — all secret fields in `appsettings.json` are `""`. The only gap is ensuring developers know to use `dotnet user-secrets set "VirtualDevTeam:Project:GitHubToken" "<PAT>"`, which is currently undocumented for new developers.

---

## Proposed Structure for VirtualDevTeam

### What stays tracked (no change)
```
src/VirtualDevTeam.Runner/appsettings.json                ✅ shared baseline, all secrets empty
src/VirtualDevTeam.Runner/run.ps1                         ✅ shared entry point
data.template.json                                         ✅ already correct pattern
```

### What gets committed (NEW)
```
src/VirtualDevTeam.Runner/develop-settings.template.json   ← schema starter
src/VirtualDevTeam.Runner/preview-settings.template.json   ← placeholder starter
src/VirtualDevTeam.Runner/sme-definitions.template.json    ← renamed from sme-definitions.json, content `{}`
```

### What gets gitignored (additions to `.gitignore`)
```gitignore
# Per-developer preview config (clone path, port override)
preview-settings.json

# Runtime-mutated SME agent registry — copy from sme-definitions.template.json on first run
sme-definitions.json

# Node.js crash / OOM reports generated by --report-on-fatalerror
report.[0-9]*.json

# Agent-generated documents dropped into Runner working directory
src/VirtualDevTeam.Runner/*-evaluation.md
src/VirtualDevTeam.Runner/*-research.md
```

### Fix existing broken entry
```gitignore
# BEFORE (broken — matches nothing useful):
src/ReportingDashboard/wwwroot/data/data.json*.pid

# AFTER (matches data.json as intended):
src/ReportingDashboard/wwwroot/data/data.json
```

### What goes in `dotnet user-secrets` (document in README)
```bash
cd src/VirtualDevTeam.Runner
dotnet user-secrets set "VirtualDevTeam:Project:GitHubToken"        "<GitHub PAT>"
dotnet user-secrets set "VirtualDevTeam:Models:premium:ApiKey"      "<Anthropic API Key>"
dotnet user-secrets set "VirtualDevTeam:Models:standard:ApiKey"     "<Anthropic API Key>"
dotnet user-secrets set "VirtualDevTeam:Models:budget:ApiKey"       "<OpenAI API Key>"
# If using Azure OpenAI, also set the per-tier Endpoint
```

### New First-Run Setup section for README.md

````markdown
## First-Run Setup

### Prerequisites
- .NET 8 SDK
- Node.js 20+
- GitHub CLI (`gh auth login` completed)
- Anthropic or OpenAI API keys (for agent model tiers)

### 1 — Clone and build
```bash
git clone https://github.com/azure-core/VirtualDevTeam.git
cd VirtualDevTeam
dotnet build VirtualDevTeam.sln
```

### 2 — Copy template files (gitignored — your local copies won't affect others)
```powershell
cd src/VirtualDevTeam.Runner
Copy-Item develop-settings.template.json develop-settings.json
Copy-Item preview-settings.template.json preview-settings.json
Copy-Item sme-definitions.template.json  sme-definitions.json
```
Open each and fill in your values (repo, branch, clone path).

### 3 — Store secrets in dotnet user-secrets (NEVER in any tracked file)
```bash
cd src/VirtualDevTeam.Runner
dotnet user-secrets set "VirtualDevTeam:Project:GitHubToken"    "<your-github-pat>"
dotnet user-secrets set "VirtualDevTeam:Models:premium:ApiKey"  "<your-anthropic-key>"
dotnet user-secrets set "VirtualDevTeam:Models:budget:ApiKey"   "<your-openai-key>"
```

### 4 — Start the runner
```powershell
.\run.ps1
```
Dashboard at http://localhost:5050
````

---

## Migration Plan

| Step | Action | Risk |
|---|---|---|
| 1 | Fix broken `.gitignore` entry for `data.json` | 🟢 Zero — gitignore fix only |
| 2 | Add `report.[0-9]*.json` to `.gitignore` | 🟢 Zero — new pattern |
| 3 | Add `preview-settings.json` to `.gitignore` | 🟢 Low — stops tracking |
| 4 | Add `sme-definitions.json` to `.gitignore` | 🟢 Low — stops tracking |
| 5 | Add agent-output markdown patterns to `.gitignore` | 🟢 Low — targeted |
| 6 | Commit `preview-settings.template.json` | 🟢 Zero — new file |
| 7 | Rename `sme-definitions.json` → `sme-definitions.template.json` | 🟡 Medium — verify config `"DefinitionsPath": "sme-definitions.json"` is satisfied after copy |
| 8 | `git rm --cached` the two crash reports + old `preview-settings.json` + `sme-definitions.json` + `tech-stack-evaluation.md` | 🟡 Medium — files remain on disk locally; other devs see them in `git status` after pull |
| 9 | Commit `develop-settings.template.json` | 🟢 Zero — new file |
| 10 | Add First-Run Setup section to `README.md` | 🟢 Zero — docs only |
| 11 | Update `run.ps1` or `scripts/verify-setup.ps1` to validate required files + helpful errors (or auto-copy templates) | 🟡 Medium — behavior change; test that auto-copy doesn't overwrite an existing file |
| 12 | Coordinate with active developers — they should `git rm --cached` locally or accept the pull cleanly | 🟡 Medium — comms / process |

---

## Open Questions for the User

1. **`sme-definitions.json` runtime behavior** — Does the runner tolerate a missing file at startup (creating one fresh), or does it require the file to exist? If required, the first-run copy step is mandatory and `run.ps1` must enforce it.

2. **`preview-settings.json` — who creates it?** — Is this populated by the Dashboard UI wizard, or manually by the developer? If the wizard creates it on first launch, the first-run template copy can be skipped and the Dashboard can check for absence instead.

3. **Should `appsettings.json` model defaults change?** — Currently hardcodes Anthropic Claude models. Developers using only OpenAI or Ollama have to edit `appsettings.json` (which they might commit) or learn `develop-settings.json` overrides. Provider-neutral placeholders, or keep Anthropic as the team default?

4. **`dashboard-data.json` — project data or sample data?** — `src/ReportingDashboard/wwwroot/data/dashboard-data.json` contains specific internal milestone dates and ADO URLs. Intentional sample (keep tracked) or real operational data that leaked (gitignore + template)?

5. **`tech-stack-evaluation.md` — one-off or pattern?** — Known accidental commit OK to `git rm`, or actually referenced by runner/dashboard? Confirm nothing reads from this path before removing.

6. **Scope of agent-output gitignore** — All `*.md` files in `src/VirtualDevTeam.Runner/` (clean break) or only specific suffixes (`*-evaluation.md`, `*-research.md`)? Former is cleaner but requires moving any intentional Runner-directory docs elsewhere.

---

*Plan generated 2026-05-10. Run the migration only after the user has reviewed and answered the open questions above.*

---

## Implementation Notes (shipped 2026-05-11)

All 7 gaps addressed. README override per user direction emphasises CLI/MCP-server auth over PATs.

### What changed
- **`.gitignore`** — fixed broken `data.json*.pid` → `data.json`; added `preview-settings.json`, `sme-definitions.json`, `report.[0-9]*.json`, plus scoped `src/VirtualDevTeam.Runner/*-evaluation.md` / `*-research.md` / `*-report.md` for agent-generated docs
- **Template files committed** — `develop-settings.template.json`, `preview-settings.template.json`, `sme-definitions.template.json` (all with `_comment` fields documenting use)
- **Untracked** (`git rm --cached`, kept locally) — `preview-settings.json`, `sme-definitions.json`, `tech-stack-evaluation.md`, two `report.*.json` crash dumps
- **README.md** — new step 2a "First-Run Setup — Local Config Files" + rewritten step 3 "Authentication — Use CLI / MCP, NOT PATs"

### Auth section override (per user direction)
The proposed README section in this plan suggested `dotnet user-secrets set "...:GitHubToken"`. **Per user instruction**, the shipped README instead leads with:
1. **GitHub auth = `gh auth login`** — uses the existing `GhCliAuthProvider` (already the default `AuthMethod` in `appsettings.json`); nothing stored on disk
2. **Azure DevOps auth = `az login`** — uses the existing `AzureCliBearerProvider`; auto-refreshes 5 min before expiry; nothing stored on disk
3. **Copilot CLI agents = GitHub MCP + Azure DevOps MCP servers** — wired automatically through `copilot --allow-all`; reuses `gh`/`az` sessions; no PAT exchange
4. **PATs are documented as the fallback path only**, with explicit warnings about user-secrets leaking between users on a shared machine and not auto-rotating

### Resolutions for the 6 open questions (deferred deeper investigation)
| Q | Resolution |
|---|---|
| Q1 SME runtime behavior | `SMEAgentDefinitionService.LoadCustomDefinitionsAsync` already short-circuits on missing file (returns empty dict) — no first-run copy step required; template committed for documentation only |
| Q2 preview-settings.json creator | `PreviewBuildService.LoadSettingsAsync` returns `new PreviewSettings()` on missing file — Dashboard UI populates on first save; template committed for documentation only |
| Q3 appsettings.json model defaults | Kept Anthropic — Copilot CLI is the default provider; direct API keys are optional fallback only |
| Q4 dashboard-data.json | Left as-is (working sample data). Templating is a separate cleanup; not in scope of this batch |
| Q5 tech-stack-evaluation.md | Confirmed unreferenced via grep — safely `git rm --cached` |
| Q6 agent-output gitignore scope | Specific suffixes (`*-evaluation.md`, `*-research.md`, `*-report.md`) — narrower than blanket `*.md` to avoid blocking legitimate Runner-directory docs |
