---
version: "1.0"
description: "Custom agent issue work product user prompt"
variables:
  - issue_number
  - issue_title
  - issue_body
  - project_context
tags:
  - custom
  - issue
---
## Issue #{{issue_number}}: {{issue_title}}

{{issue_body}}

## Project Context
{{project_context}}

Analyze this issue and produce your work product. Include all necessary detail.
