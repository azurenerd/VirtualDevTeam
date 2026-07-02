---
description: Pre-merge performance checklist — catch obvious misses before they ship
tags:
  - shared
  - performance
---

## Performance checklist

This isn't a profiling guide. It's a list of obvious misses to catch before merge so they don't compound across releases.

### Data access

- [ ] **No N+1 patterns.** A loop that fetches per parent should be replaced with a join, eager loading, or a batched lookup.
- [ ] **List endpoints have a page size limit.** Default page size, maximum page size, and either offset or cursor pagination defined.
- [ ] **Filter and sort columns are indexed** if they appear on a hot path.
- [ ] **Don't `SELECT *` to grab three columns.** Don't load 1000 rows to display 20.

### Loops and data structures

- [ ] **No unbounded loops driven by user input.** Cap iteration count or input size.
- [ ] **Avoid O(n²) when O(n log n) is trivial.** Sort once instead of `Contains` inside a loop; use a `HashSet` / `Dictionary` for membership checks.
- [ ] **Don't re-iterate the same collection inside a loop body.**

### Async and IO

- [ ] **Network calls and disk IO are async.** Sync calls on async stacks deadlock or starve threads.
- [ ] **No `.Result` or `.Wait()` on hot paths.**
- [ ] **No fan-out of 100 parallel API calls without rate limiting** or a batched alternative.

### UI and rendering

- [ ] **Lists with more than ~50 items use virtualization or pagination.**
- [ ] **Components don't re-render the whole tree on every keystroke.** Memoize results; use stable `key` props correctly.
- [ ] **Images are right-sized.** No 4MB hero images; serve appropriate dimensions.
- [ ] **Don't fetch on every render.** Use stable cache keys for any data hooks.

### Memory

- [ ] **Event listeners are removed on cleanup or disposal.**
- [ ] **Closures don't accidentally capture large objects.**
- [ ] **Stream large responses** (>1MB) instead of buffering them in memory.

### Output

If you're reviewing: tag each violation as `[Perf #N]` with `<file>:<line>` and a concrete fix.
If you're self-assessing: any violation is a "must simplify before publishing" finding.

If your task ships a static site, a local-only feature, or a small-data-volume slice, most of the data-access rows above are inapplicable — focus on the UI/rendering and memory rows.
