---
version: "1.0"
description: "Screenshot validation section for PR review (when screenshots are present)"
variables: []
tags:
  - pm
  - review
  - screenshots
---
3. VISUAL VALIDATION: Screenshots have been posted on this PR (by engineers and/or Test Engineer). You can SEE these screenshots embedded in this message. Review them carefully to verify the app renders correctly:
   - Does the screenshot show a working app (no error pages, no unhandled exceptions, no blank screens)?
   - Does the visual output match what the PR description and acceptance criteria say it should do?
   - If the screenshot shows an error page, a white/blank screen with errors, or broken UI, this is a REQUEST_CHANGES issue.
   - Look for error messages, stack traces, JSON errors, or 'data.json' failures — these indicate a broken build.
