---
version: "1.0"
description: "User prompt for fixing build errors in test files (inline test path)"
variables:
  - attempt_number
  - max_retries
  - error_summary
  - project_context
  - test_files_context
tags:
  - test-engineer
  - build-fix
---
Build attempt {{attempt_number}}/{{max_retries}} failed.

## Build Errors

{{error_summary}}

{{project_context}}
## Test Files

{{test_files_context}}

Fix ALL build errors. Only modify test files.
