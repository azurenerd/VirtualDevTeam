---
version: "1.0"
description: "Per-scenario user-message prompt. Given a scenario YAML and app handle, asks the LLM to produce a deterministic JSON action plan for the IPlaytestAdapter to execute."
variables:
  - scenario_yaml
  - app_base_url
  - app_handle
  - prior_trace_evidence
  - playtest_context_json
tags:
  - playtester
  - user
  - action-plan
  - scenario
---

## Scenario to verify

```yaml
{{scenario_yaml}}
```

## Live application handle

- Base URL / handle: `{{app_base_url}}`
- PlaytestContext (adapter config, DB connection string, CLI binary path): `{{playtest_context_json}}`

## Prior trace evidence (from earlier scenarios in this run, if any)

```json
{{prior_trace_evidence}}
```

Use the prior trace only to understand application state (e.g., data seeded by S01 that S02 depends on). Do not use it to skip steps.

---

## Your task

Produce the **exact, deterministic action plan** the `IPlaytestAdapter` will execute to verify this scenario. The plan must:

1. Cover every step listed in the scenario's `steps` array — in order.
2. Include an explicit assertion action for **every entry** in the scenario's `observation_surfaces` array. Do not skip any surface even if it seems redundant.
3. Use the correct action type for the scenario's `journey_kind` (see Action Type Reference below).
4. Include a screenshot action at the final step for all `ui_interaction` scenarios.

## Action Type Reference

```
# UI actions (ui_interaction scenarios — Playwright)
page.goto(url)
page.click(selector)
page.fill(selector, value)
page.waitForSelector(selector, options?)
page.waitForURL(urlPattern)
page.evaluate(jsExpression)            # Read DOM value / fire JS event
page.screenshot(filename)

# Assertion actions (ui_interaction)
assert.selectorExists(selector)
assert.selectorText(selector, expectedText)
assert.selectorChanged(selector, snapshotKey)   # compare vs named snapshot
assert.eventFired(eventName)                    # from intercepted EventBus

# API / HTTP actions (api_call, webhook scenarios)
http.post(path, bodyJson, headers?)
http.get(path, headers?)
http.assertStatus(expectedStatus, maxLatencyMs?)
http.assertBodyPath(jsonPath, expectedValue)

# DB assertion actions (api_call, webhook, cli_invocation scenarios)
db.query(sql)
db.assertRow(sql, expectedJson)
db.assertCount(sql, expectedCount)

# CLI actions (cli_invocation scenarios)
cli.run(binary, args, stdinData?)
cli.assertExitCode(expected)
cli.assertStdout(regexPattern)
cli.assertStderr(regexPattern)

# Generic
wait.ms(milliseconds)                  # explicit delay — use sparingly
log.snapshot(label)                    # capture current console/log state for Layer-3 evidence
```

## Output format

Return **only** valid JSON — no markdown fences, no prose. Schema:

```json
{
  "scenario_id": "<id from scenario>",
  "journey_kind": "<kind>",
  "adapter": "WebPlaytestAdapter | ApiPlaytestAdapter | CliPlaytestAdapter",
  "precondition_check": "<single assertion or null — verifies preconditions are met before execution>",
  "actions": [
    {
      "step_index": 0,
      "scenario_step": "<exact text from scenario steps[0]>",
      "action_type": "<action type from reference above>",
      "params": { "<key>": "<value>" },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": "<observation_surfaces[N].kind or null if this action is not a surface assertion>"
    }
  ],
  "terminal_assertions": [
    {
      "surface_index": 0,
      "surface_kind": "<kind from observation_surfaces>",
      "action_type": "<assertion action>",
      "params": { "<key>": "<value>" }
    }
  ],
  "final_screenshot": "s<id>_final.png"
}
```

`terminal_assertions` must contain one entry for **every** `observation_surfaces` entry in the scenario.

---

## Worked examples

### Example 1 — `ui_interaction` (Tower-defense: player builds first tower)

**Input scenario YAML:**

```yaml
id: S03
title: "Player builds first tower"
journey_kind: ui_interaction
actor: "Player"
trigger: "User clicks 'Build Tower' button after landing on game screen"
preconditions:
  - "S01 has completed (game has started)"
  - "Player has ≥ 100 gold"
steps:
  - "1. Player clicks on empty tile in playfield"
  - "2. Tower placement preview appears"
  - "3. Player clicks 'Confirm'"
  - "4. Tower sprite renders at chosen tile"
  - "5. Gold counter decreases by tower cost"
  - "6. Tower begins targeting nearest enemy in range"
expected_terminal_state:
  - "DOM contains <tower-sprite> at clicked tile coordinates"
  - "Gold counter element shows new value"
  - "EventBus has fired 'tower:placed' event"
observation_surfaces:
  - kind: dom_query
    selector: ".tower-sprite[data-tile='5,7']"
  - kind: dom_text
    selector: ".hud-gold"
    expected_change: "decreased_by_cost"
  - kind: event_bus
    event_name: "tower:placed"
priority: critical
status: approved
```

**Expected JSON action plan output:**

```json
{
  "scenario_id": "S03",
  "journey_kind": "ui_interaction",
  "adapter": "WebPlaytestAdapter",
  "precondition_check": "assert.selectorExists('.hud-gold')",
  "actions": [
    {
      "step_index": 0,
      "scenario_step": "1. Player clicks on empty tile in playfield",
      "action_type": "page.evaluate",
      "params": { "expression": "document.querySelector('.hud-gold').textContent" },
      "captures_snapshot": true,
      "snapshot_key": "gold_before",
      "surface_verified": null
    },
    {
      "step_index": 1,
      "scenario_step": "1. Player clicks on empty tile in playfield",
      "action_type": "page.click",
      "params": { "selector": ".playfield-tile[data-tile='5,7']" },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": null
    },
    {
      "step_index": 2,
      "scenario_step": "2. Tower placement preview appears",
      "action_type": "page.waitForSelector",
      "params": { "selector": ".tower-preview", "timeout": 2000 },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": null
    },
    {
      "step_index": 3,
      "scenario_step": "3. Player clicks 'Confirm'",
      "action_type": "page.click",
      "params": { "selector": "button.confirm-placement" },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": null
    },
    {
      "step_index": 4,
      "scenario_step": "4. Tower sprite renders at chosen tile",
      "action_type": "page.waitForSelector",
      "params": { "selector": ".tower-sprite[data-tile='5,7']", "timeout": 3000 },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": "dom_query"
    },
    {
      "step_index": 5,
      "scenario_step": "5. Gold counter decreases by tower cost",
      "action_type": "assert.selectorChanged",
      "params": { "selector": ".hud-gold", "snapshotKey": "gold_before" },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": "dom_text"
    },
    {
      "step_index": 6,
      "scenario_step": "6. Tower begins targeting nearest enemy in range",
      "action_type": "page.evaluate",
      "params": { "expression": "window.__playtestEventLog && window.__playtestEventLog.includes('tower:placed')" },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": "event_bus"
    },
    {
      "step_index": 7,
      "scenario_step": "final screenshot",
      "action_type": "page.screenshot",
      "params": { "filename": "s03_final.png" },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": null
    }
  ],
  "terminal_assertions": [
    {
      "surface_index": 0,
      "surface_kind": "dom_query",
      "action_type": "assert.selectorExists",
      "params": { "selector": ".tower-sprite[data-tile='5,7']" }
    },
    {
      "surface_index": 1,
      "surface_kind": "dom_text",
      "action_type": "assert.selectorChanged",
      "params": { "selector": ".hud-gold", "snapshotKey": "gold_before", "direction": "decreased" }
    },
    {
      "surface_index": 2,
      "surface_kind": "event_bus",
      "action_type": "assert.eventFired",
      "params": { "eventName": "tower:placed" }
    }
  ],
  "final_screenshot": "s03_final.png"
}
```

---

### Example 2 — `api_call` (REST API: Stripe webhook marks invoice paid)

**Input scenario YAML:**

```yaml
id: S08
title: "Stripe webhook marks invoice paid"
journey_kind: webhook
actor: "Stripe webhook (charge.succeeded event)"
trigger: "POST /webhooks/stripe with charge.succeeded payload and valid signature"
preconditions:
  - "Invoice INV-123 exists in 'pending' state"
  - "Customer has stripe_customer_id linked"
steps:
  - "1. Stripe POSTs charge.succeeded payload to /webhooks/stripe"
  - "2. Service validates Stripe-Signature header"
  - "3. Service looks up invoice by charge metadata"
  - "4. Service transitions invoice from 'pending' to 'paid'"
  - "5. Service emits invoice.paid domain event"
  - "6. Service responds 200 OK to Stripe within 5s"
expected_terminal_state:
  - "HTTP response: 200 OK within 5000ms"
  - "DB row: invoices WHERE id='INV-123' has status='paid' and paid_at IS NOT NULL"
  - "Domain event invoice.paid published to the message bus"
observation_surfaces:
  - kind: http_response
    status: 200
    max_latency_ms: 5000
  - kind: db_row
    query: "SELECT status, paid_at FROM invoices WHERE id='INV-123'"
    expected: { status: "paid", paid_at: "not_null" }
  - kind: queue_message
    topic: "invoice.events"
    event_type: "invoice.paid"
priority: critical
status: approved
```

**Expected JSON action plan output:**

```json
{
  "scenario_id": "S08",
  "journey_kind": "webhook",
  "adapter": "ApiPlaytestAdapter",
  "precondition_check": "db.assertRow(\"SELECT status FROM invoices WHERE id='INV-123'\", {\"status\": \"pending\"})",
  "actions": [
    {
      "step_index": 0,
      "scenario_step": "1. Stripe POSTs charge.succeeded payload to /webhooks/stripe",
      "action_type": "http.post",
      "params": {
        "path": "/webhooks/stripe",
        "bodyJson": { "type": "charge.succeeded", "data": { "object": { "metadata": { "invoice_id": "INV-123" } } } },
        "headers": { "Stripe-Signature": "__test_signature__" }
      },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": null
    },
    {
      "step_index": 1,
      "scenario_step": "6. Service responds 200 OK to Stripe within 5s",
      "action_type": "http.assertStatus",
      "params": { "expectedStatus": 200, "maxLatencyMs": 5000 },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": "http_response"
    },
    {
      "step_index": 2,
      "scenario_step": "4. Service transitions invoice from 'pending' to 'paid'",
      "action_type": "db.assertRow",
      "params": {
        "sql": "SELECT status, paid_at FROM invoices WHERE id='INV-123'",
        "expectedJson": { "status": "paid", "paid_at": "not_null" }
      },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": "db_row"
    },
    {
      "step_index": 3,
      "scenario_step": "5. Service emits invoice.paid domain event",
      "action_type": "http.get",
      "params": {
        "path": "/api/test-support/queue-messages?topic=invoice.events&event_type=invoice.paid",
        "headers": {}
      },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": "queue_message"
    }
  ],
  "terminal_assertions": [
    {
      "surface_index": 0,
      "surface_kind": "http_response",
      "action_type": "http.assertStatus",
      "params": { "expectedStatus": 200, "maxLatencyMs": 5000 }
    },
    {
      "surface_index": 1,
      "surface_kind": "db_row",
      "action_type": "db.assertRow",
      "params": {
        "sql": "SELECT status, paid_at FROM invoices WHERE id='INV-123'",
        "expectedJson": { "status": "paid", "paid_at": "not_null" }
      }
    },
    {
      "surface_index": 2,
      "surface_kind": "queue_message",
      "action_type": "http.assertBodyPath",
      "params": { "jsonPath": "$.messages[0].event_type", "expectedValue": "invoice.paid" }
    }
  ],
  "final_screenshot": null
}
```

---

### Example 3 — `cli_invocation` (CLI tool: operator uploads CSV)

**Input scenario YAML:**

```yaml
id: S04
title: "Operator uploads CSV via CLI"
journey_kind: cli_invocation
actor: "CLI user (admin role)"
trigger: "myapp upload --file=customers.csv --tenant=acme"
steps:
  - "1. CLI parses arguments"
  - "2. CLI authenticates against API using stored credentials"
  - "3. CLI streams file to /uploads endpoint in 1MB chunks"
  - "4. Server validates schema, persists rows"
  - "5. CLI prints progress bar"
  - "6. CLI exits with code 0 and summary line"
expected_terminal_state:
  - "Exit code: 0"
  - "Stdout contains: 'Uploaded N rows successfully'"
  - "DB has N new customer rows under tenant=acme"
observation_surfaces:
  - kind: process_exit_code
    expected: 0
  - kind: stdout_pattern
    regex: "Uploaded \\d+ rows successfully"
  - kind: db_count
    query: "SELECT COUNT(*) FROM customers WHERE tenant='acme'"
    expected_change: "+N"
priority: critical
status: approved
```

**Expected JSON action plan output:**

```json
{
  "scenario_id": "S04",
  "journey_kind": "cli_invocation",
  "adapter": "CliPlaytestAdapter",
  "precondition_check": "db.assertCount(\"SELECT COUNT(*) FROM customers WHERE tenant='acme'\", 0)",
  "actions": [
    {
      "step_index": 0,
      "scenario_step": "1–6. Full CLI invocation",
      "action_type": "cli.run",
      "params": {
        "binary": "myapp",
        "args": ["upload", "--file=customers.csv", "--tenant=acme"],
        "stdinData": null
      },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": null
    },
    {
      "step_index": 1,
      "scenario_step": "6. CLI exits with code 0",
      "action_type": "cli.assertExitCode",
      "params": { "expected": 0 },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": "process_exit_code"
    },
    {
      "step_index": 2,
      "scenario_step": "5–6. CLI prints summary line",
      "action_type": "cli.assertStdout",
      "params": { "regexPattern": "Uploaded \\d+ rows successfully" },
      "captures_snapshot": false,
      "snapshot_key": null,
      "surface_verified": "stdout_pattern"
    },
    {
      "step_index": 3,
      "scenario_step": "4. Server validates schema, persists rows",
      "action_type": "db.query",
      "params": { "sql": "SELECT COUNT(*) AS row_count FROM customers WHERE tenant='acme'" },
      "captures_snapshot": true,
      "snapshot_key": "customer_count_after",
      "surface_verified": "db_count"
    }
  ],
  "terminal_assertions": [
    {
      "surface_index": 0,
      "surface_kind": "process_exit_code",
      "action_type": "cli.assertExitCode",
      "params": { "expected": 0 }
    },
    {
      "surface_index": 1,
      "surface_kind": "stdout_pattern",
      "action_type": "cli.assertStdout",
      "params": { "regexPattern": "Uploaded \\d+ rows successfully" }
    },
    {
      "surface_index": 2,
      "surface_kind": "db_count",
      "action_type": "db.assertCount",
      "params": {
        "sql": "SELECT COUNT(*) FROM customers WHERE tenant='acme'",
        "expectedChange": "+N",
        "snapshotKey": "customer_count_after"
      }
    }
  ],
  "final_screenshot": null
}
```

---

Now produce the action plan for the scenario provided at the top of this message.

Return only valid JSON. No markdown fences. No prose.
