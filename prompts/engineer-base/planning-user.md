---
version: "1.0"
description: "User prompt for engineer task planning - provides PM spec, architecture, and issue context"
variables:
  - pm_spec
  - architecture
  - issue_number
  - issue_title
  - issue_body
tags:
  - engineer
  - engineer-base
  - planning
---
## PM Specification
{{pm_spec}}

## Architecture
{{architecture}}

## Issue #{{issue_number}}: {{issue_title}}
{{issue_body}}
