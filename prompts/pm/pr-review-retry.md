---
version: "1.0"
description: "Retry prompt when AI returns garbage response during PR review"
variables: []
tags:
  - pm
  - review
  - retry
---
That response was not a requirements review. Check the PR against its declared purpose and linked issue deliverables. For feature PRs, check acceptance criteria. For T-FINAL / Final Integration report PRs, assess report quality and accuracy — do NOT request changes because the report identifies remaining product gaps.
Output ONLY a numbered list of unmet requirements, or 'Requirements met' if acceptable.
End with VERDICT: APPROVE or VERDICT: REQUEST_CHANGES
