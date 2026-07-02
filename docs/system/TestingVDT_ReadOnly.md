# VDT Dashboard Read-Only Test Scenarios

> **Purpose:** Comprehensive list of every read-only UI scenario in the VirtualDevTeam Dashboard.
> Used by the automated Playwright test harness that runs every 2 minutes during active pipeline runs.
> 
> **This file is checked into git and should NOT be modified with run-specific results.**
> Run results are stored in `.testing/{runId}/` (gitignored).

---

## How It Works

1. A scheduled Playwright harness runs every 2 minutes during active runs
2. Each scenario is tested: page loads, content verified, links checked
3. Results written to `.testing/{runId}/results-{timestamp}.json`
4. Failures are flagged with root cause investigation notes

---

## Scenario Categories

### 1. Overview Page (`/`)

| ID | Scenario | Verification |
|----|----------|-------------|
| OV-01 | Page loads without error | HTTP 200, no "An error has occurred" text |
| OV-02 | Project banner shows repo name | Text contains configured repo name |
| OV-03 | Project banner shows branch info | Text contains working branch name |
| OV-04 | Agent cards render for all agents | Card count matches expected agent count |
| OV-05 | Agent status badges correct | Each card has Working/Idle/Blocked/Error badge |
| OV-06 | Summary stats match cards | Total/Idle/Working/Blocked counts consistent |
| OV-07 | Agent card click navigates to detail | Clicking card navigates to `/agent/{id}` |
| OV-08 | Cost badge in header shows value | `$X.XX` format visible |
| OV-09 | API calls badge shows count | `N calls` visible |

### 2. Develop Wizard (`/develop`)

| ID | Scenario | Verification |
|----|----------|-------------|
| DV-01 | Page loads without error | HTTP 200, content > 1000 chars |
| DV-02 | Platform selector visible | GitHub/ADO radio buttons present |
| DV-03 | LDP toggle visible (GitHub only) | "Local Dev Mode" toggle present when GitHub selected |
| DV-04 | LDP toggle state matches config | Toggle checked/unchecked matches develop-settings.json |
| DV-05 | Description field populated | Project description text visible |
| DV-06 | Repo field shows configured repo | owner/repo format visible |

### 3. Project Timeline (`/timeline`)

| ID | Scenario | Verification |
|----|----------|-------------|
| TL-01 | Page loads without error | HTTP 200, content > 1000 chars |
| TL-02 | Phase events render | At least one phase event visible |
| TL-03 | Document events show | Research.md/PMSpec.md/Architecture.md creation events |
| TL-04 | PR events show | PR created/merged events with numbers |
| TL-05 | Timestamps present | Each event has a timestamp |
| TL-06 | Agent names shown | Events attributed to agent names |

### 4. Repository — Code Tab (`/repository`, `/repository/files`)

| ID | Scenario | Verification |
|----|----------|-------------|
| RC-01 | Code tab loads | Page loads, file tree visible |
| RC-02 | File count badge correct | Badge shows number > 0 |
| RC-03 | Branch name shown | Working branch name in header |
| RC-04 | File tree entries are individual | Each file on its own line (not concatenated) |
| RC-05 | Clicking a .md file shows content | Navigate to .md → markdown rendered |
| RC-06 | AgentDocs folder navigable | Can browse into AgentDocs/ |
| RC-07 | Research.md viewable after merge | Content loads with markdown preview |
| RC-08 | PMSpec.md viewable after merge | Content loads with markdown preview |
| RC-09 | Architecture.md viewable after merge | Content loads with markdown preview |

### 5. Repository — Pull Requests Tab (`/repository/pulls`)

| ID | Scenario | Verification |
|----|----------|-------------|
| RP-01 | PR list loads | Page loads, PRs visible |
| RP-02 | PR titles shown | Each PR has a title with agent name |
| RP-03 | PR status badges | Open/Merged/Closed badges visible |
| RP-04 | PR labels shown | Labels like `approved`, `ready-for-review` |
| RP-05 | PR branch info | Head → Base branch shown |
| RP-06 | Clicking PR navigates to detail | Click → `/repository/pull-request/{n}` |
| RP-07 | External links hidden in LDP | No broken "↗" buttons with empty URLs |

### 6. Repository — Issues Tab (`/repository/issues`)

| ID | Scenario | Verification |
|----|----------|-------------|
| RI-01 | Issue list loads | Page loads, issues visible |
| RI-02 | Issue titles shown | Engineering task titles visible |
| RI-03 | Issue status badges | Open/Closed badges |
| RI-04 | Issue labels shown | `engineering-task`, `enhancement` labels |
| RI-05 | Clicking issue navigates to detail | Click → `/repository/issue/{n}` |

### 7. PR Detail Page (`/repository/pull-request/{n}`)

| ID | Scenario | Verification |
|----|----------|-------------|
| PD-01 | Page loads without hanging | Content renders within 5 seconds |
| PD-02 | PR title and status shown | Title, MERGED/OPEN badge |
| PD-03 | Branch info shown | Head → Base branch |
| PD-04 | Labels shown | Label badges rendered |
| PD-05 | Description tab has content | PR body markdown rendered |
| PD-06 | Files tab shows changed files | File list matches what PR introduced |
| PD-07 | Files tab shows actual docs (not just tracking) | For doc PRs: Research.md, PMSpec.md, Architecture.md listed |
| PD-08 | Comments tab accessible | Can switch to Comments tab |
| PD-09 | Reviews tab accessible | Can switch to Reviews tab |
| PD-10 | "Open in Local" button correct | Shows "Local" not "GitHub" in LDP mode |

### 8. Issue Detail Page (`/repository/issue/{n}`)

| ID | Scenario | Verification |
|----|----------|-------------|
| ID-01 | Page loads without hanging | Content renders within 5 seconds |
| ID-02 | Issue title shown | Title with task ID |
| ID-03 | Issue body rendered | Markdown body with acceptance criteria |
| ID-04 | Labels shown | Correct labels |
| ID-05 | Comments visible | Agent comments if any |

### 9. Approvals Page (`/approvals`)

| ID | Scenario | Verification |
|----|----------|-------------|
| AP-01 | Page loads without error | Content renders, no circuit crash |
| AP-02 | Pending gates shown | Gate cards with names and context |
| AP-03 | Review button links to document | PMSpec/Architecture gates → file viewer |
| AP-04 | Review button doesn't crash circuit | Click Review → page navigates, no hang |
| AP-05 | Approve/Reject buttons present | Action buttons visible on gate cards |
| AP-06 | Resolved tab shows history | Past approvals listed |
| AP-07 | Navigation works after visiting | Can navigate to other pages without force reload |

### 10. Configuration Page (`/configuration`)

| ID | Scenario | Verification |
|----|----------|-------------|
| CF-01 | Page loads without hanging | Content renders within 10 seconds |
| CF-02 | Copilot CLI section loads | Toggle + settings visible |
| CF-03 | LDP toggle in Workspace section | "Local Dev Mode" toggle with description |
| CF-04 | LDP toggle state correct | Matches develop-settings.json |
| CF-05 | Navigation works after visiting | Can navigate to strategies etc. without crash |

### 11. Strategies Page (`/strategies`)

| ID | Scenario | Verification |
|----|----------|-------------|
| ST-01 | Page loads without error | Content renders |
| ST-02 | Active/completed sections visible | Strategy framework status shown |
| ST-03 | Strategy cards render when active | During T-FINAL: candidate cards visible |

### 12. Reasoning Page (`/reasoning`)

| ID | Scenario | Verification |
|----|----------|-------------|
| RE-01 | Page loads | Content renders |
| RE-02 | Decision entries visible | Agent decisions/memories listed when present |

### 13. Scenarios Page (`/scenarios`)

| ID | Scenario | Verification |
|----|----------|-------------|
| SC-01 | Page loads | Content renders |
| SC-02 | Scenario cards show | Generated scenarios from PMSpec visible |
| SC-03 | Verification status shown | Status badges on scenarios |

### 14. Metrics Page (`/metrics`)

| ID | Scenario | Verification |
|----|----------|-------------|
| ME-01 | Page loads | Content renders |
| ME-02 | Cost data shown | Estimated cost, AI calls visible |
| ME-03 | Agent usage breakdown | Per-agent metrics |

### 15. Flow Monitor (`/flow-monitor`)

| ID | Scenario | Verification |
|----|----------|-------------|
| FM-01 | Page loads | Content renders |
| FM-02 | Findings shown when present | Detector findings visible |
| FM-03 | Severity filters work | Can filter by severity |

### 16. Cross-Page Navigation

| ID | Scenario | Verification |
|----|----------|-------------|
| NAV-01 | Overview → Approvals → Strategies | All 3 pages load in sequence |
| NAV-02 | Configuration → any page | No circuit crash after config page |
| NAV-03 | PR detail → back to PR list | Back navigation works |
| NAV-04 | Issue detail → back to issue list | Back navigation works |
| NAV-05 | Code browser drill-down and back | Navigate into folder, back to root |
| NAV-06 | All nav sidebar links work | Each sidebar link loads its page |

---

## Result Storage

Results are stored in `.testing/{runId}/` (gitignored):
```
.testing/
├── {runId}/
│   ├── results-{timestamp}.json    # Full scenario results
│   ├── screenshots/                # Page screenshots on failure
│   └── summary.md                  # Human-readable summary
```

Each result entry:
```json
{
  "id": "OV-01",
  "status": "PASS|FAIL|SKIP",
  "timestamp": "2026-05-19T12:00:00Z",
  "pageUrl": "/",
  "contentLength": 12345,
  "errorMessage": null,
  "screenshot": null
}
```
