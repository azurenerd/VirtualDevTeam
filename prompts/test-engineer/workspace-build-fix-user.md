---
version: "1.0"
description: "User prompt for fixing build errors in test files (workspace flow)"
variables:
  - attempt_number
  - max_retries
  - error_summary
  - test_file_list
tags:
  - test-engineer
  - build-fix
  - workspace
---
Build attempt {{attempt_number}}/{{max_retries}} failed.

## Build Errors

{{error_summary}}

## Test files currently written

{{test_file_list}}

Fix ALL errors. If duplicate type definitions exist, remove those files entirely and use project references. If namespace errors occur, check the actual project namespace and fix the import statements.
