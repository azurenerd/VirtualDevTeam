---
version: "1.0"
description: "Screenshot validation section for PR review (when screenshots are present)"
variables: []
tags:
  - pm
  - review
  - screenshots
---
3. VISUAL VALIDATION: Screenshots have been posted on this PR by engineers and/or Test Engineer. Each screenshot is labeled with its source: [Engineer/Author] or [Test Engineer]. Review ALL of them carefully:
   - Does each screenshot show a working app (no error pages, no unhandled exceptions, no blank screens)?
   - Does the visual output match what the PR description and acceptance criteria say it should do?
   - **[Test Engineer] screenshots are CRITICAL** — they represent independent validation from a separate workspace clone. If a Test Engineer screenshot shows an error page, broken UI, 'data.json' failures, or any error message, you MUST REQUEST_CHANGES even if the author's screenshot looks correct. The Test Engineer's result is the ground truth for whether the app actually works.
   - If ANY screenshot shows an error page, a white/blank screen with errors, or broken UI, this is a REQUEST_CHANGES issue.
   - Look for error messages, stack traces, JSON errors, or 'data.json' failures — these indicate a broken build.
