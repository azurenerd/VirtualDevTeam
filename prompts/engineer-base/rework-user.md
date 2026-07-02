---
version: "1.0"
description: "User prompt for surgical rework - provides only flagged files and review feedback"
variables:
  - pr_number
  - pr_title
  - pr_body
  - architecture
  - pm_spec
  - additional_context
  - current_files_context
  - feedback
tags:
  - engineer
  - engineer-base
  - rework
---
## PR #{{pr_number}}: {{pr_title}}

## Review Feedback (Address ALL items below)
{{feedback}}

{{current_files_context}}## Context Summary
- **Architecture approach:** {{architecture}}
- **PM Spec goals:** {{pm_spec}}
{{additional_context}}
## Original PR Description
{{pr_body}}

SURGICAL REWORK INSTRUCTIONS:
1. Start with CHANGES SUMMARY addressing each numbered feedback item
2. Output ONLY files that need modification using FILE: format
3. Each FILE: block must contain the COMPLETE file content
4. Do NOT regenerate files that weren't mentioned in the feedback
5. If asked to remove a file, omit it entirely
6. If asked to add a new file, include it with FILE: format
