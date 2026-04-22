# Strategy Framework (Experimental)

> Multi-strategy code generation A/B/C testing framework. Implements the spec in
> [`InteractiveCLIPlan.md`](./InteractiveCLIPlan.md). **Phase 1 wired into the SE agent
> behind an opt-in feature flag** (default OFF until baseline-contract lands). Safe-by-default.

## Status

| Phase | Scope | Status |
|-------|-------|--------|
| 0 | Scaffolding, config, DI | ✅ Done |
| 1 | Core foundation (orchestrator, evaluator, worktree, tracker, budget, apply) + SE integration + real baseline | ✅ Done |
| 2 | MCP-enhanced strategy | ✅ Done |
| 3 | Agentic delegation + sandbox | ✅ Done (process-level containment; OS-level is follow-up) |
| 4 | Dashboard `/strategies` page | ✅ Done |
| 5 | Adaptive selector + sampling + budget | ✅ Done (adaptive selector feature-flagged off until `val-e2e` data exists) |
| 6 | Per-strategy cost attribution in `AgentUsageTracker` | ✅ Done |
| val-e2e | End-to-end live-pipeline run against real `copilot` binary | ✅ Done. Run `20260419T231321Z` (2 strategies): baseline won by llm-rank, winner applied to PR #1931. Run `20260420T021841Z` (3 strategies, all parallel): baseline timeout@180s, mcp-enhanced gate2-build env fail, agentic-delegation gate1 empty-patch but **CLI ran 137 tool calls in 445s — first successful agentic session, bug #3 VALIDATED.** Commit `7bf9f72` ships all 3 val-e2e bug fixes. |
| val-success-criteria | Validate success criteria from the original design doc | ⚠️ Partial — framework stability (#4) and dashboard visibility (#5) demonstrated; parallel 3-strategy execution confirmed via `StrategyOrchestrator.Task.WhenAll`; strategy-outperformance (#1) needs N≥10 runs with winners applied; agentic (#2) NOW RUNS but empties patch (separate follow-up: `agentic-empty-patch`); CLI provider reports 0 tokens so cost premium (#3) can't be measured until API-key fallback path is exercised |
| agentic-empty-patch | Follow-up: agentic CLI runs 137 tool calls over 445s but writes 0 files to worktree. Either CLI is operating read-only, writing outside worktree, or the prompt convinces it there's nothing to do. Needs investigation of `ExecuteAgenticSessionAsync` sandbox + prompt shape. | 🔬 Follow-up |

The live SE pipeline now calls `StrategyOrchestrator.RunCandidatesAsync` from
`SoftwareEngineerAgent.WorkOnOwnTasksAsync` after PR creation, but **only when
`AgentSquad:StrategyFramework:Enabled` is `true`** (default `false`). The
`baseline` strategy delegates to `BaselineCodeGenerator` (in `AgentSquad.Agents`),
which mirrors the SE single-pass code-gen path: same prompts (via shared
`SinglePassPromptBuilder`), same model tier, same FILE: marker parser. Path
containment is enforced before any write — outputs that resolve outside the
candidate worktree are rejected. The orchestrator's evaluator gates each
candidate with a build, then the SE applies the winning patch and re-builds
before pushing. On any guard failure — no winner, build failure, head moved,
exception — the agent falls back to the legacy code-gen path. No existing
behavior changes with the default config.

## Configuration

```jsonc
// appsettings.json — all optional; defaults shown
"AgentSquad": {
  "StrategyFramework": {
    "Enabled": false,
    "EnabledStrategies": [ "baseline", "mcp-enhanced" ],   // agentic off by default
    "SamplingPolicy": "always",
    "PostWinnerFlow": "full-review",
    "Timeouts": { "BaselineSeconds": 180, "McpSeconds": 300, "AgenticSeconds": 600 },
    "Concurrency": {
      "GlobalMaxConcurrentProcesses": 6,
      "SingleShotPool": 4, "CandidatePool": 3, "AgenticPool": 2
    },
    "Budget": {
      "MaxTokensPerRun": 2000000,
      "AgenticMinReserve": 60000,
      "McpMinReserve": 30000
    },
    "Evaluator": {
      "ReservedPathPrefix": "tests/.evaluator-reserved/",
      "MaxJudgePatchChars": 40000,
      "SkipJudgeOnSingleSurvivor": true
    }
  }
}
```

### To opt in (Phase 1 with real baseline)

Set `AgentSquad:StrategyFramework:Enabled` to `true`. With only `baseline`
enabled (the default `EnabledStrategies` list filters `mcp-enhanced` out at
orchestrator-time since it's not yet implemented), the SE agent will run the
baseline candidate alongside legacy code-gen as a fallback. Baseline produces
real single-pass code matching the SE's existing prompt/parser; the orchestrator
build-gates it, the SE applies the winning patch, build-verifies again, then
pushes a commit with `Strategy: baseline` trailers.

### To opt in to GitHub Copilot CLI (trusted dev machines only)

GitHub Copilot CLI (`copilot-cli`, formerly `agentic-delegation`) runs `copilot --allow-all` against a
per-candidate git worktree. Phase 3 shipped **process-level containment** only:

- Windows Job Object reaps the process tree on any watchdog/timeout/cancel.
- `CopilotCliAgenticScope` redirects `HOME`/`USERPROFILE`/`XDG_CONFIG_HOME`/
  `APPDATA` into a per-session sandbox dir and scrubs a wide set of auth
  env vars (Git/GitHub, AI providers, AWS, GCP/Google, Azure, Docker, GCM,
  NPM/Yarn, PyPI, HF, GPG, Kubernetes). Scrub deletes rather than overwrites.
- `git config --worktree` isolates hardened keys per candidate, so concurrent
  candidates can't stomp each other's credential/push settings.
- `SandboxPostRunValidator` post-run checks: reparse-point scan of the worktree,
  host `~/.gitconfig` drift (both `USERPROFILE` and `HOME` candidates),
  sandbox-gitconfig integrity, and worktree `.git` pointer drift.
- Host-side patch extraction uses a hardened git invocation
  (`GitWorktreeManager.ExtractPatchAsync`) that disables `diff.external`,
  textconv, LFS filters, custom hooks, attributes file, mergetool drivers,
  and custom credential helpers — defending against a worktree whose `.git/
  config` or `.gitattributes` may have been tampered with during the session.

**What Phase 3 does NOT block**:

1. Network egress. A malicious agent can still `curl` / `Invoke-WebRequest`
   to exfiltrate anything it can see (though most secrets are scrubbed from
   env, so it can only exfil what's reachable via filesystem).
2. Reads of absolute paths the runner user can access (machine-wide cert
   stores, files in user home via raw API calls, etc.).
3. Out-of-worktree writes through absolute paths. The validator catches
   host-gitconfig drift but does not snapshot every sensitive file; an agent
   can still write to e.g. `%APPDATA%\SomeApp\config.json` if that path is
   reachable (Phase 3 redirects `APPDATA` into the sandbox, so this is
   mitigated for any process that inherits our env — but a child can still
   reset its own env vars before invoking other tools).
4. Human-targeted prompt-injection attacks (a crafted task that tricks the
   agent into producing benign-looking but malicious code).

**Real OS-level containment** (AppContainer, Docker, Hyper-V isolation) is an
explicit follow-up phase. Until then, treat `copilot-cli` as a
**trusted dev-machine** feature: only enable it on runners you're willing to
accept as fully compromised by the agent if the model or prompt misbehaves.

To enable:
1. Set `AgentSquad:StrategyFramework:Enabled = true` (master flag).
2. Add `"copilot-cli"` to `EnabledStrategies` (legacy `"agentic-delegation"` also accepted).
3. Review `StrategyFramework:Agentic.{StuckSeconds, ToolCallCap}` for your
   budget and timeout tolerances.

## Architecture

- **`GitWorktreeManager`** — creates a hardened git worktree at the task's base
  SHA under `.candidates/{taskId}/{strategyId}`. Scrubs `GIT_CONFIG_NOSYSTEM`,
  `GIT_TERMINAL_PROMPT`, clears credential helper, blocks hooks, neutralizes
  push URL.
- **`StrategyOrchestrator`** — launches all enabled strategies in parallel,
  gated by `StrategyConcurrencyGate`. Per-strategy timeout. Exceptions become
  failed results, not thrown. Worktree cleanup guaranteed in `finally`.
- **`CandidateEvaluator`** — hard gates (output produced → path safety →
  `git apply --3way --check` → `dotnet build`) followed by optional LLM judge
  (`ILlmJudge`). Tiebreakers locked: AC → Design → Readability → tokens → time
  → id.
- **`WinnerApplyService`** — re-checks branch HEAD against the expected base SHA
  before applying. If the PR branch has advanced mid-flight, refuses and
  signals `HeadChanged` so the caller can re-orchestrate.
- **`ExperimentTracker`** — NDJSON at `experiment-data/{runId}.ndjson`.
- **`RunBudgetTracker`** — per-run token counter with circuit breaker.
- **`StrategyTrailers`** — scalar-only commit trailer formatter; rejects
  multi-line values.

## Security properties

1. **Path safety**: `GitWorktreeManager.ValidatePatchPaths` rejects patches that
   touch `.git/`, use `../`, use absolute paths, or write under the evaluator's
   reserved-path prefix.
2. **Credential isolation**: each worktree's local git config is hardened
   (push URL → `file:///dev/null`, credential helper cleared). Env scrubbed
   (`GIT_CONFIG_NOSYSTEM=1`).
3. **Head-change safety**: winner application checks `git rev-parse <branch>`
   equals the orchestration base SHA. Mismatch → refuse apply.
4. **Judge prompt hardening**: `JudgeInputSanitizer` strips control characters
   and truncates patches at a configured cap before sending to an LLM judge.
5. **Budget circuit breaker**: `RunBudgetTracker` trips once per-run token cap
   is exceeded; further charges return `false`.

## Tests

- `AgentSquad.StrategyFramework.Tests` — 30 tests covering config defaults,
  path validation, experiment tracker, concurrency gate, run budget, trailer
  schema, judge sanitizer, and winner apply (head-changed + empty-patch paths).
  Includes a real-git integration test that exercises orchestrator +
  evaluator + worktree + tracker end-to-end against a disposable repo.

## Next steps

See the session plan for the full remaining todo list. The gating item for any
live end-to-end validation is **SE integration** (`p1-se-integration`):
refactoring `SoftwareEngineerAgent.WorkOnOwnTasksAsync` to generate into a
worktree and call the orchestrator behind a feature flag.
