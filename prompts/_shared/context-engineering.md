---
description: Token-budget guidance for engineering tasks — what to read, how deep, when to stop
tags:
  - shared
  - context
---

## Reading discipline

An agent that reads everything wastes tokens and loses focus. An agent that reads nothing hallucinates APIs and misses conventions. The skill is finding the middle for the specific task you have.

### Always read these first

1. **Project rules** — `.github/copilot-instructions.md` or the equivalent agent guide for this repository (typically already in your context window).
2. **The Issue body in full** — including the acceptance criteria list and the File Plan.
3. **Architecture.md and PMSpec.md** — at least the sections relevant to this task. Skim, don't deep-read; come back if you discover you need more later.
4. **Every file your File Plan modifies** — read each one in full before touching it. Don't guess what's there.

### Read on demand only

- A neighbor file — when your task imports from it or extends a pattern in it. Read just enough to copy the pattern.
- A test file — when you change behavior, find the test that covers it. If none exists, plan to add one with your change.
- Project config (`*.csproj`, `package.json`, `tsconfig.json`, etc.) — only when you're adding a dependency or adjusting a build setting.

### Don't read these unless explicitly cited

- Files belonging to sibling tasks. They are not yours; touching them creates merge conflicts and breaks integrations for the engineer who owns them.
- Generated artifacts — `bin/`, `obj/`, `dist/`, `build/`, `*.g.cs`, lockfiles. Never read or modify these by hand.
- Documentation unrelated to your slice — README sections on deployment, contribution, security policy, release process, etc.
- Old commits or unrelated PRs — unless you're explicitly debugging a regression introduced recently.

### When to stop loading context

If you've read more than five files and you're still unsure what to write, the bottleneck is probably **scope ambiguity** rather than **missing context**. Asking a clarifying question (or surfacing an assumption per `pre-pr-questions.md`) is cheaper than reading another ten files.

### Token-budget heuristics

- A typical engineering task should fit under roughly 30K tokens of read context. If you're past that, you're almost certainly reading too much.
- For each file you open, ask: "what one fact am I extracting from this?" If you can't name the fact, you're skimming aimlessly.
- Prefer the smallest file that demonstrates a pattern over the largest file that uses it.
