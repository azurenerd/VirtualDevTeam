---
version: "1.0"
description: "User prompt for CLI edit mode rework - uses native edit tools instead of FILE: blocks"
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
  - cli-edit
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
1. Start with a brief CHANGES SUMMARY addressing each numbered feedback item
2. Use your view tool to read the files mentioned in the feedback
3. Use your edit tool to make ONLY the specific changes needed — do NOT rewrite entire files
4. Do NOT touch files that weren't mentioned in the feedback
5. If asked to remove a file, delete it
6. If asked to add a new file, use your create tool
7. Focus on minimal, surgical changes — your diff should be as small as possible
