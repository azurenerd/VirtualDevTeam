---
version: "1.0"
description: "User prompt for engineer clarification questions"
variables:
  - pm_spec
  - issue_number
  - issue_title
  - issue_body
  - comments_context
  - question
tags:
  - pm
  - clarification
---
## PM Specification
{{pm_spec}}

## Issue #{{issue_number}}: {{issue_title}}
{{issue_body}}{{comments_context}}

## Engineer's Question
{{question}}
