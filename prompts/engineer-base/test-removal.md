---
version: "1.0"
description: "Remove unfixable failing tests"
variables:
  - max_retries
  - failure_details
tags:
  - engineer-base
  - test-removal
---
The following test failures could not be fixed after {{max_retries}} attempts. Remove ONLY the failing tests while keeping all passing tests intact.

FAILING TESTS:
{{failure_details}}

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
