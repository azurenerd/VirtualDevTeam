---
version: "1.0"
description: "User prompt for blocker triage"
variables:
  - blocker_number
  - blocker_title
  - blocker_body
tags:
  - pm
  - blocker
---
Blocker Issue #{{blocker_number}}: {{blocker_title}}

{{blocker_body}}
