# Playwright-Driven Iterative Fix & Assessment Protocol

> **Purpose:** This document defines the standard operating procedure for implementing, validating, and iterating on UI fixes and features using Playwright-based visual assessment. Every change goes through this loop until acceptance criteria are fully met.

---

## The Iterative Fix Loop

```
┌─────────────────────────────────────────────────────────┐
│  1. IMPLEMENT — Make the code change                    │
│  2. DEFINE — Write acceptance criteria + test scenarios  │
│  3. LAUNCH — Build, deploy, start the app               │
│  4. ASSESS — Run Playwright against each scenario        │
│  5. QUESTION — Ask 5+ validation questions about the fix │
│  6. REPORT — Document what works and what doesn't        │
│  7. TODO — Create fix items for gaps found               │
│  8. ITERATE — Fix gaps, go back to step 3               │
│  9. DONE — All scenarios pass, no more improvements      │
└─────────────────────────────────────────────────────────┘
```

If the UI state isn't visible yet (e.g., waiting for pipeline progress), set up a **scheduled check** at an appropriate interval:
- **Every 1 min** — Active UI interaction expected imminently (button click, page load)
- **Every 5 min** — Waiting for a short pipeline phase (PR review, test generation)
- **Every 10 min** — Waiting for a medium pipeline phase (implementation, plan generation)
- **Every 30 min** — Waiting for a long pipeline phase (full end-to-end run)

---

## Step-by-Step Protocol

### Step 1: Implement
- Make the code change (edit files)
- Build to verify compilation (`dotnet build` for individual projects if runner is active)
- Commit to working branch

### Step 2: Define Acceptance Criteria
For every fix, write:

**Acceptance Criteria** — Clear, testable statements:
```
AC-1: [Description of expected behavior]
AC-2: [Description of expected behavior]
...
```

**Test Scenarios** — Robust scenarios covering:
- **Happy path** — Normal use case
- **Edge cases** — Empty states, single items, boundary values
- **Interaction** — Click, hover, scroll, toggle behaviors
- **Visual** — Colors, spacing, alignment, responsiveness
- **Regression** — Existing features still work after the change
- **Theme compatibility** — All themes render correctly (Default, Cards, Metro, Blueprint)

### Step 3: Launch
- Stop runner if rebuild needed (`Stop-Process -Id <PID>`)
- Rebuild (`dotnet build`)
- Start runner from correct directory (`Start-Process` from `src/VirtualDevTeam.Runner`)
- Wait for startup confirmation (check port 5050)

If no rebuild needed (CSS-only, template-only), the running app picks up changes on refresh.

### Step 4: Assess with Playwright — CRITICALLY, Not Superficially

> 🚨 **The #1 failure mode is superficial assessment** — confirming elements EXIST without checking if they're CORRECT. "Badge present? ✅" is NOT validation. "Badge shows 38h for a 1h run? ❌" IS validation. Every extracted value must be checked against what you KNOW about the system state.

Write a Playwright script that:
1. Navigates to the page under test
2. Takes **full-page screenshots** of each scenario
3. Extracts **text content** from key elements (badges, labels, counts)
4. Performs **interactions** (click, toggle, scroll, hover)
5. Captures **before and after** states
6. Logs findings to console

**When viewing screenshots, apply these checks:**
- **Plausibility**: Are the values reasonable given what you know? (e.g., if the run started 30 min ago, a "38h" total is wrong)
- **Consistency**: Do child values sum to parent values? Are there duplicates?
- **Completeness**: Are any columns/sections missing that should be there? Are there empty columns that shouldn't be there?
- **Visual defects**: Overlapping text, duplicate badges, cut-off labels, wrong colors
- **Data source**: Is the data coming from the right source? (e.g., run-scoped PRs vs all historical PRs)

Save screenshots with descriptive names:
```
Logs/assess-{feature}-{iteration}-{scenario}.png
```

Example script structure:
```javascript
const { chromium } = require('playwright');
(async () => {
    const browser = await chromium.launch();
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
    
    // Scenario 1: Happy path
    await page.goto('http://localhost:5050/timeline');
    await page.waitForTimeout(3000);
    // ... interact, screenshot, extract text
    
    // Scenario 2: Edge case
    // ... 
    
    await browser.close();
})();
```

### Step 5: Ask 5+ Validation Questions — With Plausibility Checks

> 🚨 **Every question must include a plausibility check.** Don't just ask "does X show a value?" — ask "does X show a CORRECT value given what I know about the system?" Compare extracted values against known pipeline state, timestamps, and expected ranges.

After viewing screenshots and extracted data, ask:

1. **Plausibility**: Is [value] reasonable for the current pipeline state? (e.g., "Total time is 45m — the run started at 2:28 PM and it's now 3:13 PM, so ~45m is correct")
2. **Consistency**: Do the child items' durations sum to the parent's duration? Are there duplicate badges on the same element?
3. **Correctness**: Does [specific element] show the value derived from the right data source? (e.g., run-scoped PRs, not all historical PRs)
4. **Completeness**: Are all expected phases/columns/nodes present? Are any ghost/empty elements showing that shouldn't be?
5. **Interaction**: Does [click/toggle/scroll] produce the expected result without visual artifacts?

**For each question, state your reasoning:**
- What value did you extract?
- What value did you expect and why?
- Do they match?

Answer each question with ✅ PASS, ⚠️ PARTIAL, or ❌ FAIL with evidence AND reasoning.

### Step 6: Report
For each iteration, produce a compact report:

```
## Assessment Iteration N

### Acceptance Criteria Status
- AC-1: ✅ PASS — [evidence]
- AC-2: ❌ FAIL — [what's wrong]

### Scenario Results
| Scenario | Result | Notes |
|----------|--------|-------|
| Happy path | ✅ | Works as expected |
| Edge case | ⚠️ | Partial — [detail] |

### Validation Questions
1. Q: ... A: ✅
2. Q: ... A: ❌ [detail]

### Issues Found
- Issue 1: [description] — Priority: HIGH/MED/LOW
- Issue 2: [description] — Priority: HIGH/MED/LOW
```

### Step 7: Create TODOs
For each issue found, create a TODO with:
- Clear description of what to fix
- Which acceptance criteria it maps to
- Priority (HIGH = blocks acceptance, MED = degrades quality, LOW = polish)

### Step 8: Iterate
Fix the TODOs, rebuild/restart if needed, re-run Playwright assessment.
Continue until all acceptance criteria pass and no more improvements are identified.

### Step 9: Done
When all scenarios pass:
- Take final screenshots as evidence
- Report final assessment to user
- Clean up temp files (Playwright scripts, intermediate screenshots)

---

## Scheduled Monitoring for Deferred Validation

When a fix can't be validated immediately (e.g., waiting for pipeline to produce data):

1. Note what you're waiting for and the expected timeframe
2. Set up a `manage_schedule` prompt at the appropriate interval
3. The scheduled prompt should:
   - Take a Playwright screenshot of the relevant page
   - Check if the expected state is now visible
   - If YES: run the full assessment, stop the schedule
   - If NO: report status and continue waiting
4. Include the acceptance criteria in the scheduled prompt so you remember what to check

Example:
```
manage_schedule create --interval "5m" --prompt "Check timeline Time View for PR timing data. Take Playwright screenshot of http://localhost:5050/timeline in Time View mode. Look for: (1) Dev W0/W1 columns have duration badges, (2) PR nodes show elapsed time > 0, (3) sub-activity nodes show real durations. If all present, run full assessment and stop this schedule."
```

---

## Anti-Patterns to Avoid

- ❌ **Existence-only checks** — "Badge present? ✅" is NOT validation. Always check if the VALUE is correct, not just that the element renders. A badge showing "38h" for a 30-minute run is a bug even though it "exists."
- ❌ **Accepting implausible values** — If you know the pipeline started 30 min ago and the UI shows "38h total", that's a FAIL, not a PASS. Always cross-reference displayed values against known system state.
- ❌ **Ignoring duplicates** — If a column shows two badges where it should show one, that's a visual defect. Count elements, don't just confirm "at least one exists."
- ❌ **Ship without seeing** — Never claim a fix works without Playwright visual evidence
- ❌ **Single-scenario validation** — Always test happy path + edge cases + regression
- ❌ **Skipping iteration** — If issues are found, fix them before reporting done
- ❌ **Manual-only checks** — Always use Playwright for reproducible evidence
- ❌ **Ignoring themes** — Test at least Default + one other theme per feature
- ❌ **Stale screenshots** — Always timestamp screenshots; don't reuse old ones
- ❌ **Vague acceptance criteria** — Each criterion must be testable with a specific check
- ❌ **Not reasoning about data** — For every extracted value, ask: "Is this plausible? Where does this data come from? Is it the right source?"

---

*Created: 2026-05-17. This protocol applies to all UI fixes and features in VirtualDevTeam.*
