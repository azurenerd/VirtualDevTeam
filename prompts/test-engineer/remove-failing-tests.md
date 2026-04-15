---
version: "1.0"
description: "Prompt for removing unfixable failing tests while preserving passing ones"
variables:
  - tier
  - max_retries
  - failure_summary
tags:
  - test-engineer
  - removal
---
The following {{tier}} tests have been failing despite {{max_retries}} attempts to fix them.
These tests MUST be removed because they cannot be made to pass.

FAILING TESTS:
{{failure_summary}}

For each failing test:
1. REMOVE the failing test method entirely
2. Add a comment at the location where it was removed:
   // TEST REMOVED: [TestMethodName] - Could not be resolved after {{max_retries}} fix attempts.
   // Reason: [brief description of the failure]
   // This test should be revisited when the underlying issue is resolved.
3. Keep ALL passing tests intact — do not remove or modify them

Output ONLY the updated test files using this format:
FILE: path/to/test/file.ext
```language
<complete updated file content with failing tests removed>
```

Include the COMPLETE file content for each test file that needs changes.
Ensure the remaining code still compiles after removal.
