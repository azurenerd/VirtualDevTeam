---
version: "1.0"
description: "Test failure fix prompt"
variables:
  - step_number
  - total_steps
  - step_description
  - failed_count
  - total_count
  - failure_summary
tags:
  - engineer-base
  - test-fix
---
The code from step {{step_number}}/{{total_steps}} ({{step_description}}) has test failures.

TEST FAILURES ({{failed_count}} of {{total_count}}):
{{failure_summary}}

Fix the code so all tests pass. Output ONLY the corrected files using this format:
FILE: path/to/file.ext
```language
<complete corrected file content>
```

Include the COMPLETE file content for each file that needs changes.
Do NOT modify the test files unless the tests themselves are wrong.
