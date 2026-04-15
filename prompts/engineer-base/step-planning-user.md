---
version: "1.0"
description: "User prompt for step planning - provides issue, PR, architecture, and PM spec context"
variables:
  - issue_number
  - issue_title
  - issue_body
  - pr_body
  - architecture
  - pm_spec
tags:
  - engineer
  - engineer-base
  - planning
---
## Issue #{{issue_number}}: {{issue_title}}
{{issue_body}}

## PR Description
{{pr_body}}

## Architecture
{{architecture}}

## PM Specification
{{pm_spec}}
