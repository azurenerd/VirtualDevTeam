# Remaining Todos — VDT Session 2026-05-16

## 3 Pending Feature Todos

### 1. Parallel MCP + C# Playwright Dual Capture (`ai-nav-dual-strategy`)
Run both screenshot strategies simultaneously on the same worktree app:
- **Strategy A (Agent + Playwright MCP):** Copilot CLI takes screenshots directly via MCP tools, adapts to blank/JSON/loading
- **Strategy B (C# Playwright):** Fast deterministic fallback, captures discovered URLs
- Both are READ-ONLY (no CRUD mutations) — safe to run in parallel
- Show both artifacts in Strategies page with colored borders (🟣 MCP, 🔵 C# Playwright)
- CRUD tasks → MCP only (no competing mutations)

### 2. Chrome DevTools MCP Integration (`ai-nav-chrome-devtools`)
- Use Chrome DevTools MCP (https://developer.chrome.com/blog/chrome-devtools-mcp) for page analysis
- Detect web UI vs API-only projects
- Inspect network requests, console errors
- Understand page structure (SPA, SSR, static)

### 3. Screenshot Capture Metrics (`ai-nav-metrics`)
- Track tool calls used (out of 80 budget) per candidate
- Pages discovered by MCP agent vs C# Playwright
- Artifact count per strategy source
- Show in Strategies page details/metrics tab
- Show tested URLs under media thumbnails (expandable/popup)

## Context
- 24 commits pushed to `behumphr` this session
- All API reduction fixes implemented (SE cache, FlowMonitor tick, PR review cache, throttle notifications)
- AI-driven screenshot navigation prompt already updated (hints + acceptance criteria)
- Plan designed by 4-model consensus (GPT-5.5, Sonnet, Opus 4.7, Opus 4.6)
