---
version: "1.0"
description: "User prompt for story completion review"
variables:
  - issue_number
  - issue_title
  - issue_body
  - closed_summary
tags:
  - pm
  - review
---
## Enhancement Issue #{{issue_number}}: {{issue_title}}

### Original Specification
{{issue_body}}

### Completed Engineering Tasks
{{closed_summary}}

Review the acceptance criteria above. Are all criteria addressed by the completed tasks? Start your response with either APPROVED or NEEDS_MORE_WORK.
