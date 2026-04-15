---
version: "1.0"
description: "Build error fix prompt"
variables:
  - step_number
  - total_steps
  - step_description
  - error_count
  - error_summary
tags:
  - engineer-base
  - build-fix
---
The code from step {{step_number}}/{{total_steps}} ({{step_description}}) has build errors.

BUILD ERRORS ({{error_count}}):
{{error_summary}}

Fix the code so it compiles. Output ONLY the corrected files using this format:
FILE: path/to/file.ext
```language
<complete corrected file content>
```

Include the COMPLETE file content for each file that needs changes.
