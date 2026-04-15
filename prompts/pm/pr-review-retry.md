---
version: "1.0"
description: "Retry prompt when AI returns garbage response during PR review"
variables: []
tags:
  - pm
  - review
  - retry
---
That response was not a requirements review. Check the PR against the acceptance criteria.
Output ONLY a numbered list of unmet requirements, or 'Requirements met' if acceptable.
End with VERDICT: APPROVE or VERDICT: REQUEST_CHANGES
