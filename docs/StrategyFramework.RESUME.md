# Strategy Framework — Resume State (snapshot)

This file is the canonical hand-off record between sessions. It mirrors the
`todos` SQL table from the session that built the Phase 0 + Phase 1 foundation,
plus the dependency edges. Re-import into a new session's SQL db on resume.

## Quick stats

- Done: 32 / 56 (Phase 1 ✅, Phase 2 ✅, CLI→MCP bridge ✅, McpEnhancedStrategy ✅)
- Pending: 24
- Solution build: ✅ clean
- Tests: ✅ 728 / 728 (was 606/616 — 10 Dashboard E2E env failures are now passing in current runs)
- **End-to-end pipeline: ✅ VALIDATED (2026-04-20 02:51)** — PM review → SE merge → T4 spawned 3 strategies → **agentic-delegation selected as winner** (79 tool-calls, 287s wall) → shipped to PR #2071.

## Live end-to-end evidence (2026-04-20 run `ts=20260420-024248`)

**T1 (PR #2070)**: PM approved after restart ("No new commits since last PM review — approving to unblock") → SE merged → task T-2059 marked Done.

**T4 (PR #2071)** — full Strategy Framework cycle:

```
Orchestrating 3 strategies for task T4: baseline, mcp-enhanced, agentic-delegation
Created worktree .../T4/baseline-4d6109e1
Created worktree .../T4/mcp-enhanced-29118183
Created worktree .../T4/agentic-delegation-554379e6
BaselineCodeGenerator wrote 3/3 file(s) for task T4
BaselineCodeGenerator wrote 2/2 file(s) for task T4
AgenticDelegationStrategy succeeded for task T4 (tool-calls: 79, wall: 287.4571532s)
Persisted agentic session log: experiment-data/20260420T075125Z-T4-agentic.log
Wrote experiment record: run=20260420T074258Z task=T4 winner=agentic-delegation
Strategy framework shipped winner agentic-delegation for task T4 on PR #2071
```

Answer to the long-standing question "has the agentic third option ever finished with correct output yet?" — **YES, as of this run.**

## Session commits (2026-04-20 evening)

Applied in order on top of `fde623f`:

- `54ecc47` — fix(agentic): diff against base SHA so committed changes show up (the original empty-patch bug)
- `3f31c6d` — fix(se): don't re-review PRs the strategy already shipped
- `fde623f` — fix(se): parallel-progression HashSet (`_pastImplementationPrs`)
- `1f27af3` — fix(pm): HEAD-SHA-keyed review blacklist (re-review when SE pushes fixes)
- `063f3cf` — fix(pm): bypass comment-marker gate when HEAD SHA is known-new
- `771f4a9` — fix(pm): drop comment-marker gate; rely on HEAD SHA + pm-approved label
- `c9a5f7d` — diag: PM main-loop step logging (made the stale-DLL bug findable)
- `3b75647` — fix(se): restore PR ownership from GitHub on restart so late CHANGES REQUESTED is handled

## Gotchas uncovered this session

- **Stale DLL copies**: `dotnet build src/AgentSquad.Agents/...` does **not** refresh `src/AgentSquad.Runner/bin/Debug/net8.0/AgentSquad.Agents.dll`. Always build the Runner project (or the whole solution) so referenced DLLs are copied into the host's bin. Symptom: code changes appear to do nothing at runtime.
- **Worktree cleanup flakiness on Windows**: `git worktree remove` can fail with "locked" even after the process exits; fallback directory delete sometimes also fails. Non-fatal — next task uses a fresh hash-suffixed dir.

## Resolved follow-ups

- **`agentic-empty-patch`** — ✅ RESOLVED (commit `54ecc47`, 2026-04-20)
  - **Root cause**: the agentic `copilot --allow-all` session runs
    `git add -A && git commit` itself during its tool use to checkpoint its
    progress. After the session ended, `git diff HEAD` inside the worktree was
    empty — the agent's work was already committed — producing the
    `patchSizeBytes=0 / failureReason=empty patch` bug that prevented
    agentic-delegation from ever winning a task.
  - **Fix**: `WorktreeHandle` now carries the base SHA, and `ExtractPatchAsync`
    diffs against that base SHA instead of HEAD. This uniformly captures
    changes whether the strategy committed them, staged them, or left them
    untracked.
  - **Also**: `.sandbox/` (agentic CLI scaffolding: HOME/APPDATA/LOCALAPPDATA
    overrides with deep node_modules paths that blew past Windows MAX_PATH)
    now excluded via the worktree-local `info/exclude` — robust against the
    user's global `core.excludesfile` already listing `.sandbox` (which
    previously broke pathspec-exclude with "paths are ignored by one of your
    .gitignore files").
  - **Validation**: live val-e2e T1 run `20260420T061242Z` →
    `agentic-delegation: succeeded=true, patchSizeBytes=35377`,
    `winnerStrategyId=agentic-delegation`, shipped as PR #2070. 183/183
    strategy tests green, 728/728 total.

## Critical-path follow-up order

1. ~~**`p1-se-integration`**~~ — done
2. ~~`p1-baseline-contract`~~ — done
3. ~~Remaining `p1-test-*` cases~~ — done
4. ~~`rd-post-p1` rubber-duck checkpoint~~ — done
5. ~~**Phase 2a** (MCP server + config writer + tests)~~ — done
6. ~~**`p2-cli-mcp-bridge`** (ArgumentList refactor + scoped invocation context + prompt flip)~~ — done
7. ~~**`p2-mcp-strategy`** — `McpEnhancedStrategy`~~ — done
8. **Phase 3** (agentic delegation + sandbox + watchdog ext + semaphore split). ← NEXT
9. `rd-post-p3` rubber-duck checkpoint.
10. **Phase 4** (dashboard `/strategies` + REST + SignalR + standalone proxies).
11. **Phase 5** (adaptive selector + sampling + cost-budget UI).
12. **Validation** (`val-unit-tests` → `val-integration` → `val-e2e` → `val-success-criteria`).

## Phase 2b closeout notes (McpEnhancedStrategy — this session)

Landed this session:

- **`src/AgentSquad.Core/Strategies/McpEnhancedStrategy.cs`** (id=`mcp-enhanced`). Delegates
  to the shared `IBaselineCodeGenerator` but wraps the generator call in a scoped
  `AgentCallContext.PushInvocationContext(...)` carrying inline MCP config JSON,
  `--allow-tool=workspace-reader`, and `OverrideWorkingDirectory` = candidate worktree.
  Scope is torn down before patch extraction runs.
- **`src/AgentSquad.Core/Mcp/IMcpServerLocator.cs`** — abstraction + `DefaultMcpServerLocator`.
  Resolution order: `StrategyFrameworkConfig.McpServerDllPath` (explicit), then
  `AppContext.BaseDirectory/AgentSquad.McpServer.dll` (production shape, alongside the host),
  then dev-tree probe via repo-root + `src/AgentSquad.McpServer/bin/<cfg>/net8.0/...`.
  Verifies `.runtimeconfig.json` sidecar before returning a spec — framework-dependent
  .NET apps need it to launch via `dotnet <dll>`.
- **`StrategyFrameworkConfig.McpServerDllPath`** — optional absolute-path override; blank
  by default so probe logic kicks in. Recommended to set explicitly in production.
- **`src/AgentSquad.Runner/AgentSquad.Runner.csproj`** — project reference + explicit
  `Copy` target that drops `AgentSquad.McpServer.dll` + `.runtimeconfig.json` + `.deps.json`
  into the Runner output dir, so the `beside` probe succeeds without config overrides.
- **`IBaselineCodeGenerator.GenerateAsync`** gained an optional `strategyTag` param
  (default `"baseline-strategy"`). `McpEnhancedStrategy` passes `"mcp-enhanced-strategy"`
  so the kernel's per-agent id reads honestly in cost/telemetry, rather than reusing
  baseline's hardcoded tag.
- **`ModelRegistry._kernelCache` + `_cliFallbackTiers`** now lock-protected. Parallel
  strategy candidates (`Task.WhenAll(baseline, mcp-enhanced)`) previously raced on an
  unsynchronized `Dictionary` — would have caused duplicate kernel builds at best,
  undefined behavior at worst. New `_kernelCacheLock` + a "did another thread win?"
  re-check after build prevents both.
- **DI wiring** — `DefaultMcpServerLocator` and `McpEnhancedStrategy` registered in
  `StrategyFrameworkServiceCollectionExtensions.AddStrategyFramework`. Still gated by
  master `StrategyFrameworkConfig.Enabled` (false by default).

Test additions (`tests/AgentSquad.StrategyFramework.Tests/`):

- `McpEnhancedStrategyTests.cs` — 10 tests: scope install/teardown; async-boundary
  AsyncLocal flow; scope restore on throw/cancel; locator failure = hard failure (no
  silent baseline-shaped fallback); missing-generator = honest failure (no stub marker
  written, unlike baseline); parallel-flow isolation (two candidates on `Task.WhenAll`
  see independent contexts); strategy-id correctness; generator receives
  `"mcp-enhanced-strategy"` tag.
- `DefaultMcpServerLocatorTests.cs` — 3 tests: explicit path w/ sidecar resolves;
  missing sidecar does NOT return the broken path; missing-everywhere throws with
  probe paths in the message.

Design decisions informed by pre-implementation rubber-duck critique:
- **Allow-tool format**: emit `--allow-tool=workspace-reader` (server name). Matches
  existing convention in `CopilotCliBridgeArgumentsTests`. Tool-level scoping
  (`server:tool`) deferred until validated against live CLI behaviour.
- **CWD policy**: candidate worktree (matches baseline generator's file-write CWD).
  `--root` on the server arg anchors the scope redundantly.
- **Rollout gating**: relies on existing `StrategyFrameworkConfig.Enabled=false`
  master switch + `EnabledStrategies` whitelist. No new flag needed.
- **Concurrency**: no per-strategy cap added. The existing
  `StrategyConcurrencyGate.GlobalMaxConcurrentProcesses` (6) + CopilotCli
  `MaxConcurrentRequests` (5) provide sufficient backpressure. Revisit if real
  runs show overload.



Rubber-duck critique before landing Phase 2 flagged that naïve `McpEnhancedStrategy`
would be behaviorally indistinguishable from baseline because:

- `CopilotCliProcessManager.BuildArguments()` never emits `--mcp-config`.
- `CopilotCliChatCompletionService` flattens chat history into a single prompt that
  explicitly says *"Do NOT use any tools or shell commands."*

So Phase 2 was split: **2a = infrastructure only** (this session); **2b = bridge +
strategy** (new `p2-cli-mcp-bridge` todo now blocks `p2-mcp-strategy`).

Landed in 2a:
- `src/AgentSquad.McpServer/` — standalone stdio JSON-RPC server (init, tools/list,
  tools/call, ping). Tools: `read_file`, `list_directory`, `search_code`. Hardened:
  reparse-point ancestor + recursion guards, UNC/device/drive-qualified/control-char
  rejection, `Regex.MatchTimeout=2s`, 1 MB file cap, 10 k file cap, 500 match cap,
  binary-file probe-skip, skip `.git`/`node_modules`/`bin`/`obj`.
- `src/AgentSquad.Core/Mcp/McpConfigWriter.cs` — pure utility. `BuildConfig` + atomic
  `WriteScopedConfig`. Throws if output path lands inside the candidate worktree (or
  any caller-provided forbidden root) — prevents `git add -A` pulling the control-plane
  file into the candidate patch.
- Tests: 7 subprocess-driven protocol/path-safety tests + 6 writer tests (including
  concurrent 8-candidate race test covering `p2-test-mcp-race`).

NOT landed (deferred to bridge phase):
- DI registration of `mcp-enhanced` strategy (would otherwise surface a non-working
  strategy id).
- `read_csproj` tool (InteractiveCLIPlan listed it; explicitly dropped for now).
- Any prompt augmentation — without the bridge, it would be placebo and would
  contradict the existing `Do NOT use any tools` instruction.

### Bridge phase (`p2-cli-mcp-bridge`) — DONE

Landed this session after rubber-duck critique (which flagged 3-separate-AsyncLocals
drift risk + silent `AdditionalArgs` tokenisation regression + prompt-narration risk).

Implementation:

- **`CopilotCliInvocationContext`** (new record) — single immutable object carrying
  `AdditionalMcpConfigJson`, `AllowedMcpTools`, `OverrideWorkingDirectory`. Derives
  `AllowToolUsage` from `AllowedMcpTools?.Count > 0` rather than a separate flag,
  making it structurally impossible for prompt and CLI args to disagree.
- **`AgentCallContext.PushInvocationContext(ctx)`** — returns `IDisposable` that
  restores the prior value. Prevents cross-request state bleed that a raw setter
  would allow. Nested-scope safe.
- **`CopilotCliProcessManager.BuildArguments`** — returns `IReadOnlyList<string>`;
  caller populates `ProcessStartInfo.ArgumentList` (not `Arguments` string). Inline
  JSON survives as a single argv entry with no Windows escaping. Reads the ambient
  invocation context to emit `--additional-mcp-config <json>` and
  `--allow-tool=<name>` entries.
- **`_config.WorkingDirectory`** still honoured, but per-invocation
  `OverrideWorkingDirectory` takes precedence.
- **Legacy `CopilotCli.AdditionalArgs` string** preserved for back-compat. If it
  contains quote or escape characters that cannot be faithfully tokenised without
  shell semantics, `BuildArguments` now THROWS with a clear migration pointer to
  the new `CopilotCli.AdditionalArgList: string[]` config field. Quote-free
  values pass through as whitespace-split tokens — no silent reinterpretation.
- **`CopilotCliChatCompletionService.FormatChatHistoryAsPrompt`** — rule #2 of
  the output-format header flips based on the same context. Strict-by-default:
  *"Do NOT use any tools or shell commands."* When tools are allowed, the rule
  becomes *"You MAY silently call the configured read-only MCP tools … Do NOT
  narrate tool calls, inspection steps, or intermediate actions … Do NOT create,
  edit, or write files. Do NOT run shell commands."*
- **17 new unit tests** (82 total in StrategyFramework suite) covering: default
  argv shape; model/output-format/excluded-tools pairings; inline JSON preservation;
  multi-tool emission; context-scope restore on dispose; nested scopes; empty
  allow-list does NOT relax prompt; parallel-flow isolation (AsyncLocal); legacy
  `AdditionalArgs` quote-rejection; `AdditionalArgList` pass-through; prompt flip
  +  anti-narration clause presence.

Caller pattern (for `p2-mcp-strategy` to adopt):

```csharp
var json = McpConfigWriter.BuildConfig("workspace-reader", "dotnet", args).ToJsonString();
using var _ = AgentCallContext.PushInvocationContext(new CopilotCliInvocationContext(
    AdditionalMcpConfigJson: json,
    AllowedMcpTools: new[] { "workspace-reader" },
    OverrideWorkingDirectory: candidateWorktreeRoot));

var result = await kernel.InvokePromptAsync(prompt, ct);
// On scope exit: next call on the same async flow sees no MCP flags / strict prompt.
```

## Phase 1 closeout notes (rd-post-p1)

Post-Phase 1 rubber-duck critique surfaced 8 findings. Resolution:

- **Fixed:** nested `.git` path segment bypass in `GitWorktreeManager.ValidatePatchPaths` (now rejects any segment `== ".git"`, not just root).
- **Fixed:** pre-existing reparse-point/symlink bypass in `BaselineCodeGenerator` — added `ContainsReparsePoint` ancestor walk that fails-closed before writing through any junction/symlink that pre-dates the candidate.
- **Deferred to Phase 2+** (tracked here, not blockers for MCP work):
  - Remote-head race between orchestration and `PushAsync` — requires carrying expected remote SHA through winner-apply path and using explicit `--force-with-lease=ref:sha`. Touches SE push plumbing.
  - `StrategyConcurrencyGate` degrade flag not nest-safe — matters only when 2+ parallel backoff callers exist (Phase 2 concern).
  - `SkipJudgeOnSingleSurvivor` config is exposed but evaluator always skips on sole survivor. Tighten before Phase 2 adds telemetry on judge calls.
  - LlmJudge 2000-char floor can overflow total prompt for very large candidate sets. Revisit when Phase 2 adds MCP-driven candidate expansion.
  - Baseline vs legacy single-pass tier mismatch (config tier vs `Identity.ModelTier`) — only visible if the two drift; add parity test when convenient.
  - `ExperimentTracker` append-atomicity vs crash — make readers tolerate truncated last NDJSON line.

## Pending todos (status='pending', 36 rows)

| id | title | depends on |
|---|---|---|
| p1-baseline-contract | Baseline regression = stubbed-parity + behavioral | p1-baseline-strategy |
| p1-evaluator-llm-scoring | Implement LLM scoring for survivors | p1-evaluator-hard-gates |
| p1-se-integration | Wire StrategyOrchestrator into SoftwareEngineerAgent | p1-baseline-strategy, p1-evaluator-llm-scoring, p1-experiment-tracker |
| p1-test-backoff | Test: 429 triggers backoff and degrade | p1-global-cap |
| p1-test-bad-judge-json | Test: malformed judge output handled deterministically | p1-judge-hardening |
| p1-test-binary-patch | Test: winner apply handles binary/rename/delete patches | p1-se-integration |
| p1-test-head-change | Test: PR head changed mid-flight | p1-head-change-apply |
| p1-test-reserved-path | Test: candidate touching reserved evaluator path fails Gate2 | p1-reserved-path |
| p1-test-symlink-escape | Test: symlink/junction/absolute-path escape rejected | p1-worktree-manager |
| p2-mcp-strategy | Implement McpEnhancedStrategy | p2-cli-mcp-bridge | **DONE** this session
| p3-agentic-strategy | Implement AgenticDelegationStrategy | p3-agentic-watchdog, p3-semaphore-split |
| p3-agentic-watchdog | Extend CliInteractiveWatchdog for agentic mode | p3-process-manager-ext |
| p3-process-manager-ext | Extend CopilotCliProcessManager with agentic session method | rd-post-p1 |
| p3-real-sandbox | Real sandbox for agentic | p3-agentic-strategy |
| p3-sandbox-hardening | File-escape detection and push blocking | p3-agentic-strategy |
| p3-semaphore-split | Split semaphores into three pools | p3-process-manager-ext |
| p3-test-orphan-cleanup | Test: agentic crash leaves no orphaned worktree/process | p3-real-sandbox |
| p4-candidate-state-api | REST endpoints for candidate state | p1-se-integration |
| p4-dashboard-page | Blazor /strategies page with side-by-side cards | p4-signalr-events |
| p4-dashboard-stubs | Standalone dashboard HTTP proxies for new endpoints | p4-candidate-state-api |
| p4-signalr-events | SignalR events for candidate lifecycle | p4-candidate-state-api |
| p5-adaptive-selector | Implement AdaptiveStrategySelector | val-e2e |
| p5-cost-budget | Per-run token budget circuit breaker | p3-agentic-strategy |
| p5-sampling-policies | Implement non-always sampling policies | p1-se-integration |
| p6-telemetry | Per-strategy cost attribution in AgentUsageTracker | p3-agentic-strategy |
| rd-post-p1 | Rubber-duck after Phase 1 design | p1-se-integration |
| rd-post-p3 | Rubber-duck after Phase 3 agentic work | p3-sandbox-hardening |
| val-e2e | End-to-end live pipeline run | p4-dashboard-page, val-integration |
| val-integration | Run integration tests | val-unit-tests |
| val-success-criteria | Validate success criteria from doc | val-e2e |
| val-unit-tests | Run unit tests | p2-server-lifetime, p5-cost-budget |

## Done todos (status='done', 31 rows)

p0-config-model, p0-gitignore, p0-register-di, p0-tests-project,
p1-baseline-strategy, p1-baseline-contract, p1-contract-freeze,
p1-evaluator-hard-gates, p1-evaluator-llm-scoring, p1-experiment-tracker,
p1-global-cap, p1-head-change-apply, p1-heartbeats, p1-interfaces,
p1-judge-hardening, p1-reserved-path, p1-run-budget, p1-se-integration,
p1-strategy-orchestrator, p1-test-backoff, p1-test-bad-judge-json,
p1-test-binary-patch, p1-test-head-change, p1-test-reserved-path,
p1-test-symlink-escape, p1-trailer-schema, p1-worktree-manager, p6-docs,
rd-post-p1, p2-mcp-server, p2-mcp-config-writer, p2-per-candidate-mcp-config,
p2-server-lifetime, p2-test-mcp-race, p2-cli-mcp-bridge

## SQL rehydration script

The new session can re-create todos with one query (paste into the new session's
SQL tool):

```sql
-- truncate first if needed
DELETE FROM todo_deps; DELETE FROM todos;

-- bulk insert (status preserved)
INSERT INTO todos (id, title, status) VALUES
 ('p0-config-model','Add StrategyFrameworkConfig POCO','done'),
 ('p0-gitignore','Add .candidates/ and experiment-data/ to .gitignore','done'),
 ('p0-register-di','Register services in Runner/Program.cs','done'),
 ('p0-tests-project','Add AgentSquad.StrategyFramework.Tests xUnit project','done'),
 ('p1-baseline-strategy','Implement BaselineStrategy','done'),
 ('p1-contract-freeze','Freeze SignalR + REST contracts','done'),
 ('p1-evaluator-hard-gates','Implement CandidateEvaluator hard gates','done'),
 ('p1-experiment-tracker','Implement ExperimentTracker','done'),
 ('p1-global-cap','Global weighted cap + 429/backoff/degrade','done'),
 ('p1-head-change-apply','Winner-apply with PR-head detection','done'),
 ('p1-heartbeats','Orchestrator status heartbeats','done'),
 ('p1-interfaces','Define core strategy interfaces and records','done'),
 ('p1-judge-hardening','LLM judge hardening','done'),
 ('p1-reserved-path','Reserved evaluator path prefix','done'),
 ('p1-run-budget','Per-run token/request budget (Phase 1)','done'),
 ('p1-strategy-orchestrator','Implement StrategyOrchestrator','done'),
 ('p1-trailer-schema','Scalar-only trailers + record id','done'),
 ('p1-worktree-manager','Implement GitWorktreeManager','done'),
 ('p6-docs','Update README with enable/disable instructions','done'),
 ('p1-baseline-contract','Baseline regression = stubbed-parity + behavioral','done'),
 ('p1-evaluator-llm-scoring','Implement LLM scoring for survivors','done'),
 ('p1-se-integration','Wire StrategyOrchestrator into SoftwareEngineerAgent','done'),
 ('p1-test-backoff','Test: 429 triggers backoff and degrade','done'),
 ('p1-test-bad-judge-json','Test: malformed judge output handled deterministically','done'),
 ('p1-test-binary-patch','Test: winner apply handles binary/rename/delete patches','done'),
 ('p1-test-head-change','Test: PR head changed mid-flight','done'),
 ('p1-test-reserved-path','Test: candidate touching reserved evaluator path fails Gate2','done'),
 ('p1-test-symlink-escape','Test: symlink/junction/absolute-path escape rejected','done'),
 ('p2-mcp-config-writer','Generate per-candidate MCP config','done'),
 ('p2-mcp-server','Implement WorkspaceReaderMcpServer','done'),
 ('p2-mcp-strategy','Implement McpEnhancedStrategy','done'),
 ('p2-per-candidate-mcp-config','Per-candidate scoped MCP config','done'),
 ('p2-server-lifetime','MCP server lifetime tests','done'),
 ('p2-test-mcp-race','Test: concurrent MCP configs do not cross-contaminate','done'),
 ('p2-cli-mcp-bridge','Plumb --mcp-config through CopilotCliProcessManager + relax no-tools prompt','done'),
 ('p3-agentic-strategy','Implement AgenticDelegationStrategy','pending'),
 ('p3-agentic-watchdog','Extend CliInteractiveWatchdog for agentic mode','pending'),
 ('p3-process-manager-ext','Extend CopilotCliProcessManager with agentic session method','pending'),
 ('p3-real-sandbox','Real sandbox for agentic','pending'),
 ('p3-sandbox-hardening','File-escape detection and push blocking','pending'),
 ('p3-semaphore-split','Split semaphores into three pools','pending'),
 ('p3-test-orphan-cleanup','Test: agentic crash leaves no orphaned worktree/process','pending'),
 ('p4-candidate-state-api','REST endpoints for candidate state','pending'),
 ('p4-dashboard-page','Blazor /strategies page with side-by-side cards','pending'),
 ('p4-dashboard-stubs','Standalone dashboard HTTP proxies for new endpoints','pending'),
 ('p4-signalr-events','SignalR events for candidate lifecycle','pending'),
 ('p5-adaptive-selector','Implement AdaptiveStrategySelector','pending'),
 ('p5-cost-budget','Per-run token budget circuit breaker','pending'),
 ('p5-sampling-policies','Implement non-always sampling policies','pending'),
 ('p6-telemetry','Per-strategy cost attribution in AgentUsageTracker','pending'),
 ('rd-post-p1','Rubber-duck after Phase 1 design','done'),
 ('rd-post-p3','Rubber-duck after Phase 3 agentic work','pending'),
 ('val-e2e','End-to-end live pipeline run','pending'),
 ('val-integration','Run integration tests','pending'),
 ('val-success-criteria','Validate success criteria from doc','pending'),
 ('val-unit-tests','Run unit tests','pending');

INSERT INTO todo_deps (todo_id, depends_on) VALUES
 ('p0-config-model','p0-tests-project'),
 ('p0-register-di','p0-config-model'),
 ('p1-interfaces','p0-register-di'),
 ('p1-contract-freeze','p1-interfaces'),
 ('p1-experiment-tracker','p1-interfaces'),
 ('p1-global-cap','p1-interfaces'),
 ('p1-worktree-manager','p1-interfaces'),
 ('p1-strategy-orchestrator','p1-worktree-manager'),
 ('p1-baseline-strategy','p1-strategy-orchestrator'),
 ('p1-evaluator-hard-gates','p1-strategy-orchestrator'),
 ('p1-heartbeats','p1-strategy-orchestrator'),
 ('p1-reserved-path','p1-evaluator-hard-gates'),
 ('p1-run-budget','p1-global-cap'),
 ('p1-baseline-contract','p1-baseline-strategy'),
 ('p1-evaluator-llm-scoring','p1-evaluator-hard-gates'),
 ('p1-judge-hardening','p1-evaluator-llm-scoring'),
 ('p1-se-integration','p1-baseline-strategy'),
 ('p1-se-integration','p1-evaluator-llm-scoring'),
 ('p1-se-integration','p1-experiment-tracker'),
 ('p1-head-change-apply','p1-se-integration'),
 ('p1-trailer-schema','p1-se-integration'),
 ('p1-test-backoff','p1-global-cap'),
 ('p1-test-bad-judge-json','p1-judge-hardening'),
 ('p1-test-binary-patch','p1-se-integration'),
 ('p1-test-head-change','p1-head-change-apply'),
 ('p1-test-reserved-path','p1-reserved-path'),
 ('p1-test-symlink-escape','p1-worktree-manager'),
 ('rd-post-p1','p1-se-integration'),
 ('p2-mcp-server','rd-post-p1'),
 ('p2-mcp-config-writer','p2-mcp-server'),
 ('p2-per-candidate-mcp-config','p2-mcp-config-writer'),
 ('p2-cli-mcp-bridge','p2-mcp-server'),
 ('p2-cli-mcp-bridge','p2-mcp-config-writer'),
 ('p2-mcp-strategy','p2-cli-mcp-bridge'),
 ('p2-server-lifetime','p2-mcp-strategy'),
 ('p2-test-mcp-race','p2-per-candidate-mcp-config'),
 ('p3-process-manager-ext','rd-post-p1'),
 ('p3-agentic-watchdog','p3-process-manager-ext'),
 ('p3-semaphore-split','p3-process-manager-ext'),
 ('p3-agentic-strategy','p3-agentic-watchdog'),
 ('p3-agentic-strategy','p3-semaphore-split'),
 ('p3-real-sandbox','p3-agentic-strategy'),
 ('p3-sandbox-hardening','p3-agentic-strategy'),
 ('p3-test-orphan-cleanup','p3-real-sandbox'),
 ('rd-post-p3','p3-sandbox-hardening'),
 ('p4-candidate-state-api','p1-se-integration'),
 ('p4-signalr-events','p4-candidate-state-api'),
 ('p4-dashboard-stubs','p4-candidate-state-api'),
 ('p4-dashboard-page','p4-signalr-events'),
 ('val-unit-tests','p2-server-lifetime'),
 ('val-unit-tests','p5-cost-budget'),
 ('val-integration','val-unit-tests'),
 ('val-e2e','p4-dashboard-page'),
 ('val-e2e','val-integration'),
 ('val-success-criteria','val-e2e'),
 ('p5-adaptive-selector','val-e2e'),
 ('p5-cost-budget','p3-agentic-strategy'),
 ('p5-sampling-policies','p1-se-integration'),
 ('p6-telemetry','p3-agentic-strategy'),
 ('p6-docs','p5-adaptive-selector');
```
