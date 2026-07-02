# VDT Status Monitoring Runbook

> **Purpose:** Scheduled monitoring instructions for validation runs (3x Local + 3x GitHub).  
> Referenced by the Copilot CLI scheduled prompt — keep this file as the single source of truth.

---

## 1. Check Logs

```powershell
# Find the latest runner log
$log = Get-ChildItem "C:\Git\VirtualDevTeam\Logs\runner-*-stdout.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $log -Tail 60 | Select-String -Pattern "(phase|error|complete|stuck|flowmonitor|warning|signal|Failed)" -CaseSensitive:$false | Select-Object -Last 20
```

## 2. Check Pipeline Status

```powershell
Invoke-RestMethod http://localhost:5050/api/pipeline/status
```

Report: phase, agent statuses (working agents + status reasons), task/PR counts.

## 3. Check FlowMonitor

```powershell
Invoke-RestMethod http://localhost:5050/api/dashboard/health/flow-monitor
```

Report any findings (severity, detector, description).

## 4. Auto-Approve Gates

If there are any pending approvals (gates enabled), auto-approve them via the dashboard API:

```powershell
$pending = Invoke-RestMethod http://localhost:5050/api/decisions/pending
# Approve each pending decision
foreach ($d in $pending) {
    Invoke-RestMethod -Method Post "http://localhost:5050/api/decisions/$($d.id)/approve"
}
```

## 5. Run Completion Handling

**If the run reached phase=Completion:**

1. Update tracking:
   ```sql
   UPDATE run_tracking SET status='done', completed_at=datetime('now') WHERE status='running';
   SELECT * FROM run_tracking;
   ```

2. Determine next action:
   - **Fewer than 3 Local runs done →** Run `.\scripts\minimal-reset.ps1` from `C:\Git\VirtualDevTeam`, wait for it, start the runner via `.\scripts\start-runner.ps1`, wait for port 5050, then `Invoke-RestMethod -Method Post http://localhost:5050/api/runs/start-project?force`, insert new `run_tracking` row.
   - **3 Local runs done, GitHub not started →** Edit `develop-settings.json` to set `useLocalDevMode=false`, then reset and start GitHub run.
   - **All 6 runs done →** Stop the schedule.

## 6. Error / Stuck Agent Handling

**If errors or stuck agents found:**

- Check FlowMonitor for auto-remediation first.
- If FlowMonitor hasn't acted, investigate the logs and try to help.

### 🚨 Process Safety Rules

- **NEVER** use `Stop-Process -Name` or `Get-Process -Name` for broad kills.
- **NEVER** kill `node`, `dotnet`, or `copilot` by name.
- **ALWAYS** use `scripts/kill-orphan-runner-procs.ps1` for orphan cleanup.
- **ALWAYS** stop the runner by PID: `Get-NetTCPConnection -LocalPort 5050 -State Listen` → `Stop-Process -Id <PID>`.

## 7. Reporting Format

Report concisely with a single status line:

```
Run 1/3 Local | Phase: Research | Agents: 6 active | FlowMonitor: clean
```

Only elaborate if there are errors, stuck agents, or phase transitions to act on.
