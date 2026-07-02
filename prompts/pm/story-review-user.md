---
version: "1.1"
description: "User prompt for story completion review"
variables:
  - issue_number
  - issue_title
  - issue_body
  - closed_summary
  - evidence
tags:
  - pm
  - review
---
## Enhancement Issue #{{issue_number}}: {{issue_title}}

### Original Specification
{{issue_body}}

### Completed Engineering Tasks
{{closed_summary}}
{{evidence}}

Review the acceptance criteria above against the **verified repository state** in the Evidence section.

CRITICAL — your decision rules:
- Base your verdict on the file paths and merged PRs shown in the Evidence section. They are the source of truth — not your assumptions about what "should" be there.
- If the evidence shows files matching the acceptance criteria's deliverables, respond APPROVED. Cite the specific files you observed.
- If specific deliverables are missing from the file tree, respond NEEDS_MORE_WORK and list ONLY the gaps you can verify from the evidence (do not invent missing files).
- If the Evidence section is empty or absent (e.g., transient API failure when gathering it), respond NEEDS_MORE_WORK with the message "Could not verify repository state — defer to next review cycle". Do NOT conclude "nothing was done" from missing evidence.

Start your response with either APPROVED or NEEDS_MORE_WORK.