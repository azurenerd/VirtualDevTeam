---
version: "1.0"
description: "User prompt for reworking tests with reviewer feedback and current file context"
variables:
  - pr_number
  - pr_title
  - pr_description
  - current_files_context
  - reviewer
  - feedback
tags:
  - test-engineer
  - rework
---
## Test PR #{{pr_number}}: {{pr_title}}

## Original PR Description
{{pr_description}}

{{current_files_context}}

## Review Feedback from {{reviewer}}
{{feedback}}

REQUIRED: Start your response with CHANGES SUMMARY that addresses each numbered feedback item using the SAME numbers. Example:
CHANGES SUMMARY
1. Added missing error handling test as requested
2. Fixed assertion to check return type

Then output the corrected test files.
