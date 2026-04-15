---
version: "1.0"
description: "User prompt for classifying test failures with failure details and source code"
variables:
  - failure_summary
  - source_context
tags:
  - test-engineer
  - classification
---
## Failing Tests
{{failure_summary}}

## Source Code Under Test
{{source_context}}
