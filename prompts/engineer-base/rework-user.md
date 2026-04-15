---
version: "1.0"
description: "User prompt for rework - provides PR context, specs, current files, and review feedback"
variables:
  - pr_number
  - pr_title
  - pr_body
  - architecture
  - pm_spec
  - additional_context
  - current_files_context
  - feedback
tags:
  - engineer
  - engineer-base
  - rework
---
## PR #{{pr_number}}: {{pr_title}}
## Original PR Description
{{pr_body}}

## Architecture
{{architecture}}

## PM Specification
{{pm_spec}}

{{additional_context}}{{current_files_context}}## Review Feedback
{{feedback}}

REQUIRED: Start your response with CHANGES SUMMARY that addresses each numbered feedback item using the SAME numbers. Example:
CHANGES SUMMARY
1. Fixed the null check in AuthController.cs
2. Added validation for empty strings as requested
3. No change needed — the test already covers this case

Then you MUST output the corrected files using this exact format:

FILE: path/to/file.ext
```language
<file content>
```

Include the COMPLETE content of each changed file. You MUST include at least one FILE: block — a summary alone is not sufficient.

SCOPE RULE: Only output files that are DIRECTLY affected by the feedback items. Do NOT regenerate the entire project (.sln, .csproj, Program.cs, CSS files, config files) unless the reviewer SPECIFICALLY requested changes to those files. If the reviewer says "remove this file from the PR," simply omit it from your output.
