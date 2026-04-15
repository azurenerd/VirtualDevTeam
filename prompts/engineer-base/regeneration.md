---
version: "1.0"
description: "Regenerate code from scratch after repeated build failures"
variables:
  - failed_file_list
  - step_description
tags:
  - engineer-base
  - regeneration
---
The previous implementation had persistent build errors that could not be fixed. You need to regenerate the code from scratch with a different approach.

The following files had issues: {{failed_file_list}}

Requirements for this step:
{{step_description}}

IMPORTANT:
- Generate a COMPLETE, FRESH implementation — do not try to patch the previous code
- Ensure all interfaces match their implementations exactly
- Ensure all referenced types, namespaces, and dependencies exist
- Double-check method signatures match across interface/class boundaries
- Include ALL necessary using statements

Output each file using this format:
FILE: path/to/file.ext
```language
<complete file content>
```
