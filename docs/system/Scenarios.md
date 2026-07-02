# Dashboard Scenario Test Results

> **Generated:** 2026-04-15
> **Dashboard:** http://localhost:5050 (embedded in Runner)
> **Runner:** http://localhost:5050 (single process)
> **Browser:** Chromium (Playwright 1.52.0)
> **Resolution:** 1920×1080

## Summary

| Status | Count |
|--------|-------|
| ✅ Passed | 11 |
| ❌ Failed | 0 |
| **Total** | **10** |

## Scenarios

### S01: Agent Overview (`/`)
**Status:** ✅ PASSED

Validates: Agent cards visible, page has content, dark theme renders.

![S01 Agent Overview](scenario-screenshots/S01_AgentOverview.png)

---

### S02: Pull Requests (`/pullrequests`)
**Status:** ✅ PASSED

Validates: PR cards display, state filters (Open/Closed/All), PR-related content present.

![S02 Pull Requests](scenario-screenshots/S02_PullRequests.png)

---

### S03: Issues (`/issues`)
**Status:** ✅ PASSED

Validates: Issue cards display, state filters, issue-related content present.

![S03 Issues](scenario-screenshots/S03_Issues.png)

---

### S04: Agent Reasoning (`/reasoning`)
**Status:** ✅ PASSED

Validates: Reasoning log page renders with content.

![S04 Reasoning](scenario-screenshots/S04_Reasoning.png)

---

### S05: Project Timeline (`/timeline`)
**Status:** ✅ PASSED

Validates: Timeline groups render, page has content.

![S05 Timeline](scenario-screenshots/S05_Timeline.png)

---

### S06: Configuration (`/configuration`)
**Status:** ✅ PASSED

Validates: Configuration page has agent sections, settings, and config-related content.

![S06 Configuration](scenario-screenshots/S06_Configuration.png)

---

### S08: Health Monitor (`/health`)
**Status:** ✅ PASSED

Validates: Health status page renders with content.

![S08 Health Monitor](scenario-screenshots/S08_HealthMonitor.png)

---

### S09: Metrics (`/metrics`)
**Status:** ✅ PASSED

Validates: Metrics page loads successfully with build/test metric cards.

![S09 Metrics](scenario-screenshots/S09_Metrics.png)

---

### S10: Team Visualization (`/team`)
**Status:** ✅ PASSED

Validates: Team visualization page renders with content.

![S10 Team Viz](scenario-screenshots/S10_TeamViz.png)

---

### S11: Approvals (`/approvals`)
**Status:** ✅ PASSED

Validates: Approval gates page renders with content.

![S11 Approvals](scenario-screenshots/S11_Approvals.png)

---

## Test Infrastructure

- **Test Project:** `tests/VirtualDevTeam.Dashboard.Tests/`
- **NuGet:** `Microsoft.Playwright 1.52.0`
- **Browser:** Chromium (non-headless, 1920×1080)
- **Video:** Recorded per browser context (`.webm` format)
- **Screenshots:** Full-page PNG captures

### How to Run

```bash
# Ensure runner (port 5050) is running — dashboard is embedded
dotnet test tests/VirtualDevTeam.Dashboard.Tests

# Run a single scenario
dotnet test tests/VirtualDevTeam.Dashboard.Tests --filter "S01"
```

### Known Limitations

- **Metrics page** (`/metrics`) now properly registers `BuildTestMetrics` in DI and returns 200.
- **Engineering Plan page** was removed (duplicate of Timeline). Agent Reasoning page (`/reasoning`) is tested instead.
- Tests require the Runner (port 5050) to be running — it serves both the API and dashboard UI.
