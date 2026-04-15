---
version: "1.0"
description: "User prompt for fixing failing test code"
variables:
  - tier
  - failed_count
  - total_count
  - failure_summary
tags:
  - test-engineer
  - test-fix
---
{{tier}} tests failed ({{failed_count}} of {{total_count}}):

{{failure_summary}}

Fix the test code. Output ONLY corrected files using:
FILE: path/to/file.ext
```language
<content>
```

Only fix test bugs — don't mask real code bugs.
